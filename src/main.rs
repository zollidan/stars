use axum::{Router, extract::State, http::StatusCode, routing::get};
use sqlx::{PgPool, Postgres, migrate::MigrateDatabase};

mod error;
mod matcher;
mod normalize;
mod similarity;
mod teams;

#[tokio::main]
async fn main() {
    let addr = std::env::var("STARS_BIND_ADDR").unwrap_or_else(|_| "0.0.0.0:8080".into());
    if std::env::args().nth(1).as_deref() == Some("healthcheck") {
        std::process::exit(i32::from(!healthcheck(&addr)));
    }
    let url = std::env::var("STARS_DATABASE_URL").expect("STARS_DATABASE_URL");

    // Volume Postgres уже инициализирован, init-скрипты не выполнятся: создаём базу сами.
    if !Postgres::database_exists(&url)
        .await
        .expect("check database")
    {
        Postgres::create_database(&url)
            .await
            .expect("create database");
    }
    let pool = PgPool::connect(&url).await.expect("connect");
    sqlx::migrate!().run(&pool).await.expect("migrate");

    teams::spawn_matcher(pool.clone());

    let app = Router::new()
        .route("/health", get(health))
        .merge(teams::router())
        .with_state(pool);
    let listener = tokio::net::TcpListener::bind(&addr).await.expect("bind");
    println!("stars listening on {addr}");
    axum::serve(listener, app)
        .with_graceful_shutdown(shutdown())
        .await
        .expect("serve");
}

/// PID 1 в контейнере не завершается по SIGTERM без обработчика.
async fn shutdown() {
    let mut terminate = tokio::signal::unix::signal(tokio::signal::unix::SignalKind::terminate())
        .expect("SIGTERM handler");
    tokio::select! {
        _ = terminate.recv() => {}
        _ = tokio::signal::ctrl_c() => {}
    }
}

async fn health(State(pool): State<PgPool>) -> StatusCode {
    match sqlx::query("SELECT 1").execute(&pool).await {
        Ok(_) => StatusCode::OK,
        Err(_) => StatusCode::SERVICE_UNAVAILABLE,
    }
}

/// Для HEALTHCHECK в образе без curl: `stars healthcheck`.
fn healthcheck(addr: &str) -> bool {
    use std::io::{Read, Write};
    let port = addr.rsplit(':').next().unwrap_or("8080");
    let Ok(mut stream) = std::net::TcpStream::connect(("127.0.0.1", port.parse().unwrap_or(8080)))
    else {
        return false;
    };
    let _ = stream.set_read_timeout(Some(std::time::Duration::from_secs(3)));
    let mut response = String::new();
    stream.write_all(b"GET /health HTTP/1.0\r\n\r\n").is_ok()
        && stream.read_to_string(&mut response).is_ok()
        && response.split_whitespace().nth(1) == Some("200")
}
