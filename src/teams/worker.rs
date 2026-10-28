//! Фоновый цикл матчера: даты из очереди и ежедневный плановый проход.

use std::time::{Duration, Instant};

use sqlx::PgPool;

use super::{matching, repository};

const TICK: Duration = Duration::from_secs(30);
const SWEEP_EVERY: Duration = Duration::from_secs(24 * 3600);
// Плановый проход — 22 даты.
const DAYS_PER_TICK: i64 = 31;

pub fn spawn(db: PgPool) {
    tokio::spawn(async move {
        let mut last_sweep: Option<Instant> = None;
        let mut tick = tokio::time::interval(TICK);
        loop {
            tick.tick().await;
            if last_sweep.is_none_or(|at| at.elapsed() >= SWEEP_EVERY) {
                match repository::enqueue_days(&db, &matching::sweep_days()).await {
                    Ok(()) => last_sweep = Some(Instant::now()),
                    Err(err) => eprintln!("matcher: плановый проход не поставлен: {err}"),
                }
            }
            if let Err(err) = run_pending(&db).await {
                eprintln!("matcher: {err}");
            }
        }
    });
}

async fn run_pending(db: &PgPool) -> sqlx::Result<()> {
    for day in repository::pending_days(db, DAYS_PER_TICK).await? {
        let started = Instant::now();
        match matching::process_day(db, day).await? {
            Some(stats) if stats.matches == 0 => {}
            Some(stats) => println!(
                "matcher {day}: матчей {}, улик {}, подтверждено {} за {:?}",
                stats.matches,
                stats.evidence,
                stats.confirmed,
                started.elapsed()
            ),
            // Справочник занят приёмом: дата остаётся в очереди до следующего тика.
            None => return Ok(()),
        }
    }
    Ok(())
}
