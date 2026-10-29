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

## Образ

Образ linux/amd64 публикуется в GHCR вручную: Actions → CI → Run workflow.
Тег — `YYYY.MM.DD-<номер запуска>` (например, `2026.10.30-7`), плюс `latest`
при запуске с `master`.

```yaml
services:
  stars:
    image: ghcr.io/zollidan/stars:2026.10.30-7
    environment:
      STARS_DATABASE_URL: postgres://stars:stars@db:5432/stars
    ports:
      - "8080:8080"
```
