use sqlx::PgPool;

use super::{identities, matching, repository, schemas, service};

async fn observe(db: &PgPool, source: &str, fixtures: serde_json::Value) {
    service::save_observations(
        db,
        serde_json::from_value::<schemas::ObservationsRequest>(serde_json::json!({
            "source": source, "fixtures": fixtures,
        }))
        .unwrap(),
    )
    .await
    .unwrap();
}

#[sqlx::test(migrations = "./migrations")]
#[ignore = "requires DATABASE_URL; creates and removes an isolated test database"]
async fn identities_resolve_by_external_id_and_history(db: PgPool) {
    observe(&db, "api-sport-ru", serde_json::json!([
        {"event_id": 1, "home": "Home", "away": "Away", "home_ext_id": 10, "away_ext_id": 20, "observed_at": "2026-10-08T12:00:00Z"},
        {"event_id": 2, "home": "Duplicate", "away": "Duplicate", "home_ext_id": 30, "away_ext_id": 31, "observed_at": "2026-10-08T12:00:00Z"}
    ])).await;
    sqlx::raw_sql("INSERT INTO teams (id, preferred_name) VALUES (1, 'Home'), (2, 'Away');
        UPDATE source_teams SET team_id = CASE name WHEN 'Home' THEN 1 ELSE 2 END WHERE name IN ('Home', 'Away');")
        .execute(&db).await.unwrap();
    // Одно историческое имя ведёт к общей команде; несвязанная одноимённая запись
    // делает другое имя неоднозначным. Внешний ID при этом остаётся точным.
    sqlx::raw_sql("INSERT INTO source_team_names (source_team_id, name, normalized_name, first_seen_at, last_seen_at)
        SELECT id, 'Old Home', 'old home', now(), now() FROM source_teams WHERE source = 'api-sport-ru' AND name = 'Home';
        INSERT INTO source_teams (source, name, normalized_name, first_seen_at, last_seen_at)
        VALUES ('api-sport-ru', 'Home', 'home', now(), now());")
        .execute(&db).await.unwrap();
    let resolved = identities::resolve(
        &db,
        identities::IdentityRequest {
            source: "api-sport-ru".into(),
            external_ids: vec!["10".into()],
            names: vec!["Home".into(), "Old Home".into(), "Duplicate".into()],
            team_ids: vec![],
        },
    )
    .await
    .unwrap();
    assert_eq!(resolved.external.len(), 1);
    assert_eq!(resolved.named.len(), 1);
    assert_eq!(
        resolved.named["Old Home"].team_id,
        resolved.external["10"].team_id
    );
    assert!(
        identities::resolve(
            &db,
            identities::IdentityRequest {
                source: String::new(),
                external_ids: vec!["1".into(); 2001],
                names: vec![],
                team_ids: vec![],
            }
        )
        .await
        .is_err()
    );
}

#[sqlx::test(migrations = "./migrations")]
#[ignore = "requires DATABASE_URL; creates and removes an isolated test database"]
async fn fixtures_resolve_by_external_id_or_name_without_fallback(db: PgPool) {
    sqlx::raw_sql("INSERT INTO teams (id, preferred_name) VALUES (1, 'One'), (2, 'Two'), (3, 'Three'), (4, 'Four');
        INSERT INTO source_teams (id, team_id, source, source_team_id, name, normalized_name, first_seen_at, last_seen_at) VALUES
        (1, 1, 'api-sport-ru', '10', 'Shared', 'shared', now(), now()),
        (2, 2, 'api-sport-ru', '20', 'Shared', 'shared', now(), now()),
        (3, 3, 'api-sport-ru', NULL, 'Shared', 'shared', now(), now()),
        (4, 4, 'marathon', NULL, 'Shared', 'shared', now(), now()),
        (5, NULL, 'marathon', '10', 'Shared', 'shared', now(), now());
        INSERT INTO fixtures (source, event_id, home, away, home_ext_id, away_ext_id, kickoff, observed_at) VALUES
        ('api-sport-ru', '1', 'Renamed', 'Shared', '10', '20', '2026-10-08T00:00:00Z', now()),
        ('api-sport-ru', '3', 'Shared', 'Shared', '999', NULL, '2026-10-08T12:00:00Z', now()),
        ('marathon', '2', 'Shared', 'Missing', NULL, NULL, '2026-10-08T12:00:00Z', now()),
        ('marathon', '4', 'Shared', 'Shared', NULL, NULL, '2026-10-09T00:00:00Z', now());")
        .execute(&db).await.unwrap();
    let from = chrono::NaiveDate::from_ymd_opt(2026, 10, 8)
        .unwrap()
        .and_time(chrono::NaiveTime::MIN)
        .and_utc();
    let rows = repository::fixtures_between(
        &mut db.acquire().await.unwrap(),
        from,
        from + chrono::TimeDelta::days(1),
    )
    .await
    .unwrap();
    let resolved: Vec<_> = rows
        .iter()
        .map(|r| {
            (
                r.source.as_str(),
                r.event_id.as_str(),
                r.home_source_id,
                r.home_team_id,
                r.away_source_id,
                r.away_team_id,
            )
        })
        .collect();
    assert_eq!(
        resolved,
        vec![
            ("api-sport-ru", "1", Some(1), Some(1), Some(2), Some(2)),
            ("api-sport-ru", "3", None, None, Some(3), Some(3)),
            ("marathon", "2", Some(4), Some(4), None, None),
        ]
    );
}

#[sqlx::test(migrations = "./migrations")]
#[ignore = "requires DATABASE_URL; creates and removes an isolated test database"]
async fn observations_match_and_replay(db: PgPool) {
    let fixture = serde_json::json!([{
        "event_id": 1, "home": "Arsenal", "away": "Chelsea",
        "kickoff": "2026-10-08T18:00:00Z", "observed_at": "2026-10-08T12:00:00Z"
    }]);
    observe(&db, "api-sport-ru", serde_json::json!([{
        "event_id": 10, "home": "Arsenal", "away": "Chelsea", "home_ext_id": 100, "away_ext_id": 200,
        "kickoff": "2026-10-08T18:00:00Z", "observed_at": "2026-10-08T12:00:00Z"
    }])).await;
    observe(&db, "marathon", fixture.clone()).await;
    let day = chrono::NaiveDate::from_ymd_opt(2026, 10, 8).unwrap();
    assert_eq!(
        matching::process_day(&db, day)
            .await
            .unwrap()
            .unwrap()
            .confirmed,
        2
    );
    observe(&db, "marathon", fixture).await;
    assert_eq!(
        matching::process_day(&db, day)
            .await
            .unwrap()
            .unwrap()
            .confirmed,
        0
    );
    let count: i64 = sqlx::query_scalar("SELECT count(*) FROM teams")
        .fetch_one(&db)
        .await
        .unwrap();
    assert_eq!(count, 2);
    let count: i64 = sqlx::query_scalar("SELECT count(*) FROM team_match_evidence")
        .fetch_one(&db)
        .await
        .unwrap();
    assert_eq!(count, 2);
    // Поздний повтор не меняет свежую карточку.
    observe(
        &db,
        "marathon",
        serde_json::json!([{
            "event_id": 1, "home": "Stale", "away": "Stale", "observed_at": "2026-10-07T12:00:00Z"
        }]),
    )
    .await;
    let name: String = sqlx::query_scalar("SELECT home FROM fixtures WHERE source = 'marathon'")
        .fetch_one(&db)
        .await
        .unwrap();
    assert_eq!(name, "Arsenal");
}
