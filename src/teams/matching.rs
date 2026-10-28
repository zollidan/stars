//! Матчер справочника: пересчёт UTC-дня из очереди и автоподтверждение связей.
//!
//! День обрабатывается одной транзакцией под блокировкой справочника:
//! приём событий не может изменить данные посреди расчёта.

use std::collections::{BTreeMap, BTreeSet};

use chrono::{NaiveDate, TimeDelta, Utc};
use serde::Deserialize;
use serde_json::{Value, json};
use sqlx::{PgConnection, PgPool};

use super::repository::{self, FixtureRow};
use super::schemas::ProposalStatus;
use crate::matcher::{Assignment, Fixture, Pair, Team, WINDOW_US, assign};
use crate::normalize::normalize_team_name;

pub const API_SOURCE: &str = "api-sport-ru";

/// Пороги однозначности улики для автоподтверждения.
const MAX_TIME_DIFF_HOURS: f64 = 0.5;
const MIN_CANDIDATE_MARGIN: f64 = 5.0;
const MIN_ORIENTATION_MARGIN: f64 = 10.0;

pub struct DayStats {
    pub matches: usize,
    pub evidence: usize,
    pub confirmed: usize,
}

/// `None` — справочник занят приёмом событий; дата останется в очереди.
pub async fn process_day(db: &PgPool, day: NaiveDate) -> sqlx::Result<Option<DayStats>> {
    let start = day.and_time(chrono::NaiveTime::MIN).and_utc();
    let end = start + TimeDelta::days(1);
    let window = TimeDelta::microseconds(WINDOW_US);
    let mut tx = db.begin().await?;
    if !repository::try_guard_team_directory(&mut tx).await? {
        return Ok(None);
    }
    // Матчи api соседних суток конкурируют за события у границы дня.
    let (api_rows, book_rows): (Vec<_>, Vec<_>) =
        repository::fixtures_between(&mut tx, start - window * 2, end + window * 2)
            .await?
            .into_iter()
            .partition(|row| row.source == API_SOURCE);
    let api_rows: Vec<FixtureRow> = api_rows
        .into_iter()
        .filter(|row| row.kickoff >= start - window && row.kickoff < end + window)
        .collect();
    let in_day = |row: &FixtureRow| row.kickoff >= start && row.kickoff < end;
    let day_ids: Vec<i32> = api_rows
        .iter()
        .filter(|row| in_day(row))
        .filter_map(|row| row.event_id.parse().ok())
        .collect();
    repository::invalidate_match_evidence(&mut tx, &day_ids).await?;

    let matches: Vec<Fixture> = api_rows.iter().map(fixture).collect();
    let mut sources: BTreeMap<&str, Vec<&FixtureRow>> = BTreeMap::new();
    for row in &book_rows {
        sources.entry(&row.source).or_default().push(row);
    }
    let mut touched = BTreeSet::new();
    let mut evidence = 0;
    for (source, events) in sources {
        let fixtures: Vec<Fixture> = events.iter().map(|row| fixture(row)).collect();
        for assignment in assign(&matches, &fixtures) {
            if !in_day(&api_rows[assignment.pair.api]) {
                continue;
            }
            let context = Context {
                source,
                api_rows: &api_rows,
                events: &events,
                assignment: &assignment,
            };
            for side in [Side::Home, Side::Away] {
                if let Some(proposal_id) = save_side(&mut tx, &context, side).await? {
                    touched.insert(proposal_id);
                    evidence += 1;
                }
            }
        }
    }
    // Снимаем дату до распространения: новая связь может вернуть её в очередь.
    repository::acknowledge_day(&mut tx, day).await?;
    let mut linked = Vec::new();
    for proposal_id in touched {
        if let Some(pair) = auto_confirm(&mut tx, proposal_id).await? {
            linked.extend(pair);
        }
    }
    if !linked.is_empty() {
        // Связанный соперник подтверждает другие матчи этих команд: пересчитываем их даты.
        let sweep = sweep_days();
        let from = sweep[0].and_time(chrono::NaiveTime::MIN).and_utc();
        let to = from + TimeDelta::days(sweep.len() as i64);
        repository::enqueue_days_of_source_teams(&mut tx, &linked, from, to).await?;
    }
    let confirmed = linked.len() / 2;
    tx.commit().await?;
    Ok(Some(DayStats {
        matches: day_ids.len(),
        evidence,
        confirmed,
    }))
}

fn fixture(row: &FixtureRow) -> Fixture {
    Fixture {
        kickoff_us: row.kickoff.timestamp_micros(),
        home: Team::new(normalize_team_name(&row.home), row.home_team_id),
        away: Team::new(normalize_team_name(&row.away), row.away_team_id),
    }
}

/// ID событий в уликах — числа, как их присылают источники.
fn id_value(id: &str) -> Value {
    id.parse::<i64>().map_or_else(|_| json!(id), |id| json!(id))
}

#[derive(Clone, Copy)]
enum Side {
    Home,
    Away,
}

struct Context<'a> {
    source: &'a str,
    api_rows: &'a [FixtureRow],
    events: &'a [&'a FixtureRow],
    assignment: &'a Assignment,
}

impl Context<'_> {
    fn alternative(&self, pair: &Pair) -> Value {
        let (api, book) = (&self.api_rows[pair.api], self.events[pair.event]);
        json!({
            "api_match_id": id_value(&api.event_id),
            "bookmaker_event_id": id_value(&book.event_id),
            "api_home": api.home,
            "api_away": api.away,
            "bookmaker_home": book.home,
            "bookmaker_away": book.away,
            "score": pair.score,
        })
    }
}

/// (команда источника, общая команда) стороны события.
fn side_of(row: &FixtureRow, home: bool) -> (Option<i32>, Option<i32>) {
    if home {
        (row.home_source_id, row.home_team_id)
    } else {
        (row.away_source_id, row.away_team_id)
    }
}

/// Улика «команда api ↔ команда букмекера» по одной стороне назначенного матча.
async fn save_side(
    db: &mut PgConnection,
    context: &Context<'_>,
    side: Side,
) -> sqlx::Result<Option<i32>> {
    let pair = &context.assignment.pair;
    if pair.orientation_margin <= 0.0 {
        return Ok(None);
    }
    let (api, book) = (&context.api_rows[pair.api], context.events[pair.event]);
    let api_home = matches!(side, Side::Home);
    // При перестановке хозяева api — гости у букмекера.
    let book_home = api_home != pair.swapped;
    let (api_source, api_team) = side_of(api, api_home);
    let (book_source, book_team) = side_of(book, book_home);
    let (Some(api_source), Some(book_source)) = (api_source, book_source) else {
        return Ok(None);
    };
    if api_team.is_some() && api_team == book_team {
        return Ok(None);
    }
    let (_, api_opponent) = side_of(api, !api_home);
    let (_, book_opponent) = side_of(book, !book_home);
    let (side_name, score) = match side {
        Side::Home => ("home", pair.home_score),
        Side::Away => ("away", pair.away_score),
    };
    let alternative_score = context.assignment.alternative_score();
    let details = json!({
        "api_match_id": id_value(&api.event_id),
        "bookmaker_event_id": id_value(&book.event_id),
        "source": context.source,
        "api_home": api.home,
        "api_away": api.away,
        "bookmaker_home": book.home,
        "bookmaker_away": book.away,
        "api_kickoff": api.kickoff.to_rfc3339(),
        "bookmaker_kickoff": book.kickoff.to_rfc3339(),
        "api_league": api.league,
        "bookmaker_league": book.league,
        "side": side_name,
        "method": "independent_match",
        "context_complete": true,
        "score": pair.score,
        "home_score": pair.home_score,
        "away_score": pair.away_score,
        "swapped": pair.swapped,
        "orientation_margin": pair.orientation_margin,
        "time_diff_hours": pair.time_diff_hours,
        "alternative_score": alternative_score,
        "candidate_margin": pair.score - alternative_score,
        "alternatives": context.assignment.alternatives.iter().map(|p| context.alternative(p)).collect::<Vec<_>>(),
        // Тот же уже связанный соперник в то же время — это тот же матч.
        "opponent_linked": api_opponent.is_some() && api_opponent == book_opponent,
    });
    let key = format!(
        "{}:{}:{}:{side_name}",
        context.source, api.event_id, book.event_id
    );
    let (proposal_id, _) =
        repository::save_proposal(db, api_source, book_source, &key, score, &details).await?;
    Ok(Some(proposal_id))
}

/// Поля улики, важные для решения; старые улики монолита без новых полей тоже читаются.
#[derive(Deserialize, Default)]
#[serde(default)]
struct Proof {
    method: String,
    context_complete: bool,
    evidence_score: f64,
    score: f64,
    time_diff_hours: Option<f64>,
    candidate_margin: f64,
    orientation_margin: f64,
    api_match_id: Option<i64>,
    opponent_linked: bool,
}

/// Однозначный матч и одно из: почти одинаковые имена, уже связанный соперник
/// или та же пара в двух разных матчах.
fn confirmable(proofs: &[Proof]) -> bool {
    let strong: Vec<&Proof> = proofs
        .iter()
        .filter(|p| {
            p.method == "independent_match"
                && p.context_complete
                && p.time_diff_hours
                    .is_some_and(|t| (0.0..=MAX_TIME_DIFF_HOURS).contains(&t))
                && p.candidate_margin >= MIN_CANDIDATE_MARGIN
                && p.orientation_margin >= MIN_ORIENTATION_MARGIN
        })
        .collect();
    let near_identical = |p: &&Proof| p.evidence_score >= 95.0 && p.score >= 90.0;
    let distinct_matches: BTreeSet<_> = strong.iter().filter_map(|p| p.api_match_id).collect();
    strong
        .iter()
        .any(|p| near_identical(p) || p.opponent_linked)
        || distinct_matches.len() >= 2
}

/// Связанные команды источников, если пара подтверждена.
async fn auto_confirm(db: &mut PgConnection, proposal_id: i32) -> sqlx::Result<Option<[i32; 2]>> {
    let proposal = repository::get_proposal(db, proposal_id).await?;
    if proposal.status != ProposalStatus::Pending || proposal.reviewed_by.is_some() {
        return Ok(None);
    }
    let proofs: Vec<Proof> = repository::valid_evidence(db, proposal_id)
        .await?
        .into_iter()
        .filter_map(|details| serde_json::from_value(details).ok())
        .collect();
    if !confirmable(&proofs) {
        return Ok(None);
    }
    let ids = [proposal.left_id, proposal.right_id];
    let rows = repository::source_team_states(db, &ids).await?;
    if rows.len() != 2 || repository::any_unlinked(db, &ids).await? {
        return Ok(None);
    }
    let Some(api) = rows.iter().find(|row| row.source == API_SOURCE) else {
        return Ok(None);
    };
    let book = rows
        .iter()
        .find(|row| row.id != api.id)
        .expect("две строки");
    if let (Some(left), Some(right)) = (api.team_id, book.team_id)
        && left != right
    {
        return Ok(None);
    }
    // Одно имя букмекера у разных api-команд (два «Динамо») — не связываем.
    let rivals = repository::rival_api_teams(db, book.id, api.id, API_SOURCE).await?;
    if rivals
        .iter()
        .any(|team| team.is_none() || *team != api.team_id)
    {
        return Ok(None);
    }
    let team_id = match api.team_id.or(book.team_id) {
        Some(team_id) => team_id,
        None => repository::create_team(db, &api.name).await?,
    };
    for row in [api, book] {
        if row.team_id != Some(team_id) {
            repository::link_source_team(db, row, team_id, proposal_id).await?;
        }
    }
    repository::confirm_proposal(db, proposal_id).await?;
    Ok(Some(ids))
}

/// Плановый проход: окно как у прежнего полного прогона (14 дней назад, 7 вперёд).
pub fn sweep_days() -> Vec<NaiveDate> {
    let today = Utc::now().date_naive();
    (-14..=7)
        .map(|offset| today + TimeDelta::days(offset))
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    fn strong(api_match_id: i64, evidence_score: f64) -> Proof {
        Proof {
            method: "independent_match".into(),
            context_complete: true,
            evidence_score,
            score: evidence_score,
            time_diff_hours: Some(0.0),
            candidate_margin: 20.0,
            orientation_margin: 50.0,
            api_match_id: Some(api_match_id),
            opponent_linked: false,
        }
    }

    #[test]
    fn confirms_unambiguous_identical_repeated_or_anchored() {
        assert!(confirmable(&[strong(1, 100.0)]), "почти одинаковые имена");
        assert!(
            !confirmable(&[strong(1, 80.0)]),
            "одна похожая улика — мало"
        );
        assert!(
            confirmable(&[strong(1, 80.0), strong(2, 78.0)]),
            "два разных матча"
        );
        assert!(
            !confirmable(&[strong(1, 80.0), strong(1, 80.0)]),
            "один матч дважды не считается"
        );
        assert!(
            confirmable(&[Proof {
                opponent_linked: true,
                ..strong(1, 76.0)
            }]),
            "соперник связан"
        );
    }

    #[test]
    fn rejects_ambiguous_or_stale_evidence() {
        let late = Proof {
            time_diff_hours: Some(2.0),
            ..strong(1, 100.0)
        };
        let rival = Proof {
            candidate_margin: 1.0,
            ..strong(1, 100.0)
        };
        let orientation = Proof {
            orientation_margin: 3.0,
            ..strong(1, 100.0)
        };
        let stale = Proof {
            context_complete: false,
            ..strong(1, 100.0)
        };
        for proof in [late, rival, orientation, stale] {
            assert!(!confirmable(&[proof]));
        }
        let legacy: Proof = serde_json::from_value(json!({"method": "independent_match"})).unwrap();
        assert!(
            !confirmable(&[legacy]),
            "старая улика без полей не подтверждает"
        );
    }
}
