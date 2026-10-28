//! Сопоставление матчей api-sport с событиями одного букмекера.
//!
//! Кандидаты — события в окне ±3 ч (бинарный поиск по отсортированному времени),
//! затем жадное назначение «один к одному» по убыванию сходства:
//! O(A·log E + P·log P), где P — пары в окне, без индексов имён и кэшей.

use crate::similarity::{markers, name_similarity};

pub const WINDOW_US: i64 = 3 * 3_600_000_000;
/// Каждая команда пары должна быть похожа: одна точная не скрывает чужого соперника.
pub const MIN_TEAM_SCORE: f64 = 75.0;
pub const MIN_PAIR_SCORE: f64 = 80.0;
const ALTERNATIVES: usize = 5;

pub struct Team {
    pub name: String,
    markers: String,
    /// Общая команда, если связь уже подтверждена.
    pub team_id: Option<i32>,
}

impl Team {
    /// `name` уже нормализовано (`normalize_team_name`).
    pub fn new(name: String, team_id: Option<i32>) -> Self {
        Self {
            markers: markers(&name),
            name,
            team_id,
        }
    }
}

pub struct Fixture {
    pub kickoff_us: i64,
    pub home: Team,
    pub away: Team,
}

#[derive(Clone, Copy, Debug)]
pub struct Pair {
    pub api: usize,
    pub event: usize,
    pub score: f64,
    pub home_score: f64,
    pub away_score: f64,
    /// Хозяева api — гости у букмекера.
    pub swapped: bool,
    pub orientation_margin: f64,
    pub time_diff_hours: f64,
}

pub struct Assignment {
    pub pair: Pair,
    /// Лучшие пары с тем же матчем или событием, от сильной к слабой.
    pub alternatives: Vec<Pair>,
}

impl Assignment {
    pub fn alternative_score(&self) -> f64 {
        self.alternatives.first().map_or(0.0, |pair| pair.score)
    }
}

/// Подтверждённые связи сильнее написания: та же команда — 100, другая — пара невозможна.
fn team_score(left: &Team, right: &Team) -> Option<f64> {
    if let (Some(left_id), Some(right_id)) = (left.team_id, right.team_id) {
        return (left_id == right_id).then_some(100.0);
    }
    if left.name.is_empty() || right.name.is_empty() || left.markers != right.markers {
        return None;
    }
    Some(name_similarity(&left.name, &right.name))
}

fn orientation(home: &Team, away: &Team, first: &Team, second: &Team) -> Option<(f64, f64, f64)> {
    let home_score = team_score(home, first)?;
    let away_score = team_score(away, second)?;
    (home_score.min(away_score) >= MIN_TEAM_SCORE).then_some((
        (home_score + away_score) / 2.0,
        home_score,
        away_score,
    ))
}

fn pair(api: usize, match_: &Fixture, event: usize, odds: &Fixture) -> Option<Pair> {
    let straight = orientation(&match_.home, &match_.away, &odds.home, &odds.away);
    let swapped = orientation(&match_.home, &match_.away, &odds.away, &odds.home);
    let ((score, home_score, away_score), other, is_swapped) = match (straight, swapped) {
        (Some(s), Some(w)) if w.0 > s.0 => (w, Some(s), true),
        (Some(s), w) => (s, w, false),
        (None, Some(w)) => (w, None, true),
        (None, None) => return None,
    };
    Some(Pair {
        api,
        event,
        score,
        home_score,
        away_score,
        swapped: is_swapped,
        // Неподходящая ориентация считается как -1, чтобы отрыв был виден.
        orientation_margin: score - other.map_or(-1.0, |o| o.0),
        time_diff_hours: (match_.kickoff_us - odds.kickoff_us).unsigned_abs() as f64
            / 3_600_000_000.0,
    })
}

/// При равном сходстве выигрывает ближайшее время, затем стабильный порядок.
fn rank(left: &Pair, right: &Pair) -> std::cmp::Ordering {
    right
        .score
        .total_cmp(&left.score)
        .then(left.time_diff_hours.total_cmp(&right.time_diff_hours))
        .then(left.api.cmp(&right.api))
        .then(left.event.cmp(&right.event))
}

pub fn assign(matches: &[Fixture], events: &[Fixture]) -> Vec<Assignment> {
    let mut by_time: Vec<usize> = (0..events.len()).collect();
    by_time.sort_by_key(|&event| events[event].kickoff_us);

    let mut pairs = Vec::new();
    for (api, match_) in matches.iter().enumerate() {
        let from =
            by_time.partition_point(|&e| events[e].kickoff_us < match_.kickoff_us - WINDOW_US);
        let to =
            by_time.partition_point(|&e| events[e].kickoff_us <= match_.kickoff_us + WINDOW_US);
        pairs.extend(
            by_time[from..to]
                .iter()
                .filter_map(|&event| pair(api, match_, event, &events[event])),
        );
    }
    pairs.sort_by(rank);

    // Индексы пар (уже по убыванию) для каждого матча и события — источник альтернатив.
    let mut by_api = vec![Vec::new(); matches.len()];
    let mut by_event = vec![Vec::new(); events.len()];
    for (index, pair) in pairs.iter().enumerate() {
        by_api[pair.api].push(index);
        by_event[pair.event].push(index);
    }

    let mut used_api = vec![false; matches.len()];
    let mut used_event = vec![false; events.len()];
    let mut assignments = Vec::new();
    for (index, pair) in pairs.iter().enumerate() {
        if pair.score < MIN_PAIR_SCORE {
            break;
        }
        if used_api[pair.api] || used_event[pair.event] {
            continue;
        }
        used_api[pair.api] = true;
        used_event[pair.event] = true;
        let mut rivals: Vec<usize> = by_api[pair.api]
            .iter()
            .chain(&by_event[pair.event])
            .copied()
            .filter(|&other| other != index)
            .collect();
        rivals.sort_unstable();
        rivals.dedup();
        rivals.truncate(ALTERNATIVES);
        assignments.push(Assignment {
            pair: *pair,
            alternatives: rivals.into_iter().map(|other| pairs[other]).collect(),
        });
    }
    assignments
}

#[cfg(test)]
mod tests {
    use super::*;

    const HOUR: i64 = 3_600_000_000;

    fn fixture(hours: i64, home: &str, away: &str) -> Fixture {
        Fixture {
            kickoff_us: hours * HOUR,
            home: Team::new(home.into(), None),
            away: Team::new(away.into(), None),
        }
    }

    #[test]
    fn assigns_one_to_one_with_time_and_orientation() {
        let matches = [
            fixture(10, "zenit", "spartak moskva"),
            fixture(12, "real madrid", "barcelona"),
            fixture(20, "real madrid", "barcelona"), // повтор позже — событие уже занято
        ];
        let events = [
            fixture(10, "spartak moskva", "zenit st petersburg"), // переставлены
            fixture(12, "real madrid", "barcelona"),
            fixture(14, "liverpool", "everton"),
        ];
        let result = assign(&matches, &events);
        assert_eq!(result.len(), 2);
        let zenit = &result.iter().find(|a| a.pair.api == 0).unwrap().pair;
        assert_eq!(zenit.event, 0);
        assert!(zenit.swapped && zenit.home_score == 100.0);
        assert!(zenit.orientation_margin > 10.0);
        let real = result.iter().find(|a| a.pair.api == 1).unwrap();
        assert_eq!((real.pair.event, real.pair.time_diff_hours), (1, 0.0));
        // Матч в 20:00 видит то же событие в окне 3 ч? Нет: 8 ч — вне окна.
        assert!(real.alternatives.is_empty());
    }

    #[test]
    fn closest_kickoff_wins_and_rival_lowers_margin() {
        let matches = [
            fixture(10, "dinamo", "rostov"),
            fixture(12, "dinamo", "rostov"),
        ];
        let events = [fixture(11, "dinamo", "rostov")];
        let result = assign(&matches, &events);
        assert_eq!(result.len(), 1);
        assert_eq!(
            result[0].pair.api, 0,
            "равное сходство — раньше по api, время одинаково далеко"
        );
        assert_eq!(
            result[0].alternative_score(),
            100.0,
            "второй матч — равный соперник"
        );
    }

    #[test]
    fn squad_markers_and_links_decide() {
        let mut youth = fixture(10, "zenit u19", "spartak u19");
        let events = [fixture(10, "zenit", "spartak")];
        assert!(
            assign(&[youth], &events).is_empty(),
            "u19 не равен основному составу"
        );

        youth = fixture(10, "zenit", "fakel");
        youth.away.team_id = Some(7);
        let mut linked = fixture(10, "zenit", "fakel voronezh");
        linked.away.team_id = Some(8);
        assert!(
            assign(&[youth], &[linked]).is_empty(),
            "связанные с разными командами не совпадают"
        );
    }
}
