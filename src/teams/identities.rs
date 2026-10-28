//! Пакетное чтение подтверждённых ID для потребителей справочника.
use std::collections::BTreeMap;

use serde::{Deserialize, Serialize};
use sqlx::PgPool;

use crate::error::{AppError, AppResult};

#[derive(Deserialize)]
pub struct IdentityRequest {
    #[serde(default)]
    pub source: String,
    #[serde(default)]
    pub external_ids: Vec<String>,
    #[serde(default)]
    pub names: Vec<String>,
    #[serde(default)]
    pub team_ids: Vec<i32>,
}

#[derive(Serialize)]
pub struct Identity {
    pub team_id: i32,
    pub canonical_name: String,
}

#[derive(Serialize)]
pub struct IdentityResponse {
    pub external: BTreeMap<String, Identity>,
    pub named: BTreeMap<String, Identity>,
    pub canonical: BTreeMap<i32, String>,
}

pub async fn resolve(db: &PgPool, request: IdentityRequest) -> AppResult<IdentityResponse> {
    if request.external_ids.len() + request.names.len() + request.team_ids.len() > 2000 {
        return Err(AppError::Validation(
            "Не больше 2000 ключей за запрос".into(),
        ));
    }
    let mut tx = db.begin().await?;
    sqlx::query("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY")
        .execute(&mut *tx)
        .await?;
    let external: Vec<(String, i32, String)> = sqlx::query_as(
        "SELECT s.source_team_id, t.id, t.preferred_name FROM source_teams s \
         JOIN teams t ON t.id = s.team_id WHERE s.source = $1 AND s.source_team_id = ANY($2)",
    )
    .bind(&request.source)
    .bind(&request.external_ids)
    .fetch_all(&mut *tx)
    .await?;
    // Несвязанная запись с тем же именем тоже делает имя неоднозначным.
    let named: Vec<(String, i32, String)> = sqlx::query_as(
        "WITH names AS ( \
           SELECT s.name, s.team_id FROM source_teams s WHERE s.source = $1 AND s.name = ANY($2) \
           UNION ALL SELECT n.name, s.team_id FROM source_team_names n JOIN source_teams s ON s.id = n.source_team_id \
           WHERE s.source = $1 AND n.name = ANY($2) \
         ), resolved AS (SELECT name, min(team_id) AS team_id FROM names GROUP BY name \
           HAVING count(DISTINCT team_id) = 1 AND bool_and(team_id IS NOT NULL)) \
         SELECT r.name, t.id, t.preferred_name FROM resolved r JOIN teams t ON t.id = r.team_id")
        .bind(&request.source).bind(&request.names).fetch_all(&mut *tx).await?;
    let canonical: Vec<(i32, String)> =
        sqlx::query_as("SELECT id, preferred_name FROM teams WHERE id = ANY($1)")
            .bind(&request.team_ids)
            .fetch_all(&mut *tx)
            .await?;
    tx.commit().await?;
    let identities = |rows: Vec<(String, i32, String)>| {
        rows.into_iter()
            .map(|(key, team_id, canonical_name)| {
                (
                    key,
                    Identity {
                        team_id,
                        canonical_name,
                    },
                )
            })
            .collect()
    };
    Ok(IdentityResponse {
        external: identities(external),
        named: identities(named),
        canonical: canonical.into_iter().collect(),
    })
}
