//! Сборка ответов справочника и правила поверх repository.

use std::collections::{BTreeMap, BTreeSet, HashMap};

use chrono::{DateTime, NaiveDate, TimeDelta, Utc};

use sqlx::PgPool;

use super::repository::{
    self, DirectoryFilter, Fixture, NameObservation, SourceTeamsFilter, TeamObservation,
    like_pattern,
};
use super::schemas::{
    DirectoryFilter as DirectoryQuery, Mapping, ObservationsRequest, ObservationsResponse,
    PageQuery, ProposalStatus, SourceTeamHistoryResponse, SourceTeamResponse,
    SourceTeamsFilter as SourceTeamsQuery, TeamAliasCreate, TeamAliasDeleteResponse, TeamAliasItem,
    TeamAliasResponse, TeamDirectoryItem, TeamDirectoryResponse, TeamListResponse,
    TeamProposalItem, TeamProposalListResponse,
};
use crate::error::{AppError, AppResult};
use crate::normalize::normalize_team_name;

/// Шаблоны поиска: ILIKE по исходному тексту и LIKE по нормализованному имени.
struct Search {
    pattern: Option<String>,
    normalized: Option<String>,
}

impl Search {
    fn new(search: &str) -> Self {
        let term = search.trim();
        if term.is_empty() {
            return Self {
                pattern: None,
                normalized: None,
            };
        }
        let normalized = normalize_team_name(term);
        Self {
            pattern: Some(like_pattern(term)),
            normalized: (!normalized.is_empty()).then(|| like_pattern(&normalized)),
        }
    }
}

/// Пустой `?source=` означает «все источники», как во FastAPI-версии.
fn non_empty(value: &Option<String>) -> Option<&str> {
    value.as_deref().filter(|value| !value.is_empty())
}

pub async fn team_list(db: &PgPool, page: &PageQuery) -> AppResult<TeamListResponse> {
    let search = Search::new(&page.search);
    let (teams, count) =
        repository::teams_page(db, search.pattern.as_deref(), page.limit, page.offset).await?;
    Ok(TeamListResponse {
        teams,
        count,
        limit: page.limit,
        offset: page.offset,
    })
}

pub async fn source_teams_page(
    db: &PgPool,
    page: &PageQuery,
    query: &SourceTeamsQuery,
) -> AppResult<SourceTeamResponse> {
    if query.team_id.is_some_and(|id| id <= 0) {
        return Err(AppError::Validation(
            "team_id должен быть положительным".into(),
        ));
    }
    let search = Search::new(&page.search);
    let filter = SourceTeamsFilter {
        unmapped: query.unmapped,
        team_id: query.team_id,
        source: non_empty(&query.source),
        search: search.pattern.as_deref(),
        normalized: search.normalized.as_deref(),
    };
    let (teams, count) =
        repository::source_teams_page(db, &filter, page.limit, page.offset).await?;
    Ok(SourceTeamResponse {
        teams,
        count,
        limit: page.limit,
        offset: page.offset,
        sources: repository::distinct_sources(db).await?,
    })
}

/// Фильтр указателя общий для страницы и выгрузки.
fn directory_filter<'a>(
    search: &'a Search,
    term: &str,
    query: &'a DirectoryQuery,
) -> DirectoryFilter<'a> {
    DirectoryFilter {
        mapping: match query.mapping {
            Mapping::All => "all",
            Mapping::Linked => "linked",
            Mapping::Unlinked => "unlinked",
        },
        all_sources: query.all_sources,
        source: non_empty(&query.source),
        search: search.pattern.as_deref(),
        normalized: search.normalized.as_deref(),
        // Число в поиске — ещё и ID общей команды.
        search_id: term
            .bytes()
            .all(|byte| byte.is_ascii_digit())
            .then(|| term.parse().ok())
            .flatten(),
    }
}

pub async fn team_directory(
    db: &PgPool,
    page: &PageQuery,
    query: &DirectoryQuery,
) -> AppResult<TeamDirectoryResponse> {
    let search = Search::new(&page.search);
    let filter = directory_filter(&search, page.search.trim(), query);
    let (rows, count) = repository::directory_rows(db, &filter, page.limit, page.offset).await?;

    let team_ids: Vec<i32> = rows.iter().filter_map(|row| row.team_id).collect();
    let source_ids: Vec<i32> = rows.iter().filter_map(|row| row.source_id).collect();
    let mut index = HashMap::new();
    let mut teams: Vec<TeamDirectoryItem> = rows
        .into_iter()
        .enumerate()
        .map(|(position, row)| {
            let key = match row.team_id {
                Some(id) => format!("team:{id}"),
                None => format!("source:{}", row.source_id.unwrap_or_default()),
            };
            index.insert(key.clone(), position);
            TeamDirectoryItem {
                key,
                team_id: row.team_id,
                name: row.name,
                sources: Default::default(),
            }
        })
        .collect();
    if !teams.is_empty() {
        for mut entry in repository::directory_entries(db, &team_ids, &source_ids).await? {
            let key = match entry.team_id {
                Some(id) => format!("team:{id}"),
                None => format!("source:{}", entry.id),
            };
            // Между чтением страницы и её записей matcher может изменить связь.
            let Some(&position) = index.get(&key) else {
                continue;
            };
            let group = &mut teams[position];
            if group.team_id.is_some() {
                entry.team_name = Some(group.name.clone());
            }
            group
                .sources
                .entry(entry.source.clone())
                .or_default()
                .push(entry);
        }
    }
    Ok(TeamDirectoryResponse {
        teams,
        count,
        limit: page.limit,
        offset: page.offset,
        sources: repository::distinct_sources(db).await?,
    })
}

pub async fn source_team_history(db: &PgPool, id: i32) -> AppResult<SourceTeamHistoryResponse> {
    let team = repository::get_source_team(db, id)
        .await?
        .ok_or_else(|| AppError::NotFound("Команда не найдена".into()))?;
    Ok(SourceTeamHistoryResponse {
        team,
        names: repository::source_team_names(db, id).await?,
        audit: repository::source_team_audit(db, id).await?,
    })
}

const EVIDENCE_PER_PROPOSAL: i64 = 20;

pub async fn proposals_page(
    db: &PgPool,
    page: &PageQuery,
    status: ProposalStatus,
) -> AppResult<TeamProposalListResponse> {
    let (rows, count) = repository::proposals_page(db, status, page.limit, page.offset).await?;
    let ids: Vec<i32> = rows.iter().map(|row| row.id).collect();
    let side_ids: Vec<i32> = rows
        .iter()
        .flat_map(|row| [row.left_id, row.right_id])
        .collect();

    let sides: HashMap<_, _> = repository::source_teams_by_ids(db, &side_ids)
        .await?
        .into_iter()
        .map(|team| (team.id, team))
        .collect();
    let counts: HashMap<_, _> = repository::evidence_counts(db, &ids)
        .await?
        .into_iter()
        .collect();
    let mut evidence: HashMap<i32, Vec<_>> = HashMap::new();
    for entry in repository::latest_evidence(db, &ids, EVIDENCE_PER_PROPOSAL).await? {
        evidence.entry(entry.proposal_id).or_default().push(entry);
    }

    let proposals = rows
        .into_iter()
        .map(|row| {
            // FK гарантирует наличие обеих сторон; clone — одна команда бывает в нескольких парах.
            let left = sides.get(&row.left_id).cloned();
            let right = sides.get(&row.right_id).cloned();
            Ok(TeamProposalItem {
                id: row.id,
                status: row.status,
                left: left.ok_or_else(|| AppError::NotFound("Команда не найдена".into()))?,
                right: right.ok_or_else(|| AppError::NotFound("Команда не найдена".into()))?,
                reviewed_by: row.reviewed_by,
                reviewed_at: row.reviewed_at,
                evidence_count: counts.get(&row.id).copied().unwrap_or(0),
                evidence: evidence.remove(&row.id).unwrap_or_default(),
            })
        })
        .collect::<AppResult<_>>()?;
    Ok(TeamProposalListResponse {
        proposals,
        count,
        limit: page.limit,
        offset: page.offset,
    })
}

// --- Алиасы ----------------------------------------------------------------

pub async fn list_team_aliases(db: &PgPool) -> AppResult<TeamAliasResponse> {
    let aliases = repository::list_aliases(db).await?;
    Ok(TeamAliasResponse {
        count: aliases.len(),
        aliases,
    })
}

pub async fn create_team_alias(db: &PgPool, payload: &TeamAliasCreate) -> AppResult<TeamAliasItem> {
    let alias = normalize_team_name(&payload.alias);
    let canonical = normalize_team_name(&payload.canonical);
    if alias.is_empty() || canonical.is_empty() {
        return Err(AppError::BadRequest(
            "alias и canonical не могут быть пустыми".into(),
        ));
    }
    if let Some(item) = repository::insert_alias(db, &alias, &canonical).await? {
        println!("Добавлен алиас команды: '{alias}' -> '{canonical}'");
        return Ok(item);
    }
    let existing = repository::alias_canonical(db, &alias)
        .await?
        .unwrap_or_default();
    Err(AppError::Conflict(format!(
        "Алиас '{alias}' уже существует -> '{existing}'"
    )))
}

pub async fn delete_team_alias(db: &PgPool, id: i32) -> AppResult<TeamAliasDeleteResponse> {
    if !repository::delete_alias(db, id).await? {
        return Err(AppError::NotFound("Алиас не найден".into()));
    }
    Ok(TeamAliasDeleteResponse { deleted: id })
}

// --- Наблюдения от монолита -------------------------------------------------

const MAX_FIXTURES_PER_REQUEST: usize = 1000;

/// Пробелы схлопываются, чтобы имена из разных источников совпадали.
fn source_name_key(name: &str) -> String {
    name.split_whitespace().collect::<Vec<_>>().join(" ")
}

/// UTC-даты на пересчёт, включая конкурентов по обе стороны суток (±6 ч).
fn dirty_days(kickoffs: impl IntoIterator<Item = DateTime<Utc>>) -> Vec<NaiveDate> {
    let days: BTreeSet<_> = kickoffs
        .into_iter()
        .flat_map(|kickoff| {
            [-6, 0, 6].map(|hours| (kickoff + TimeDelta::hours(hours)).date_naive())
        })
        .collect();
    days.into_iter().collect()
}

/// Ключ команды источника — внешний ID, если он есть, иначе имя.
type TeamKey = (Option<String>, String);
/// Первое и последнее наблюдение имени команды.
type NameSpans = BTreeMap<(TeamKey, String), (DateTime<Utc>, DateTime<Utc>)>;

fn team_key(external_id: &Option<String>, name: &str) -> TeamKey {
    match external_id {
        Some(id) => (Some(id.clone()), String::new()),
        None => (None, name.to_owned()),
    }
}

/// Команды и их имена из событий: даты расширяются, имя берётся из самого свежего.
fn team_observations(fixtures: &[Fixture]) -> (BTreeMap<TeamKey, TeamObservation>, NameSpans) {
    let mut teams: BTreeMap<TeamKey, TeamObservation> = BTreeMap::new();
    let mut names = BTreeMap::new();
    for fixture in fixtures {
        let seen = fixture.observed_at;
        for (name, external_id) in [
            (&fixture.home, &fixture.home_ext_id),
            (&fixture.away, &fixture.away_ext_id),
        ] {
            if name.is_empty() {
                continue;
            }
            let key = team_key(external_id, name);
            let team = teams.entry(key.clone()).or_insert_with(|| TeamObservation {
                source_team_id: external_id.clone(),
                name: name.clone(),
                normalized_name: normalize_team_name(name),
                first_seen_at: seen,
                last_seen_at: seen,
            });
            team.first_seen_at = team.first_seen_at.min(seen);
            if seen >= team.last_seen_at {
                team.last_seen_at = seen;
                team.name = name.clone();
                team.normalized_name = normalize_team_name(name);
            }
            let span = names.entry((key, name.clone())).or_insert((seen, seen));
            *span = (span.0.min(seen), span.1.max(seen));
        }
    }
    (teams, names)
}

/// Повтор того же или более старого сообщения безвреден: монолит может слать его сколько угодно.
pub async fn save_observations(
    db: &PgPool,
    request: ObservationsRequest,
) -> AppResult<ObservationsResponse> {
    let source = request.source.trim().to_owned();
    if source.is_empty() || source.len() > 64 {
        return Err(AppError::Validation(
            "source должен быть от 1 до 64 символов".into(),
        ));
    }
    if request.fixtures.len() > MAX_FIXTURES_PER_REQUEST {
        return Err(AppError::Validation(format!(
            "не больше {MAX_FIXTURES_PER_REQUEST} событий за запрос"
        )));
    }
    // Одно событие дважды в запросе: остаётся самое свежее наблюдение.
    let mut unique: BTreeMap<String, Fixture> = BTreeMap::new();
    for fixture in request.fixtures {
        let fixture = Fixture {
            event_id: fixture.event_id.into(),
            home: source_name_key(&fixture.home),
            away: source_name_key(&fixture.away),
            home_ext_id: fixture.home_ext_id.map(Into::into),
            away_ext_id: fixture.away_ext_id.map(Into::into),
            kickoff: fixture.kickoff,
            league: fixture.league,
            observed_at: fixture.observed_at,
        };
        match unique.get(&fixture.event_id) {
            Some(current) if current.observed_at > fixture.observed_at => {}
            _ => {
                unique.insert(fixture.event_id.clone(), fixture);
            }
        }
    }
    let fixtures: Vec<Fixture> = unique.into_values().collect();
    if fixtures.is_empty() {
        return Ok(ObservationsResponse {
            fixtures: 0,
            teams: 0,
            dirty_days: 0,
        });
    }

    let mut tx = db.begin().await?;
    if !repository::try_guard_team_directory(&mut tx).await? {
        return Err(AppError::Busy);
    }

    let event_ids: Vec<String> = fixtures.iter().map(|f| f.event_id.clone()).collect();
    let existing: HashMap<_, _> = repository::fixtures_by_ids(&mut tx, &source, &event_ids)
        .await?
        .into_iter()
        .map(|state| (state.event_id.clone(), state))
        .collect();
    // Пересчитываем старую и новую дату, если сменились команды или время начала.
    let mut kickoffs = Vec::new();
    for fixture in &fixtures {
        match existing.get(&fixture.event_id) {
            None => kickoffs.push(fixture.kickoff),
            Some(old)
                if fixture.observed_at >= old.observed_at
                    && (&old.home, &old.away, old.kickoff)
                        != (&fixture.home, &fixture.away, fixture.kickoff) =>
            {
                kickoffs.extend([old.kickoff, fixture.kickoff]);
            }
            Some(_) => {}
        }
    }
    let days = dirty_days(kickoffs.into_iter().flatten());
    let saved = repository::upsert_fixtures(&mut tx, &source, &fixtures).await?;
    if !days.is_empty() {
        repository::mark_team_matches_dirty(&mut tx, &days).await?;
    }

    let (teams, names) = team_observations(&fixtures);
    let mut identities = HashMap::new();
    for with_external_id in [true, false] {
        let group: Vec<&TeamObservation> = teams
            .values()
            .filter(|team| team.source_team_id.is_some() == with_external_id)
            .collect();
        if group.is_empty() {
            continue;
        }
        for (id, external_id, name) in
            repository::upsert_source_teams(&mut tx, &source, with_external_id, &group).await?
        {
            identities.insert(team_key(&external_id, &name), id);
        }
    }
    let names: Vec<NameObservation> = names
        .into_iter()
        .map(
            |((key, name), (first_seen_at, last_seen_at))| NameObservation {
                source_team_id: identities[&key],
                normalized_name: normalize_team_name(&name),
                name,
                first_seen_at,
                last_seen_at,
            },
        )
        .collect();
    repository::upsert_source_team_names(&mut tx, &names).await?;
    tx.commit().await?;

    Ok(ObservationsResponse {
        fixtures: saved,
        teams: teams.len(),
        dirty_days: days.len(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn at(value: &str) -> DateTime<Utc> {
        value.parse().unwrap()
    }

    fn fixture(home: &str, home_ext_id: Option<&str>, observed_at: &str) -> Fixture {
        Fixture {
            event_id: "1".into(),
            home: home.into(),
            away: String::new(),
            home_ext_id: home_ext_id.map(Into::into),
            away_ext_id: None,
            kickoff: None,
            league: None,
            observed_at: at(observed_at),
        }
    }

    #[test]
    fn dirty_days_cover_neighbours() {
        let days = dirty_days([at("2026-10-08T03:00:00Z"), at("2026-10-08T12:00:00Z")]);
        let days: Vec<_> = days.iter().map(ToString::to_string).collect();
        assert_eq!(days, ["2026-10-07", "2026-10-08"]);
        assert_eq!(dirty_days([at("2026-10-08T20:00:00Z")]).len(), 2);
    }

    #[test]
    fn team_observations_keep_latest_name_and_widen_dates() {
        let fixtures = [
            fixture("ФК Зенит", Some("77"), "2026-10-02T00:00:00Z"),
            fixture("Зенит", Some("77"), "2026-10-01T00:00:00Z"),
            fixture("Зенит", None, "2026-10-03T00:00:00Z"),
        ];
        let (teams, names) = team_observations(&fixtures);
        assert_eq!(teams.len(), 2, "по внешнему ID и по имени — разные команды");
        let by_id = &teams[&(Some("77".into()), String::new())];
        assert_eq!(by_id.name, "ФК Зенит");
        assert_eq!(by_id.normalized_name, "zenit");
        assert_eq!(by_id.first_seen_at, at("2026-10-01T00:00:00Z"));
        assert_eq!(by_id.last_seen_at, at("2026-10-02T00:00:00Z"));
        assert_eq!(names.len(), 3, "история хранит оба имени команды с ID");
    }
}
