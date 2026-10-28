use axum::{
    Json,
    http::{StatusCode, header::RETRY_AFTER},
    response::{IntoResponse, Response},
};
use serde_json::json;

/// Ошибки HTTP-слоя; тело `{"detail": ...}`.
#[derive(Debug)]
pub enum AppError {
    BadRequest(String),
    NotFound(String),
    Conflict(String),
    Validation(String),
    /// Справочник обновляет другая транзакция; клиент повторяет запрос.
    Busy,
    Db(sqlx::Error),
}

impl From<sqlx::Error> for AppError {
    fn from(err: sqlx::Error) -> Self {
        Self::Db(err)
    }
}

impl IntoResponse for AppError {
    fn into_response(self) -> Response {
        let (status, detail) = match self {
            Self::BadRequest(msg) => (StatusCode::BAD_REQUEST, msg),
            Self::NotFound(msg) => (StatusCode::NOT_FOUND, msg),
            Self::Conflict(msg) => (StatusCode::CONFLICT, msg),
            Self::Validation(msg) => (StatusCode::UNPROCESSABLE_ENTITY, msg),
            Self::Busy => {
                let detail =
                    json!({ "detail": "Справочник команд обновляется. Повторите попытку." });
                return (
                    StatusCode::SERVICE_UNAVAILABLE,
                    [(RETRY_AFTER, "5")],
                    Json(detail),
                )
                    .into_response();
            }
            Self::Db(err) => {
                eprintln!("database error: {err}");
                (
                    StatusCode::INTERNAL_SERVER_ERROR,
                    "Internal Server Error".into(),
                )
            }
        };
        (status, Json(json!({ "detail": detail }))).into_response()
    }
}

pub type AppResult<T> = Result<T, AppError>;
