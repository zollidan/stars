//! HTTP-ручки справочника: чтение, алиасы и приём наблюдений от источников.
//! Ручных связей (link/unlink, решения по предложениям) нет —
//! связи ставит автоматическое подтверждение.

use axum::{
    Json, Router,
    extract::{Path, Query, State},
    http::StatusCode,
    routing::{delete, get, post},
};
use sqlx::PgPool;

use super::schemas::{
    DirectoryFilter, ObservationsRequest, ObservationsResponse, PageQuery, ProposalsFilter,
    SourceTeamHistoryResponse, SourceTeamResponse, SourceTeamsFilter, TeamAliasCreate,
    TeamAliasDeleteResponse, TeamAliasItem, TeamAliasResponse, TeamDirectoryResponse,
    TeamListResponse, TeamProposalListResponse,
};
use super::service;
use crate::error::AppResult;

pub fn router() -> Router<PgPool> {
    Router::new()
        .route("/identities", post(resolve_identities))
        .route("/teams", get(list_teams))
        .route("/source-teams", get(list_source_teams))
        .route("/source-teams/directory", get(team_directory))
        .route("/source-teams/{id}/history", get(history))
        .route("/team-proposals", get(proposals))
        .route("/aliases", get(list_aliases).post(create_alias))
        .route("/aliases/{id}", delete(delete_alias))
        .route("/observations", post(save_observations))
}

async fn resolve_identities(
    State(db): State<PgPool>,
    Json(request): Json<super::identities::IdentityRequest>,
) -> AppResult<Json<super::identities::IdentityResponse>> {
    Ok(Json(super::identities::resolve(&db, request).await?))
}

async fn list_teams(
    State(db): State<PgPool>,
    Query(page): Query<PageQuery>,
) -> AppResult<Json<TeamListResponse>> {
    page.validate()?;
    Ok(Json(service::team_list(&db, &page).await?))
}

async fn list_source_teams(
    State(db): State<PgPool>,
    Query(page): Query<PageQuery>,
    Query(filter): Query<SourceTeamsFilter>,
) -> AppResult<Json<SourceTeamResponse>> {
    page.validate()?;
    Ok(Json(service::source_teams_page(&db, &page, &filter).await?))
}

async fn team_directory(
    State(db): State<PgPool>,
    Query(page): Query<PageQuery>,
    Query(filter): Query<DirectoryFilter>,
) -> AppResult<Json<TeamDirectoryResponse>> {
    page.validate()?;
    Ok(Json(service::team_directory(&db, &page, &filter).await?))
}

async fn history(
    State(db): State<PgPool>,
    Path(id): Path<i32>,
) -> AppResult<Json<SourceTeamHistoryResponse>> {
    Ok(Json(service::source_team_history(&db, id).await?))
}

async fn proposals(
    State(db): State<PgPool>,
    Query(page): Query<PageQuery>,
    Query(filter): Query<ProposalsFilter>,
) -> AppResult<Json<TeamProposalListResponse>> {
    page.validate()?;
    Ok(Json(
        service::proposals_page(&db, &page, filter.status).await?,
    ))
}

async fn list_aliases(State(db): State<PgPool>) -> AppResult<Json<TeamAliasResponse>> {
    Ok(Json(service::list_team_aliases(&db).await?))
}

async fn create_alias(
    State(db): State<PgPool>,
    Json(payload): Json<TeamAliasCreate>,
) -> AppResult<(StatusCode, Json<TeamAliasItem>)> {
    Ok((
        StatusCode::CREATED,
        Json(service::create_team_alias(&db, &payload).await?),
    ))
}

async fn delete_alias(
    State(db): State<PgPool>,
    Path(id): Path<i32>,
) -> AppResult<Json<TeamAliasDeleteResponse>> {
    Ok(Json(service::delete_team_alias(&db, id).await?))
}

async fn save_observations(
    State(db): State<PgPool>,
    Json(request): Json<ObservationsRequest>,
) -> AppResult<Json<ObservationsResponse>> {
    Ok(Json(service::save_observations(&db, request).await?))
}
