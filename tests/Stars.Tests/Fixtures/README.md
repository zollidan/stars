# Legacy corpus

`legacy-corpus.json` is a deterministic sample extracted from the old Rust STARS
`team_directory` dump (2026-11-15). It is committed so CI can guard the normalizer and
matching thresholds without restoring the 29 MB dump.

Regenerate with:

```bash
./scripts/restore-legacy-dump.sh eva-team-directory_backup_2026-11-15_00-00-08.dump
STARS_LEGACY_CONNECTION='Host=127.0.0.1;Port=55433;Database=team_directory;Username=stars;Password=stars' \
  dotnet run --project tools/Stars.LegacyEval -c Release -- \
  --export-corpus=tests/Stars.Tests/Fixtures/legacy-corpus.json
```

Contents:

- 500 cross-provider positive pairs (same legacy `team_id`);
- 500 random cross-provider negative pairs (different teams);
- up to 100 ambiguous normalized names with records from different canonical teams.

The test asserts concrete precision/recall floors and that repeated same-feed names are
not auto-linked. It is a regression guard, not a substitute for the full evaluation.
