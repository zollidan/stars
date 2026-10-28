# Sports Taxonomy, Alignment & Reconciliation System

### STARS — open-source, self-hosted платформа для формирования и сопровождения спортивных мастер-данных из пользовательских источников, обеспечивающая устойчивую идентификацию сущностей, проверяемое сопоставление и управляемое подключение новых источников.

## Разработка

```sh
docker compose up -d   # или podman compose up -d — локальный Postgres
export STARS_DATABASE_URL=postgres://stars:stars@localhost:5432/stars
cargo run              # миграции применяются при старте, API на :8080

# интеграционные тесты создают отдельные БД
DATABASE_URL=$STARS_DATABASE_URL cargo test -- --include-ignored
```

| Переменная            | По умолчанию   |
| --------------------- | -------------- |
| `STARS_DATABASE_URL`  | — (обязательна) |
| `STARS_BIND_ADDR`     | `0.0.0.0:8080` |
