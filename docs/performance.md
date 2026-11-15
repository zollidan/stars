# STARS — performance и нагрузочное тестирование (v1)

Дата: **2026-11-15**  
Объект: текущий Docker-образ `stars-api:perf` (Release, ASP.NET Core 10) + PostgreSQL 18.  
Изменений в коде приложения не делалось: только тестовые данные, замеры и эта документация.

## 1. Стенд

| Параметр | Значение |
| --- | --- |
| Хост | Fedora 44, 20 CPU, 31 GiB RAM, NVMe |
| Контейнеры | podman 5.8.7, отдельная user-defined network |
| API | `stars-api:perf`, образ 250 MB, Release |
| PostgreSQL | `postgres:18-alpine`, 307 MB, дефолтная конфигурация |
| `max_connections` | 100 (дефолт) |
| Worker | один `ProcessingJobWorker` в процессе API |
| Нагрузчики | `hey 0.1.5`, `vegeta v12`, `ghz` (запуск с хоста, loopback) |
| Трафик | HTTP/1.1 для `/api/*`, gRPC h2c для `:5081` |

Это локальный однохостовый стенд. Абсолютные цифры нельзя переносить на production
без учёта TLS, reverse proxy, сети, аутентификации и реального профиля запросов.

## 2. Датасеты

| Датасет | Сущности | События | Canonical | Links | Jobs | DB size |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Baseline (historical) | 16 | 8 | 4 | 8 | 23 | 9.5 MB |
| Synthetic auto (500 команд/провайдер) | 1 002 | 1 000 | 500 | 1 000 | 1 522 | 15 MB |
| Synthetic review (1 000 команд/провайдер) | 2 002 | 2 000 | 0 | 0 | 3 041 | 34 MB |

Synthetic auto: уникальные имена (`PerfXXXXXXXX FC`) в двух провайдерах, совпадающие
fixtures. Проверяет auto-link, создание master-сущностей и запись 1 000 связей.

Synthetic review: имена `Synthetic Club 0000`, `Synthetic Club 0001`, ... — специально
похожие. Из-за token-set similarity и margin 2.7% < 3% все пары уходят в review. Это
**стресс-тест review/candidate/evidence path**, а не оценка качества матчинга.

Synthetic-наборы были сгенерированы для исторических замеров и в поставку не входят.

## 3. Потребление в покое

Замер по дельте `cpu_time` за 10 секунд, память — cgroup working set.

| Контейнер | CPU | RAM | PIDs |
| --- | ---: | ---: | ---: |
| `stars-api` | ~0.005 cores | ~127 MB | 43 |
| `stars-postgres` | ~0.002 cores | ~84 MB | 29 |

При пустом датасете и без нагрузки система практически не потребляет CPU.

## 4. Read API: отдельные endpoints

### 4.1 Базовый датасет (16 сущностей, 4 master)

`hey -z 20s -c 50`, все ответы 200.

| Endpoint | RPS | avg | p50 | p95 | p99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `GET /api/sources` | 13 017 | 3.8 ms | 3.7 ms | 6.1 ms | 7.6 ms |
| `GET /api/directory` | 12 001 | 4.2 ms | 4.0 ms | 6.3 ms | 7.5 ms |
| `GET /api/directory/{id}` | 7 202 | 6.9 ms | 6.8 ms | 9.8 ms | 11.4 ms |
| `GET /api/review` | 10 022 | 5.0 ms | 4.8 ms | 7.5 ms | 8.8 ms |
| `GET /api/metrics` | 14 264 | 3.5 ms | 3.3 ms | 5.6 ms | 6.9 ms |
| `GET /api/resolve?...` | 22 165 | 2.3 ms | 2.1 ms | 3.6 ms | 4.5 ms |

### 4.2 Synthetic auto (500 master, 1 000 links, 1 002 сущности)

`hey -z 15s -c 50`, все ответы 200.

| Endpoint | RPS | avg | p50 | p95 | p99 |
| --- | ---: | ---: | ---: | ---: | ---: |
| `GET /api/sources` | 7 590 | 6.6 ms | 6.5 ms | 9.0 ms | 10.4 ms |
| `GET /api/directory` | **1 050** | **47.6 ms** | 46.0 ms | 67.3 ms | **79.2 ms** |
| `GET /api/directory/{id}` | 5 595 | 8.9 ms | 8.8 ms | 11.0 ms | 13.3 ms |
| `GET /api/review` | 10 028 | 5.0 ms | 4.9 ms | 6.9 ms | 8.2 ms |
| `GET /api/metrics` | 10 664 | 4.7 ms | 4.5 ms | 6.7 ms | 8.1 ms |
| `GET /api/resolve?...` | 18 610 | 2.7 ms | 2.6 ms | 3.9 ms | 4.7 ms |

`/api/directory` — первый явный bottleneck при росте числа master-сущностей: endpoint
возвращает весь справочник, все source links и имена без пагинации. При 500 master
p99 уже 79 ms, throughput падает до ~1 050 RPS.

## 5. Mixed read load

### 5.1 Базовый датасет, 5 000 RPS, 30 секунд

`vegeta attack -rate 5000 -duration 30s -workers 60`, смесь `/sources`, `/directory`,
`/review`, `/metrics`, `/resolve`, `/directory/{id}`.

| Показатель | Значение |
| --- | ---: |
| Запросов | 150 000 |
| Throughput | 4 999.84 RPS |
| Success | 100% |
| p50 | 1.16 ms |
| p95 | 2.37 ms |
| p99 | 3.46 ms |
| max | 128.8 ms |
| Передано | 319.5 MB |

Ресурсы (сэмплирование каждые 2 с):

| Контейнер | CPU avg / max | RAM avg / max |
| --- | ---: | ---: |
| `stars-api` | 2.16 / 2.21 cores | 143 / 165 MB |
| `stars-postgres` | 4.98 / 5.37 cores | 335 / 345 MB |

### 5.2 Synthetic auto, 1 000 RPS, 20 секунд

`vegeta attack -rate 1000 -duration 20s -workers 80`; в targets был тяжёлый
`/api/directory` и resolve-запросы.

| Показатель | Значение |
| --- | ---: |
| Запросов | 20 000 |
| Throughput | 999.97 RPS |
| Success | 100% |
| p50 | 1.55 ms |
| p95 | 7.29 ms |
| p99 | 10.86 ms |
| max | 24.65 ms |
| Передано | 2.45 GB |

| Контейнер | CPU avg / max | RAM avg / max |
| --- | ---: | ---: |
| `stars-api` | 1.68 / 1.84 cores | 222 / 237 MB |
| `stars-postgres` | 2.13 / 2.38 cores | 114 / 115 MB |

## 6. gRPC read API

`ghz --insecure --call stars.v1.StarsRegistry/ResolveBySourceId -c 50 -z 15s`.
Один и тот же master запрашивается по source key.

| Показатель | Значение |
| --- | ---: |
| Запросов | 340 662 |
| Throughput | 22 710 RPS |
| p50 | 1.98 ms |
| p95 | 2.93 ms |
| p99 | 3.49 ms |
| OK | 340 630 |
| Unavailable | 32 (0.009%) |

Ошибки — `use of closed network connection` при высоком concurrency / churn
HTTP/2-соединений, не прикладные 5xx. Одиночный холодный вызов до прогрева — ~22.6 ms,
прогретый p50 — ~2 ms.

Ресурсы:

| Контейнер | CPU avg / max | RAM avg / max |
| --- | ---: | ---: |
| `stars-api` | 2.60 / 2.72 cores | 130 / 131 MB |
| `stars-postgres` | 10.82 / 10.91 cores | 88 MB |

Высокая нагрузка на PostgreSQL объясняется тем, что `ResolveBySourceId` на каждый вызов
делает join по source entity + active link. API при этом почти не нагружен. Для роста
этого пути нужен кэш/read-model или батчинг, а не увеличение числа API-реплик.

## 7. Sync и запись

### 7.1 Конкурентный sync

`POST /api/sources/{feedId}/sync`, 5 concurrent, 10 секунд:

- 207 ответов `200`;
- 396 950 ответов `409 busy`;
- 409 отдаются мгновенно, без 5xx.

Это ожидаемое backpressure: на один feed допускается один sync; параллельные запросы
не запускают второй проход и не создают гонку.

`POST /api/sync` (все feeds), 2 concurrent, 10 секунд:

| Показатель | Значение |
| --- | ---: |
| Успешных | 296 |
| Throughput | 29.4 RPS |
| avg | 67.9 ms |
| p50 | 67.0 ms |
| p95 | 92.7 ms |
| p99 | 103.9 ms |
| Ошибки | 0 |

### 7.2 Первый импорт и matching

| Датасет | Записей | Sync avg / max | Startup до ready | Jobs drain | Jobs/s | Итог |
| --- | ---: | ---: | ---: | ---: | ---: | --- |
| Baseline | 24 | ~0.10 s / — | — | 31 ms | — | 4 master, 8 links |
| Auto 500 | 2 011 | 0.82 s / 0.99 s | 3.25 s | 39.7 s | ~38 | 500 master, 1 000 links, 0 review |
| Review 1000 | 4 021 | 1.47 s / 1.87 s | 4.65 s | 121.5 s | ~25 | 6 000 pending candidates |

Ресурсы во время импорта/матчинга:

| Датасет | API CPU avg / max | API RAM avg / max | PG CPU avg / max | PG RAM avg / max |
| --- | ---: | ---: | ---: | ---: |
| Auto 500 | 0.34 / 1.35 cores | 129 / 140 MB | 0.49 / 0.59 cores | 45 / 48 MB |
| Review 1000 | 0.24 / 1.38 cores | 107 / 140 MB | 0.59 / 0.73 cores | 58 / 72 MB |

Импорт упирается не в CPU, а в количество последовательных DB-операций в worker:
один job = загрузка профиля, кандидаты, evidence и транзакция записи. Это хороший
кандидат на батчинг и увеличение числа worker-ов, но только после настройки блокировок.

### 7.3 Повторный sync (идемпотентность)

На auto-датасете 5 повторных `POST /api/sync`:

- 0.73–0.99 s на запрос;
- `sourceEntities`/`sourceEvents` не изменились;
- `masterEntities=500`, `activeLinks=1000`, `pendingReviews=0`;
- `pendingJobs=0`, `doneJobs=1522` не изменился;
- `recordsChanged` увеличивался только на первом импорте.

## 8. Найденная проблема: connection pool vs `max_connections`

После 30-секундного mixed load на 5 000 RPS с **дефолтным** Npgsql pool API занял
около 100 соединений к PostgreSQL. `pg_stat_activity`/PIDs дошли до 109, а новый
`psql` уже не мог подключиться:

```text
FATAL: sorry, too many clients already
```

Сам API при этом отдал 100% успешных ответов: пул успел занять все доступные слоты.
Но операционный запас закончился — миграции, админ, мониторинг и второй worker
подключиться бы не смогли.

Все последующие тесты запускались с:

```text
Maximum Pool Size=20
```

Рекомендация для деплоя без изменения кода:

- держать `Maximum Pool Size` ниже `max_connections` с запасом (например, 40 при
  `max_connections=100`), либо поднять `max_connections`/поставить PgBouncer;
- отдельно проверить, что connection string API и worker используют один и тот же
  лимит;
- мониторить `pg_stat_activity` и `Npgsql` pool metrics.

## 9. Выводы

1. **Read API быстрый** на базовых данных: 7–22 тыс. RPS на endpoint при p99 < 12 ms.
2. **`/api/directory` не масштабируется линейно**: уже 500 master дают ~1 050 RPS и
   p99 ~79 ms из-за отсутствия пагинации и полного ответа. Это первый кандидат на
   оптимизацию.
3. **gRPC resolve быстрый по latency**, но очень дорогой для PostgreSQL:
   ~22.7 тыс RPS требуют ~10.8 cores PostgreSQL. Нужен кэш/read-model при росте.
4. **Импорт и matching корректны и идемпотентны**, но throughput ~25–38 jobs/s на
   одном worker. CPU почти не используется; оптимизировать нужно число round-trip
   и батчинг, а не железо.
5. **Review path дорогой по записи**: 6 000 candidates + evidence на 2 000 событий.
   Это ожидаемо для похожих имён, но показывает будущую нагрузку на review UI/API.
6. **Нужна настройка пула соединений**: дефолтные 100 + `max_connections=100` не
   оставляют запаса.
7. **Текущий прототип пригоден** для локального/небольшого справочника; для
   production-нагрузки нужны пагинация, пул, кэш read-path и батчинг worker-а.

## 10. Как воспроизвести

```bash
# 1. Собрать образ текущего HEAD
podman build -t stars-api:perf .

# 2. Сеть и PostgreSQL
podman network create stars-perf
podman run -d --name perf-pg --network stars-perf \
  -e POSTGRES_USER=stars -e POSTGRES_PASSWORD=stars -e POSTGRES_DB=stars_perf \
  postgres:18-alpine

# 3. API
podman run -d --name perf-api --network stars-perf \
  -e ConnectionStrings__Stars='Host=perf-pg;Port=5432;Database=stars_perf;Username=stars;Password=stars;Maximum Pool Size=20' \
  -p 5084:5080 -p 5085:5081 stars-api:perf

# 4. Дождаться /health/ready и пустой очереди
curl http://127.0.0.1:5084/api/metrics | jq

# 5. Read load
hey -z 20s -c 50 http://127.0.0.1:5084/api/directory
vegeta attack -targets targets.txt -rate 5000 -duration 30s -workers 60 > results.bin
vegeta report -type=text results.bin

# 6. gRPC
ghz --insecure --proto src/Stars.Api/Protos/stars.proto \
  --call stars.v1.StarsRegistry/ResolveBySourceId -c 50 -z 15s \
  -d '{"source_feed_id":"<eva-feed-id>","entity_kind":"team","source_key":"team:10"}' \
  127.0.0.1:5085

# 7. Импорт тестового набора выполняется через настроенный provider/feed
```

## 11. Как измерить CPU именно matching

Важно разделять три фазы:

1. **startup / migration**;
2. **sync + ingestion** (`SourceSyncService`);
3. **matching worker** (`ProcessingJobWorker` + `MatchingProcessor`).

`podman stats --no-stream` показывает накопительный `cpu_time`, а не мгновенный CPU.
Поэтому корректно считать дельту:

```text
cores = (cpu_time_after - cpu_time_before) / wall_seconds
```

Готовый скрипт:

```bash
STARS_API_URL=http://127.0.0.1:5080 \
API_CONTAINER=stars-api PG_CONTAINER=stars-postgres \
./scripts/perf/measure-matching.sh
```

Он ждёт, пока `/api/metrics` покажет `pendingJobs + processingJobs > 0`, сэмплирует
`cpu_time` контейнеров API и PostgreSQL, ждёт опустошения очереди и печатает средний CPU
в ядрах и память.

Замеры на текущем HEAD, только фаза matching, без read-нагрузки:

| Датасет | Длительность | API avg / max | PostgreSQL avg / max | Jobs/s |
| --- | ---: | ---: | ---: | ---: |
| Auto 500 (1 522 jobs) | 39.7 s | **0.28 / 0.63 cores** | **0.49 / 0.59 cores** | ~38 |
| Review 1000 (3 041 job) | 121.5 s | **0.21 / 0.51 cores** | **0.59 / 0.73 cores** | ~25 |

То есть matching сейчас занимает примерно **0.7–0.8 ядра суммарно** на 20-ядерном
хосте, или порядка **3–4% CPU хоста**. Это не 100% CPU. Startup/ingestion заметно
дороже: API до ~1.35–1.38 cores, PostgreSQL до ~0.68 cores, но это миграции, sync и
первичная запись, а не пересчёт всей справочной базы.

Для проверки, что поиск не превратился в полный скан, полезно посмотреть план
кандидатного запроса:

```sql
EXPLAIN (ANALYZE, BUFFERS)
SELECT id FROM source_entities
WHERE entity_kind = 'team'
  AND normalized_name % 'spartak moskva'
ORDER BY similarity(normalized_name, 'spartak moskva') DESC
LIMIT 24;
```

Ожидаемый план — Bitmap/Index Scan по `ix_source_entities_name_trgm`, а не Seq Scan.
Если там появляется Seq Scan, нужно проверить `pg_trgm`, GIN-индекс и статистику.

## 12. Ограничения

- Один хост, loopback, без TLS/прокси/аутентификации и rate limiting.
- PostgreSQL с дефолтными настройками, без тюнинга `shared_buffers`, `work_mem`,
  autovacuum и без PgBouncer.
- Один worker; результаты не показывают масштабирование по worker-ам.
- Synthetic-датасеты специально простые/похожие; это не оценка качества матчинга.
  Качество на реальных данных — в [legacy-eval.md](legacy-eval.md).
- `podman stats` CPU/память сэмплировались раз в 1–2 секунды; p99 и пики —
  приблизительные.
- Размер ответа `/api/directory` зависит от числа master/links; сравнение базового и
  large корректно только внутри своего датасета.
