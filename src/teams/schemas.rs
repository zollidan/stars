//! Запросы и ответы API справочника.

use std::collections::BTreeMap;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use sqlx::FromRow;

use crate::error::{AppError, AppResult};

// --- Запросы ---------------------------------------------------------------

fn default_limit() -> i64 {
    25
}

#[derive(Deserialize)]
pub struct PageQuery {
    #[serde(default = "default_limit")]
    pub limit: i64,
    #[serde(default)]
    pub offset: i64,
    #[serde(default)]
    pub search: String,
}

impl PageQuery {
    pub fn validate(&self) -> AppResult<()> {
        if !(1..=100).contains(&self.limit) {
            return Err(AppError::Validation("limit должен быть от 1 до 100".into()));
        }
        if self.offset < 0 {
            return Err(AppError::Validation(
                "offset не может быть отрицательным".into(),
            ));
        }
        if self.search.chars().count() > 200 {
            return Err(AppError::Validation("search длиннее 200 символов".into()));
        }
        Ok(())
    }
}

#[derive(Deserialize)]
pub struct SourceTeamsFilter {
    pub source: Option<String>,
    #[serde(default)]
    pub unmapped: bool,
    pub team_id: Option<i32>,
}

#[derive(Deserialize, Clone, Copy, Default)]
#[serde(rename_all = "lowercase")]
pub enum Mapping {
    #[default]
    All,
    Linked,
    Unlinked,
}

#[derive(Deserialize)]
pub struct DirectoryFilter {
    pub source: Option<String>,
    #[serde(default)]
    pub mapping: Mapping,
    #[serde(default)]
    pub all_sources: bool,
}

#[derive(Deserialize, Clone, Copy, Default, PartialEq, Serialize, sqlx::Type)]
#[serde(rename_all = "lowercase")]
#[sqlx(type_name = "text", rename_all = "lowercase")]
pub enum ProposalStatus {
    #[default]
    Pending,
    Confirmed,
    Rejected,
}

#[derive(Deserialize)]
pub struct ProposalsFilter {
    #[serde(default)]
    pub status: ProposalStatus,
}

#[derive(Deserialize)]
pub struct TeamAliasCreate {
    pub alias: String,
    pub canonical: String,
}

// --- Ответы ----------------------------------------------------------------

#[derive(Serialize, FromRow)]
pub struct TeamItem {
    pub id: i32,
    pub preferred_name: String,
    pub created_at: DateTime<Utc>,
}

#[derive(Serialize)]
pub struct TeamListResponse {
    pub teams: Vec<TeamItem>,
    pub count: i64,
    pub limit: i64,
    pub offset: i64,
}

#[derive(Serialize, FromRow, Clone)]
pub struct SourceTeamItem {
    pub id: i32,
    pub team_id: Option<i32>,
    pub team_name: Option<String>,
    pub source: String,
    pub source_team_id: Option<String>,
    pub name: String,
    pub normalized_name: String,
    pub first_seen_at: DateTime<Utc>,
    pub last_seen_at: DateTime<Utc>,
}

#[derive(Serialize)]
pub struct SourceTeamResponse {
    pub teams: Vec<SourceTeamItem>,
    pub count: i64,
    pub limit: i64,
    pub offset: i64,
    pub sources: Vec<String>,
}

/// Строка указателя: либо общая команда, либо несвязанная команда источника.
#[derive(FromRow)]
pub struct DirectoryRow {
    pub team_id: Option<i32>,
    pub source_id: Option<i32>,
    pub name: String,
}

#[derive(Serialize)]
pub struct TeamDirectoryItem {
    pub key: String,
    pub team_id: Option<i32>,
    pub name: String,
    pub sources: BTreeMap<String, Vec<SourceTeamItem>>,
}

#[derive(Serialize)]
pub struct TeamDirectoryResponse {
    pub teams: Vec<TeamDirectoryItem>,
    pub count: i64,
    pub limit: i64,
    pub offset: i64,
    pub sources: Vec<String>,
}

#[derive(Serialize, FromRow)]
pub struct SourceTeamNameItem {
    pub name: String,
    pub first_seen_at: DateTime<Utc>,
    pub last_seen_at: DateTime<Utc>,
}

#[derive(Serialize, FromRow)]
pub struct TeamMappingAuditItem {
    pub id: i32,
    pub old_team_id: Option<i32>,
    pub new_team_id: Option<i32>,
    pub actor: String,
    pub method: String,
    pub proposal_id: Option<i32>,
    pub created_at: DateTime<Utc>,
}

#[derive(Serialize)]
pub struct SourceTeamHistoryResponse {
    pub team: SourceTeamItem,
    pub names: Vec<SourceTeamNameItem>,
    pub audit: Vec<TeamMappingAuditItem>,
}

#[derive(FromRow)]
pub struct ProposalRow {
    pub id: i32,
    pub left_id: i32,
    pub right_id: i32,
    pub status: ProposalStatus,
    pub reviewed_by: Option<String>,
    pub reviewed_at: Option<DateTime<Utc>>,
}

#[derive(Serialize, FromRow)]
pub struct TeamMatchEvidenceItem {
    pub id: i32,
    #[serde(skip)]
    pub proposal_id: i32,
    pub score: f64,
    pub details: serde_json::Value,
}

#[derive(Serialize)]
pub struct TeamProposalItem {
    pub id: i32,
    pub status: ProposalStatus,
    pub left: SourceTeamItem,
    pub right: SourceTeamItem,
    pub reviewed_by: Option<String>,
    pub reviewed_at: Option<DateTime<Utc>>,
    pub evidence_count: i64,
    pub evidence: Vec<TeamMatchEvidenceItem>,
}

#[derive(Serialize)]
pub struct TeamProposalListResponse {
    pub proposals: Vec<TeamProposalItem>,
    pub count: i64,
    pub limit: i64,
    pub offset: i64,
}

#[derive(Serialize, FromRow)]
pub struct TeamAliasItem {
    pub id: i32,
    pub alias: String,
    pub canonical: String,
    pub created_at: Option<DateTime<Utc>>,
}

#[derive(Serialize)]
pub struct TeamAliasResponse {
    pub aliases: Vec<TeamAliasItem>,
    pub count: usize,
}

#[derive(Serialize)]
pub struct TeamAliasDeleteResponse {
    pub deleted: i32,
}

// --- Наблюдения от источников ----------------------------------------------

/// ID источника: источники шлют числа или строки, храним строкой.
#[derive(Deserialize)]
#[serde(untagged)]
pub enum ExternalId {
    Int(i64),
    Str(String),
}

impl From<ExternalId> for String {
    fn from(value: ExternalId) -> Self {
        match value {
            ExternalId::Int(id) => id.to_string(),
            ExternalId::Str(id) => id,
        }
    }
}

#[derive(Deserialize)]
pub struct FixtureIn {
    pub event_id: ExternalId,
    pub home: String,
    pub away: String,
    pub home_ext_id: Option<ExternalId>,
    pub away_ext_id: Option<ExternalId>,
    pub kickoff: Option<DateTime<Utc>>,
    pub league: Option<String>,
    pub observed_at: DateTime<Utc>,
}

#[derive(Deserialize)]
pub struct ObservationsRequest {
    pub source: String,
    pub fixtures: Vec<FixtureIn>,
}

#[derive(Serialize)]
pub struct ObservationsResponse {
    pub fixtures: u64,
    pub teams: usize,
    pub dirty_days: usize,
}
