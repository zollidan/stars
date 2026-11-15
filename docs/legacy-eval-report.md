# Legacy team-directory evaluation

> Generated 2026-11-15 06:11 UTC from a read-only copy of the old `team_directory` dump.
> This is a probe of name compatibility and scoring, not a full end-to-end STARS run.

## Dataset

| Metric | Value |
| --- | ---: |
| Source team records | 130,813 |
| Linked source records (ground truth `team_id`) | 26,363 |
| Unlinked source records | 104,450 |
| Canonical teams | 10,097 |
| Historical source names | 131,271 |
| Fixtures | 764,335 |
| Confirmed proposals | 10,108 |
| Pending proposals | 5,105 |
| Records without external ID | 75,459 |
| Records with fixture context | 102,866 |

### Records by provider

| Provider | Records | Linked |
| --- | ---: | ---: |
| api-sport-ru | 55,354 | 10,137 |
| fonbet | 33,522 | 8,189 |
| archive | 19,643 | 0 |
| marathon | 14,901 | 8,037 |
| winline | 7,393 | 0 |

## New normalizer versus the old Rust normalizer

Compared on 26,363 linked names: **1,570** differ (5.96%).
Differences are not automatically regressions: some are better handling of Unicode and aliases.

| Legacy normal form | New normal form | Records |
| --- | --- | ---: |
| `grei` | `grey` | 3 |
| `lokomotiv moscow` | `lokomotiv moskva` | 3 |
| `cska moscow` | `cska moskva` | 2 |
| `dynamo 2 moscow` | `dinamo 2 moskva` | 2 |
| `dynamo brest` | `dinamo brest` | 2 |
| `dynamo dresden u19` | `dinamo dresden u19` | 2 |
| `dynamo kirov` | `dinamo kirov` | 2 |
| `dynamo moscow` | `dinamo moskva` | 2 |
| `dynamo puerto` | `dinamo puerto` | 2 |
| `kol ding` | `kolding` | 2 |
| `obson dynamo` | `obson dinamo` | 2 |
| `raal la luv er` | `raal la luver` | 2 |
| `rodina moscow` | `rodina moskva` | 2 |
| `shepshed dynamo` | `shepshed dinamo` | 2 |
| `spartak moscow` | `spartak moskva` | 2 |
| `torpedo moscow` | `torpedo moskva` | 2 |
| `veles moscow` | `veles moskva` | 2 |
| `03 khil den` | `03 khilden` | 1 |
| `1 bokhol t 1900` | `1 bokholt 1900` | 1 |
| `12 bingel spor` | `12 bingelspor` | 1 |
| `12 iyunya vil ya ayes` | `12 iyunya vilya ayes` | 1 |
| `15 novembru do 20` | `15 novembru u20` | 1 |
| `1deg de maio ec` | `1 de maio ec` | 1 |
| `1deg de marzo` | `1 de marzo` | 1 |
| `1o dezembro` | `1 dezembro` | 1 |

## Pair scoring on real confirmed links

Positive pairs (same legacy `team_id`, different providers): **22,398**.
Random negative pairs (different teams and providers): **50,000**.
Hard negatives sharing the same new normal form but different legacy teams: **312** across **277** ambiguous names.
Sport and country are unknown in the old data, so the probe uses name similarity plus squad-marker hard stops only.

| Threshold | Precision | Recall | F1 | TP | FP |
| --- | ---: | ---: | ---: | ---: | ---: |
| 0.60 | 89.25% | 95.67% | 92.35% | 21,428 | 2,582 |
| 0.65 | 97.57% | 94.31% | 95.92% | 21,124 | 525 |
| 0.70 | 99.31% | 92.60% | 95.84% | 20,740 | 144 |
| 0.75 | 99.79% | 90.37% | 94.85% | 20,241 | 43 |
| 0.80 | 99.94% | 86.07% | 92.49% | 19,278 | 11 |
| 0.85 | 99.98% | 78.92% | 88.21% | 17,676 | 4 |
| 0.88 | 99.98% | 73.56% | 84.76% | 16,475 | 3 |
| 0.90 | 99.98% | 69.54% | 82.03% | 15,575 | 3 |
| 0.95 | 99.98% | 56.15% | 71.91% | 12,576 | 2 |
| 0.99 | 99.98% | 50.08% | 66.73% | 11,217 | 2 |

Positive score distribution: mean 0.908, p50 1.000, p90 1.000.
Random negative distribution: mean 0.350, p50 0.448, p90 0.571.
Hard-negative distribution: mean 1.000, max 1.000, at/above 0.88: 312.

### Context on the same positive pairs

Positive pairs where both sides have fixture context: **20,618** of 22,398.

| Threshold | Precision (context) | Recall (context) | Recall (names only, same pairs) |
| --- | ---: | ---: | ---: |
| 0.70 | 99.99% | 89.26% | 93.43% |
| 0.75 | 99.99% | 85.62% | 91.34% |
| 0.80 | 100.00% | 80.58% | 87.25% |
| 0.85 | 100.00% | 74.98% | 80.39% |
| 0.88 | 100.00% | 69.82% | 75.15% |
| 0.90 | 100.00% | 65.42% | 71.14% |

### Providers in positive pairs

| Provider pair | Positive pairs |
| --- | ---: |
| api-sport-ru ↔ fonbet | 8,231 |
| api-sport-ru ↔ marathon | 8,082 |
| fonbet ↔ marathon | 6,085 |

## Ambiguous names that need context, not names

| Normalized name | Legacy teams | Records |
| --- | ---: | ---: |
| `al ittihad` | 4 | 4 |
| `vitoria` | 4 | 4 |
| `al shabab` | 4 | 4 |
| `andorra` | 3 | 6 |
| `bolivar` | 3 | 6 |
| `porto` | 3 | 4 |
| `al arabi` | 3 | 4 |
| `nacional` | 3 | 3 |
| `al ahli` | 3 | 3 |
| `grey` | 2 | 5 |
| `melgar` | 2 | 5 |
| `lokomotiv moskva` | 2 | 4 |
| `red star` | 2 | 4 |
| `apollon limassol` | 2 | 4 |
| `fas` | 2 | 4 |
| `leopards` | 2 | 4 |
| `ebk` | 2 | 4 |
| `sarpsborg 08` | 2 | 4 |
| `gremio` | 2 | 4 |
| `fiorentina` | 2 | 4 |
| `sassuolo` | 2 | 4 |
| `asane` | 2 | 4 |
| `transinvest` | 2 | 4 |
| `nara` | 2 | 4 |
| `renofa yamaguchi` | 2 | 4 |

## What this says

- Legacy `team_id` gives a strong labelled set: tens of thousands of real cross-provider positives and a large pool of unlabeled name-only records.
- Name-only scoring is already very precise at the current 0.88 threshold, but conservative: roughly 70% of confirmed pairs would auto-link by name alone.
- Lowering the threshold to 0.75–0.80 recovers much more recall with very few random false positives, but the decision must still use margin and context. The 277 ambiguous names prove that equal names are not identity.
- The normalizer divergence is large enough to matter. Turkish dotless letters, Cyrillic `ю/я/й`, `до 21` age markers and `St`/`Sankt` are concrete candidates for fixes.
- 104,450 unlinked records are not ground truth: they include real unlinked teams, stale names and future duplicates. They are useful as hard negatives after manual sampling, not as automatic negatives.

## Next evaluation steps

1. Add fixture context from `fixtures` and re-measure the 0.70–0.85 band.
2. Evaluate top-K retrieval through `pg_trgm`, not just pairwise scores.
3. Replay the 5,105 legacy pending proposals and compare STARS review routing.
4. Extract a deterministic positive/negative/ambiguous corpus into CI without shipping the dump.
