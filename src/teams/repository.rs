//! SQL справочника. Функции только выполняют запросы; сборка ответов — в service.
//!
//! Фильтры — статический SQL с необязательными параметрами (`$n IS NULL OR ...`),
//! общий для запроса страницы и подсчёта, поэтому вынесен в макросы.

use chrono::{DateTime, NaiveDate, Utc};
use sqlx::{FromRow, PgConnection, PgPool};

use super::schemas::{
    DirectoryRow, ProposalRow, ProposalStatus, SourceTeamItem, SourceTeamNameItem, TeamAliasItem,
    TeamItem, TeamMappingAuditItem, TeamMatchEvidenceItem,
};

/// Подстрока для ILIKE/LIKE с экранированием `\`, `%`, `_`.
pub fn like_pattern(term: &str) -> String {
    let escaped = term
        .replace('\\', "\\\\")
        .replace('%', "\\%")
        .replace('_', "\\_");
    format!("%{escaped}%")
}

/// Поиск по команде источника `s`: $search — ILIKE-шаблон, $normalized — LIKE-шаблон.
macro_rules! source_team_search {
    ($search:literal, $normalized:literal) => {
        concat!(
            "(s.name ILIKE ",
            $search,
            " OR s.source_team_id ILIKE ",
            $search,
            " OR s.normalized_name LIKE ",
            $normalized,
            ")"
        )
    };
}

// --- Команды ---------------------------------------------------------------

macro_rules! teams_filter {
    () => {
        " FROM teams WHERE ($1::text IS NULL OR preferred_name ILIKE $1)"
    };
}

pub async fn teams_page(
    db: &PgPool,
    search: Option<&str>,
    limit: i64,
    offset: i64,
) -> sqlx::Result<(Vec<TeamItem>, i64)> {
    let count = sqlx::query_scalar(concat!("SELECT count(*)", teams_filter!()))
        .bind(search)
        .fetch_one(db)
        .await?;
    let rows = sqlx::query_as(concat!(
        "SELECT id, preferred_name, created_at",
        teams_filter!(),
        " ORDER BY preferred_name, id LIMIT $2 OFFSET $3"
    ))
    .bind(search)
    .bind(limit)
    .bind(offset)
    .fetch_all(db)
    .await?;
    Ok((rows, count))
}

// --- Команды источников ----------------------------------------------------

macro_rules! source_team_select {
    () => {
        concat!(
            "SELECT s.id, s.team_id, t.preferred_name AS team_name, s.source, s.source_team_id,",
            " s.name, s.normalized_name, s.first_seen_at, s.last_seen_at",
            " FROM source_teams s LEFT JOIN teams t ON t.id = s.team_id"
        )
    };
}

macro_rules! source_teams_filter {
    () => {
        concat!(
            " WHERE (NOT $1 OR s.team_id IS NULL)",
            " AND ($2::int IS NULL OR s.team_id = $2)",
            " AND ($3::text IS NULL OR s.source = $3)",
            " AND ($4::text IS NULL OR ",
            source_team_search!("$4", "$5"),
            ")"
        )
    };
}

pub struct SourceTeamsFilter<'a> {
    pub unmapped: bool,
    pub team_id: Option<i32>,
    pub source: Option<&'a str>,
    pub search: Option<&'a str>,
    pub normalized: Option<&'a str>,
}

pub async fn source_teams_page(
    db: &PgPool,
    filter: &SourceTeamsFilter<'_>,
    limit: i64,
    offset: i64,
) -> sqlx::Result<(Vec<SourceTeamItem>, i64)> {
    let count = sqlx::query_scalar(concat!(
        "SELECT count(*) FROM source_teams s",
        source_teams_filter!()
    ))
    .bind(filter.unmapped)
    .bind(filter.team_id)
    .bind(filter.source)
    .bind(filter.search)
    .bind(filter.normalized)
    .fetch_one(db)
    .await?;
    let rows = sqlx::query_as(concat!(
        source_team_select!(),
        source_teams_filter!(),
        " ORDER BY s.name, s.source, s.id LIMIT $6 OFFSET $7"
    ))
    .bind(filter.unmapped)
    .bind(filter.team_id)
    .bind(filter.source)
    .bind(filter.search)
    .bind(filter.normalized)
    .bind(limit)
    .bind(offset)
    .fetch_all(db)
    .await?;
    Ok((rows, count))
}

pub async fn distinct_sources(db: &PgPool) -> sqlx::Result<Vec<String>> {
    sqlx::query_scalar("SELECT DISTINCT source FROM source_teams ORDER BY source")
        .fetch_all(db)
        .await
}

pub async fn get_source_team(db: &PgPool, id: i32) -> sqlx::Result<Option<SourceTeamItem>> {
    sqlx::query_as(concat!(source_team_select!(), " WHERE s.id = $1"))
        .bind(id)
        .fetch_optional(db)
        .await
}

pub async fn source_teams_by_ids(db: &PgPool, ids: &[i32]) -> sqlx::Result<Vec<SourceTeamItem>> {
    sqlx::query_as(concat!(source_team_select!(), " WHERE s.id = ANY($1)"))
        .bind(ids)
        .fetch_all(db)
        .await
}

pub async fn source_team_names(db: &PgPool, id: i32) -> sqlx::Result<Vec<SourceTeamNameItem>> {
    sqlx::query_as(
        "SELECT name, first_seen_at, last_seen_at FROM source_team_names \
         WHERE source_team_id = $1 ORDER BY first_seen_at, id",
    )
    .bind(id)
    .fetch_all(db)
    .await
}

pub async fn source_team_audit(db: &PgPool, id: i32) -> sqlx::Result<Vec<TeamMappingAuditItem>> {
    sqlx::query_as(
        "SELECT id, old_team_id, new_team_id, actor, method, proposal_id, created_at \
         FROM team_mapping_audit WHERE source_team_id = $1 ORDER BY id DESC",
    )
    .bind(id)
    .fetch_all(db)
    .await
}

// --- Указатель -------------------------------------------------------------

// Пагинация по единым командам: связи одной команды не разрываются между
// страницами, а фильтр по источнику сохраняет остальные связи команды.
macro_rules! directory_filter {
    () => {
        concat!(
            " FROM (",
            "SELECT t.id AS team_id, NULL::int AS source_id, t.preferred_name AS name FROM teams t",
            " WHERE EXISTS (SELECT 1 FROM source_teams s WHERE s.team_id = t.id)",
            " UNION ALL",
            " SELECT NULL::int, s.id, s.name FROM source_teams s WHERE s.team_id IS NULL",
            ") d WHERE ($1 = 'all' OR ($1 = 'linked') = (d.team_id IS NOT NULL))",
            // Только единые команды, связанные со всеми известными источниками.
            " AND (NOT $2 OR (d.team_id IS NOT NULL AND",
            " (SELECT count(DISTINCT s.source) FROM source_teams s WHERE s.team_id = d.team_id)",
            " = (SELECT count(DISTINCT source) FROM source_teams)))",
            " AND ($3::text IS NULL OR EXISTS (SELECT 1 FROM source_teams s",
            " WHERE (s.team_id = d.team_id OR s.id = d.source_id) AND s.source = $3))",
            " AND ($4::text IS NULL OR d.name ILIKE $4 OR d.team_id = $6",
            " OR EXISTS (SELECT 1 FROM source_teams s",
            " WHERE (s.team_id = d.team_id OR s.id = d.source_id) AND ",
            source_team_search!("$4", "$5"),
            "))"
        )
    };
}

pub struct DirectoryFilter<'a> {
    pub mapping: &'a str,
    pub all_sources: bool,
    pub source: Option<&'a str>,
    pub search: Option<&'a str>,
    pub normalized: Option<&'a str>,
    pub search_id: Option<i32>,
}

pub async fn directory_rows(
    db: &PgPool,
    filter: &DirectoryFilter<'_>,
    limit: i64,
    offset: i64,
) -> sqlx::Result<(Vec<DirectoryRow>, i64)> {
    let count = sqlx::query_scalar(concat!("SELECT count(*)", directory_filter!()))
        .bind(filter.mapping)
        .bind(filter.all_sources)
        .bind(filter.source)
        .bind(filter.search)
        .bind(filter.normalized)
        .bind(filter.search_id)
        .fetch_one(db)
        .await?;
    let rows = sqlx::query_as(concat!(
        "SELECT d.team_id, d.source_id, d.name",
        directory_filter!(),
        " ORDER BY d.name, d.team_id, d.source_id LIMIT $7 OFFSET $8"
    ))
    .bind(filter.mapping)
    .bind(filter.all_sources)
    .bind(filter.source)
    .bind(filter.search)
    .bind(filter.normalized)
    .bind(filter.search_id)
    .bind(limit)
    .bind(offset)
    .fetch_all(db)
    .await?;
    Ok((rows, count))
}

/// Все команды источников для страницы указателя; team_name заполняет service.
pub async fn directory_entries(
    db: &PgPool,
    team_ids: &[i32],
    source_ids: &[i32],
) -> sqlx::Result<Vec<SourceTeamItem>> {
    sqlx::query_as(
        "SELECT s.id, s.team_id, NULL::text AS team_name, s.source, s.source_team_id, \
         s.name, s.normalized_name, s.first_seen_at, s.last_seen_at FROM source_teams s \
         WHERE s.team_id = ANY($1) OR s.id = ANY($2) ORDER BY s.source, s.name, s.id",
    )
    .bind(team_ids)
    .bind(source_ids)
    .fetch_all(db)
    .await
}

// --- Предложения -----------------------------------------------------------

pub async fn proposals_page(
    db: &PgPool,
    status: ProposalStatus,
    limit: i64,
    offset: i64,
) -> sqlx::Result<(Vec<ProposalRow>, i64)> {
    let count = sqlx::query_scalar("SELECT count(*) FROM team_match_proposals WHERE status = $1")
        .bind(status)
        .fetch_one(db)
        .await?;
    let rows = sqlx::query_as(
        "SELECT id, left_id, right_id, status, reviewed_by, reviewed_at \
         FROM team_match_proposals WHERE status = $1 ORDER BY id DESC LIMIT $2 OFFSET $3",
    )
    .bind(status)
    .bind(limit)
    .bind(offset)
    .fetch_all(db)
    .await?;
    Ok((rows, count))
}

pub async fn evidence_counts(db: &PgPool, proposal_ids: &[i32]) -> sqlx::Result<Vec<(i32, i64)>> {
    sqlx::query_as(
        "SELECT proposal_id, count(*) FROM team_match_evidence \
         WHERE proposal_id = ANY($1) GROUP BY proposal_id",
    )
    .bind(proposal_ids)
    .fetch_all(db)
    .await
}

/// Последние `per_proposal` улик каждого предложения, новые первыми.
pub async fn latest_evidence(
    db: &PgPool,
    proposal_ids: &[i32],
    per_proposal: i64,
) -> sqlx::Result<Vec<TeamMatchEvidenceItem>> {
    sqlx::query_as(
        "SELECT id, proposal_id, score, details FROM ( \
           SELECT id, proposal_id, score, details, \
                  row_number() OVER (PARTITION BY proposal_id ORDER BY id DESC) AS rn \
           FROM team_match_evidence WHERE proposal_id = ANY($1) \
         ) e WHERE rn <= $2 ORDER BY proposal_id, id DESC",
    )
    .bind(proposal_ids)
    .bind(per_proposal)
    .fetch_all(db)
    .await
}

// --- Алиасы ----------------------------------------------------------------

pub async fn list_aliases(db: &PgPool) -> sqlx::Result<Vec<TeamAliasItem>> {
    sqlx::query_as("SELECT id, alias, canonical, created_at FROM team_aliases ORDER BY alias")
        .fetch_all(db)
        .await
}

/// `None`, если алиас уже есть: уникальность проверяет БД, без гонки check-then-insert.
pub async fn insert_alias(
    db: &PgPool,
    alias: &str,
    canonical: &str,
) -> sqlx::Result<Option<TeamAliasItem>> {
    sqlx::query_as(
        "INSERT INTO team_aliases (alias, canonical) VALUES ($1, $2) \
         ON CONFLICT (alias) DO NOTHING RETURNING id, alias, canonical, created_at",
    )
    .bind(alias)
    .bind(canonical)
    .fetch_optional(db)
    .await
}

pub async fn alias_canonical(db: &PgPool, alias: &str) -> sqlx::Result<Option<String>> {
    sqlx::query_scalar("SELECT canonical FROM team_aliases WHERE alias = $1")
        .bind(alias)
        .fetch_optional(db)
        .await
}

pub async fn delete_alias(db: &PgPool, id: i32) -> sqlx::Result<bool> {
    let result = sqlx::query("DELETE FROM team_aliases WHERE id = $1")
        .bind(id)
        .execute(db)
        .await?;
    Ok(result.rows_affected() > 0)
}

// --- Наблюдения ------------------------------------------------------------

/// Отдельно от блокировки сессии, исключающей параллельные запуски matcher.
const TEAM_DIRECTORY_LOCK_KEY: i64 = 0x455641444952;

/// Сериализует запись в справочник до commit; `false` — справочник занят.
pub async fn try_guard_team_directory(db: &mut PgConnection) -> sqlx::Result<bool> {
    sqlx::query_scalar("SELECT pg_try_advisory_xact_lock($1)")
        .bind(TEAM_DIRECTORY_LOCK_KEY)
        .fetch_one(db)
        .await
}

/// Нормализованное событие источника; `event_id` и внешние ID — строки.
pub struct Fixture {
    pub event_id: String,
    pub home: String,
    pub away: String,
    pub home_ext_id: Option<String>,
    pub away_ext_id: Option<String>,
    pub kickoff: Option<DateTime<Utc>>,
    pub league: Option<String>,
    pub observed_at: DateTime<Utc>,
}

#[derive(FromRow)]
pub struct FixtureState {
    pub event_id: String,
    pub home: String,
    pub away: String,
    pub kickoff: Option<DateTime<Utc>>,
    pub observed_at: DateTime<Utc>,
}

pub async fn fixtures_by_ids(
    db: &mut PgConnection,
    source: &str,
    event_ids: &[String],
) -> sqlx::Result<Vec<FixtureState>> {
    sqlx::query_as(
        "SELECT event_id, home, away, kickoff, observed_at FROM fixtures \
         WHERE source = $1 AND event_id = ANY($2)",
    )
    .bind(source)
    .bind(event_ids)
    .fetch_all(db)
    .await
}

/// Число вставленных или обновлённых событий; более старое наблюдение не применяется.
pub async fn upsert_fixtures(
    db: &mut PgConnection,
    source: &str,
    fixtures: &[Fixture],
) -> sqlx::Result<u64> {
    let result = sqlx::query(
        "INSERT INTO fixtures (source, event_id, home, away, home_ext_id, away_ext_id, \
                               kickoff, league, observed_at) \
         SELECT $1, u.* FROM unnest($2::text[], $3::text[], $4::text[], $5::text[], \
                                    $6::text[], $7::timestamptz[], $8::text[], $9::timestamptz[]) AS u \
         ON CONFLICT (source, event_id) DO UPDATE SET \
           home = EXCLUDED.home, away = EXCLUDED.away, \
           home_ext_id = EXCLUDED.home_ext_id, away_ext_id = EXCLUDED.away_ext_id, \
           kickoff = EXCLUDED.kickoff, league = EXCLUDED.league, observed_at = EXCLUDED.observed_at \
         WHERE EXCLUDED.observed_at >= fixtures.observed_at",
    )
    .bind(source)
    .bind(fixtures.iter().map(|f| f.event_id.clone()).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.home.clone()).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.away.clone()).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.home_ext_id.clone()).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.away_ext_id.clone()).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.kickoff).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.league.clone()).collect::<Vec<_>>())
    .bind(fixtures.iter().map(|f| f.observed_at).collect::<Vec<_>>())
    .execute(db)
    .await?;
    Ok(result.rows_affected())
}

/// Ставит даты в очередь матчера; запись и расчёт идут под одной блокировкой справочника.
pub async fn mark_team_matches_dirty(
    db: &mut PgConnection,
    days: &[NaiveDate],
) -> sqlx::Result<()> {
    sqlx::query(
        "INSERT INTO team_match_work (match_date, generation) SELECT unnest($1::date[]), 1 \
         ON CONFLICT (match_date) DO UPDATE SET generation = team_match_work.generation + 1",
    )
    .bind(days)
    .execute(db)
    .await?;
    Ok(())
}

/// Агрегированное наблюдение команды источника за запрос.
pub struct TeamObservation {
    pub source_team_id: Option<String>,
    pub name: String,
    pub normalized_name: String,
    pub first_seen_at: DateTime<Utc>,
    pub last_seen_at: DateTime<Utc>,
}

// Имя обновляется только более свежим наблюдением; даты лишь расширяются,
// поэтому повтор старого сообщения ничего не откатывает.
macro_rules! upsert_source_teams {
    ($conflict:literal) => {
        concat!(
            "INSERT INTO source_teams (source, source_team_id, name, normalized_name, first_seen_at, last_seen_at) ",
            "SELECT $1, u.* FROM unnest($2::text[], $3::text[], $4::text[], $5::timestamptz[], $6::timestamptz[]) AS u ",
            "ON CONFLICT ", $conflict, " DO UPDATE SET ",
            "name = CASE WHEN EXCLUDED.last_seen_at >= source_teams.last_seen_at ",
            "THEN EXCLUDED.name ELSE source_teams.name END, ",
            "normalized_name = CASE WHEN EXCLUDED.last_seen_at >= source_teams.last_seen_at ",
            "THEN EXCLUDED.normalized_name ELSE source_teams.normalized_name END, ",
            "first_seen_at = LEAST(source_teams.first_seen_at, EXCLUDED.first_seen_at), ",
            "last_seen_at = GREATEST(source_teams.last_seen_at, EXCLUDED.last_seen_at) ",
            "RETURNING id, source_team_id, name"
        )
    };
}

/// Все строки должны быть одной группы: с внешним ID или без него.
pub async fn upsert_source_teams(
    db: &mut PgConnection,
    source: &str,
    with_external_id: bool,
    teams: &[&TeamObservation],
) -> sqlx::Result<Vec<(i32, Option<String>, String)>> {
    let sql = if with_external_id {
        upsert_source_teams!("(source, source_team_id) WHERE source_team_id IS NOT NULL")
    } else {
        upsert_source_teams!("(source, name) WHERE source_team_id IS NULL")
    };
    sqlx::query_as(sql)
        .bind(source)
        .bind(
            teams
                .iter()
                .map(|t| t.source_team_id.clone())
                .collect::<Vec<_>>(),
        )
        .bind(teams.iter().map(|t| t.name.clone()).collect::<Vec<_>>())
        .bind(
            teams
                .iter()
                .map(|t| t.normalized_name.clone())
                .collect::<Vec<_>>(),
        )
        .bind(teams.iter().map(|t| t.first_seen_at).collect::<Vec<_>>())
        .bind(teams.iter().map(|t| t.last_seen_at).collect::<Vec<_>>())
        .fetch_all(db)
        .await
}

pub struct NameObservation {
    pub source_team_id: i32,
    pub name: String,
    pub normalized_name: String,
    pub first_seen_at: DateTime<Utc>,
    pub last_seen_at: DateTime<Utc>,
}

pub async fn upsert_source_team_names(
    db: &mut PgConnection,
    names: &[NameObservation],
) -> sqlx::Result<()> {
    sqlx::query(
        "INSERT INTO source_team_names (source_team_id, name, normalized_name, first_seen_at, last_seen_at) \
         SELECT * FROM unnest($1::int[], $2::text[], $3::text[], $4::timestamptz[], $5::timestamptz[]) \
         ON CONFLICT (source_team_id, name) DO UPDATE SET \
           first_seen_at = LEAST(source_team_names.first_seen_at, EXCLUDED.first_seen_at), \
           last_seen_at = GREATEST(source_team_names.last_seen_at, EXCLUDED.last_seen_at)",
    )
    .bind(names.iter().map(|n| n.source_team_id).collect::<Vec<_>>())
    .bind(names.iter().map(|n| n.name.clone()).collect::<Vec<_>>())
    .bind(names.iter().map(|n| n.normalized_name.clone()).collect::<Vec<_>>())
    .bind(names.iter().map(|n| n.first_seen_at).collect::<Vec<_>>())
    .bind(names.iter().map(|n| n.last_seen_at).collect::<Vec<_>>())
    .execute(db)
    .await?;
    Ok(())
}

// --- Матчер ----------------------------------------------------------------

pub async fn pending_days(db: &PgPool, limit: i64) -> sqlx::Result<Vec<NaiveDate>> {
    sqlx::query_scalar("SELECT match_date FROM team_match_work ORDER BY match_date LIMIT $1")
        .bind(limit)
        .fetch_all(db)
        .await
}

/// Плановый проход: уже стоящие в очереди даты не трогаются.
pub async fn enqueue_days(db: &PgPool, days: &[NaiveDate]) -> sqlx::Result<()> {
    sqlx::query(
        "INSERT INTO team_match_work (match_date) SELECT unnest($1::date[]) \
         ON CONFLICT (match_date) DO NOTHING",
    )
    .bind(days)
    .execute(db)
    .await?;
    Ok(())
}

pub async fn acknowledge_day(db: &mut PgConnection, day: NaiveDate) -> sqlx::Result<()> {
    sqlx::query("DELETE FROM team_match_work WHERE match_date = $1")
        .bind(day)
        .execute(db)
        .await?;
    Ok(())
}

/// Событие с командами справочника: по внешнему ID, а без него — по имени.
#[derive(FromRow)]
pub struct FixtureRow {
    pub source: String,
    pub event_id: String,
    pub home: String,
    pub away: String,
    pub kickoff: DateTime<Utc>,
    pub league: Option<String>,
    pub home_source_id: Option<i32>,
    pub home_team_id: Option<i32>,
    pub away_source_id: Option<i32>,
    pub away_team_id: Option<i32>,
}

macro_rules! source_team_of {
    ($side:literal) => {
        concat!(
            " LEFT JOIN LATERAL (SELECT s.id, s.team_id FROM source_teams s WHERE s.source = f.source AND ",
            "f.", $side, "_ext_id IS NOT NULL AND s.source_team_id = f.", $side, "_ext_id ",
            "UNION ALL SELECT s.id, s.team_id FROM source_teams s WHERE s.source = f.source AND ",
            "f.", $side, "_ext_id IS NULL AND s.source_team_id IS NULL AND s.name = f.", $side, ") ",
            $side, "_team ON true"
        )
    };
}

pub async fn fixtures_between(
    db: &mut PgConnection,
    from: DateTime<Utc>,
    to: DateTime<Utc>,
) -> sqlx::Result<Vec<FixtureRow>> {
    sqlx::query_as(concat!(
        "SELECT f.source, f.event_id, f.home, f.away, f.kickoff, f.league, ",
        "home_team.id AS home_source_id, home_team.team_id AS home_team_id, ",
        "away_team.id AS away_source_id, away_team.team_id AS away_team_id FROM fixtures f",
        source_team_of!("home"),
        source_team_of!("away"),
        " WHERE f.kickoff >= $1 AND f.kickoff < $2 ORDER BY f.source, f.event_id"
    ))
    .bind(from)
    .bind(to)
    .fetch_all(db)
    .await
}

/// Старые улики пересчитанных матчей остаются историей, но больше не подтверждают связь.
pub async fn invalidate_match_evidence(
    db: &mut PgConnection,
    match_ids: &[i32],
) -> sqlx::Result<()> {
    sqlx::query(
        "UPDATE team_match_evidence SET details = details || \
           '{\"context_complete\": false, \"invalidated_reason\": \"match_context_recalculated\"}' \
         WHERE (details ->> 'api_match_id')::int = ANY($1) \
           AND details ->> 'method' = 'independent_match' \
           AND (details ->> 'context_complete')::boolean",
    )
    .bind(match_ids)
    .execute(db)
    .await?;
    Ok(())
}

/// Предложение пары (left < right) и его улика; `true`, если улика новая.
pub async fn save_proposal(
    db: &mut PgConnection,
    left_id: i32,
    right_id: i32,
    evidence_key: &str,
    score: f64,
    details: &serde_json::Value,
) -> sqlx::Result<(i32, bool)> {
    let (left_id, right_id) = (left_id.min(right_id), left_id.max(right_id));
    sqlx::query(
        "INSERT INTO team_match_proposals (left_id, right_id) VALUES ($1, $2) \
         ON CONFLICT (left_id, right_id) DO NOTHING",
    )
    .bind(left_id)
    .bind(right_id)
    .execute(&mut *db)
    .await?;
    let proposal_id: i32 = sqlx::query_scalar(
        "SELECT id FROM team_match_proposals WHERE left_id = $1 AND right_id = $2",
    )
    .bind(left_id)
    .bind(right_id)
    .fetch_one(&mut *db)
    .await?;
    // xmax = 0 только у только что вставленной строки.
    let added: bool = sqlx::query_scalar(
        "INSERT INTO team_match_evidence (proposal_id, evidence_key, score, details) \
         VALUES ($1, $2, $3, $4) ON CONFLICT (proposal_id, evidence_key) \
         DO UPDATE SET score = EXCLUDED.score, details = EXCLUDED.details \
         RETURNING xmax = 0",
    )
    .bind(proposal_id)
    .bind(evidence_key)
    .bind(score)
    .bind(details)
    .fetch_one(&mut *db)
    .await?;
    if added {
        // Новая улика возвращает отклонённую пару на рассмотрение.
        sqlx::query("UPDATE team_match_proposals SET status = 'pending' WHERE id = $1 AND status = 'rejected'")
            .bind(proposal_id)
            .execute(db)
            .await?;
    }
    Ok((proposal_id, added))
}

#[derive(FromRow)]
pub struct ProposalState {
    pub left_id: i32,
    pub right_id: i32,
    pub status: ProposalStatus,
    pub reviewed_by: Option<String>,
}

pub async fn get_proposal(db: &mut PgConnection, id: i32) -> sqlx::Result<ProposalState> {
    sqlx::query_as(
        "SELECT left_id, right_id, status, reviewed_by FROM team_match_proposals WHERE id = $1",
    )
    .bind(id)
    .fetch_one(db)
    .await
}

pub async fn valid_evidence(
    db: &mut PgConnection,
    proposal_id: i32,
) -> sqlx::Result<Vec<serde_json::Value>> {
    sqlx::query_scalar(
        "SELECT details || jsonb_build_object('evidence_score', score) FROM team_match_evidence \
         WHERE proposal_id = $1 AND (details ->> 'context_complete')::boolean",
    )
    .bind(proposal_id)
    .fetch_all(db)
    .await
}

#[derive(FromRow)]
pub struct SourceTeamState {
    pub id: i32,
    pub source: String,
    pub name: String,
    pub team_id: Option<i32>,
}

pub async fn source_team_states(
    db: &mut PgConnection,
    ids: &[i32],
) -> sqlx::Result<Vec<SourceTeamState>> {
    sqlx::query_as(
        "SELECT id, source, name, team_id FROM source_teams WHERE id = ANY($1) ORDER BY id",
    )
    .bind(ids)
    .fetch_all(db)
    .await
}

/// Последний аудит снимал связь — команду не связываем автоматически повторно.
pub async fn any_unlinked(db: &mut PgConnection, ids: &[i32]) -> sqlx::Result<bool> {
    sqlx::query_scalar(
        "SELECT EXISTS (SELECT 1 FROM (SELECT DISTINCT ON (source_team_id) new_team_id \
           FROM team_mapping_audit WHERE source_team_id = ANY($1) \
           ORDER BY source_team_id, id DESC) latest WHERE new_team_id IS NULL)",
    )
    .bind(ids)
    .fetch_one(db)
    .await
}

/// Общие команды других api-команд, с которыми у этой команды букмекера есть пары.
pub async fn rival_api_teams(
    db: &mut PgConnection,
    book_id: i32,
    api_id: i32,
    api_source: &str,
) -> sqlx::Result<Vec<Option<i32>>> {
    sqlx::query_scalar(
        "SELECT s.team_id FROM team_match_proposals p \
         JOIN source_teams s ON s.id = CASE WHEN p.left_id = $1 THEN p.right_id ELSE p.left_id END \
         WHERE (p.left_id = $1 OR p.right_id = $1) AND p.status IN ('pending', 'confirmed') \
           AND s.source = $3 AND s.id <> $2",
    )
    .bind(book_id)
    .bind(api_id)
    .bind(api_source)
    .fetch_all(db)
    .await
}

pub async fn create_team(db: &mut PgConnection, preferred_name: &str) -> sqlx::Result<i32> {
    sqlx::query_scalar("INSERT INTO teams (preferred_name) VALUES ($1) RETURNING id")
        .bind(preferred_name)
        .fetch_one(db)
        .await
}

/// Связь команды источника с общей командой с записью в аудит.
pub async fn link_source_team(
    db: &mut PgConnection,
    source_team: &SourceTeamState,
    team_id: i32,
    proposal_id: i32,
) -> sqlx::Result<()> {
    sqlx::query("UPDATE source_teams SET team_id = $2 WHERE id = $1")
        .bind(source_team.id)
        .bind(team_id)
        .execute(&mut *db)
        .await?;
    sqlx::query(
        "INSERT INTO team_mapping_audit (source_team_id, old_team_id, new_team_id, actor, method, proposal_id) \
         VALUES ($1, $2, $3, 'auto:match', 'auto_match', $4)",
    )
    .bind(source_team.id)
    .bind(source_team.team_id)
    .bind(team_id)
    .bind(proposal_id)
    .execute(db)
    .await?;
    Ok(())
}

pub async fn confirm_proposal(db: &mut PgConnection, id: i32) -> sqlx::Result<()> {
    sqlx::query(
        "UPDATE team_match_proposals SET status = 'confirmed', reviewed_by = 'auto:match', \
         reviewed_at = now() WHERE id = $1",
    )
    .bind(id)
    .execute(db)
    .await?;
    Ok(())
}

/// Даты матчей этих команд источников в окне: новая связь меняет их скоринг.
pub async fn enqueue_days_of_source_teams(
    db: &mut PgConnection,
    source_team_ids: &[i32],
    from: DateTime<Utc>,
    to: DateTime<Utc>,
) -> sqlx::Result<()> {
    sqlx::query(
        "INSERT INTO team_match_work (match_date) \
         SELECT DISTINCT (f.kickoff AT TIME ZONE 'UTC')::date FROM source_teams s \
         JOIN fixtures f ON f.source = s.source AND ( \
           (s.source_team_id IS NOT NULL AND s.source_team_id IN (f.home_ext_id, f.away_ext_id)) OR \
           (s.source_team_id IS NULL AND s.name IN (f.home, f.away))) \
         WHERE s.id = ANY($1) AND f.kickoff >= $2 AND f.kickoff < $3 \
         ON CONFLICT (match_date) DO UPDATE SET generation = team_match_work.generation + 1",
    )
    .bind(source_team_ids)
    .bind(from)
    .bind(to)
    .execute(db)
    .await?;
    Ok(())
}
