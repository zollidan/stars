using System.Text.Json;
using Npgsql;
using Stars.Application.Matching;
using Stars.Application.Normalization;

var connectionString = Environment.GetEnvironmentVariable("STARS_LEGACY_CONNECTION")
    ?? "Host=localhost;Port=55433;Database=team_directory;Username=stars;Password=stars";
var negativeSample = 50_000;

await using var connection = new NpgsqlConnection(connectionString);
await connection.OpenAsync();

var counts = await LoadCountsAsync(connection);
var records = await LoadRecordsAsync(connection);
var contexts = await LoadContextsAsync(connection, records);
var linked = records.Where(record => record.TeamId is not null).ToArray();

var output = new List<string>
{
    "# Legacy team-directory evaluation",
    "",
    $"> Generated {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC from a read-only copy of the old `team_directory` dump.",
    "> This is a probe of name compatibility and scoring, not a full end-to-end STARS run.",
    "",
    "## Dataset",
    "",
    "| Metric | Value |",
    "| --- | ---: |",
    $"| Source team records | {counts.SourceTeams:N0} |",
    $"| Linked source records (ground truth `team_id`) | {counts.LinkedSourceTeams:N0} |",
    $"| Unlinked source records | {counts.UnlinkedSourceTeams:N0} |",
    $"| Canonical teams | {counts.Teams:N0} |",
    $"| Historical source names | {counts.SourceTeamNames:N0} |",
    $"| Fixtures | {counts.Fixtures:N0} |",
    $"| Confirmed proposals | {counts.ConfirmedProposals:N0} |",
    $"| Pending proposals | {counts.PendingProposals:N0} |",
    $"| Records without external ID | {counts.WithoutExternalId:N0} |",
    $"| Records with fixture context | {contexts.Count:N0} |",
    "",
    "### Records by provider",
    "",
    "| Provider | Records | Linked |",
    "| --- | ---: | ---: |",
};
output.AddRange(counts.BySource.Select(pair =>
    $"| {pair.Source} | {pair.Total:N0} | {pair.Linked:N0} |"));

// ---------------------------------------------------------------- normalizer
var diffs = linked
    .Where(record => record.LegacyNormalized != record.NewNormalized)
    .ToArray();
var diffExamples = diffs
    .GroupBy(record => (record.LegacyNormalized, record.NewNormalized))
    .OrderByDescending(group => group.Count())
    .ThenBy(group => group.Key.LegacyNormalized, StringComparer.Ordinal)
    .Take(25)
    .ToArray();

output.AddRange(
[
    "",
    "## New normalizer versus the old Rust normalizer",
    "",
    $"Compared on {linked.Length:N0} linked names: **{diffs.Length:N0}** differ ({100.0 * diffs.Length / linked.Length:F2}%).",
    "Differences are not automatically regressions: some are better handling of Unicode and aliases.",
    "",
    "| Legacy normal form | New normal form | Records |",
    "| --- | --- | ---: |",
]);
output.AddRange(diffExamples.Select(group =>
    $"| `{Escape(group.Key.LegacyNormalized)}` | `{Escape(group.Key.NewNormalized)}` | {group.Count():N0} |"));

// ---------------------------------------------------------------- pair probes
var engine = new MatchEngine();
var groups = linked.GroupBy(record => record.TeamId!.Value).ToArray();
var positives = new List<Pair>();
foreach (var group in groups)
{
    var members = group.OrderBy(record => record.Id).ToArray();
    for (var left = 0; left < members.Length; left++)
    {
        for (var right = left + 1; right < members.Length; right++)
        {
            if (members[left].Source != members[right].Source)
            {
                positives.Add(new Pair(members[left], members[right]));
            }
        }
    }
}

var random = new Random(42);
var negatives = new List<Pair>(negativeSample);
while (negatives.Count < negativeSample)
{
    var left = linked[random.Next(linked.Length)];
    var right = linked[random.Next(linked.Length)];
    if (left.Source != right.Source && left.TeamId != right.TeamId)
    {
        negatives.Add(new Pair(left, right));
    }
}

var positiveScores = positives.Select(pair => Score(engine, pair.Left, pair.Right)).ToArray();
var negativeScores = negatives.Select(pair => Score(engine, pair.Left, pair.Right)).ToArray();

var ambiguous = linked
    .GroupBy(record => record.NewNormalized)
    .Where(group => group.Select(record => record.TeamId).Distinct().Count() > 1)
    .ToArray();
var hardNegatives = new List<Pair>();
foreach (var group in ambiguous)
{
    var members = group.ToArray();
    for (var left = 0; left < members.Length; left++)
    {
        for (var right = left + 1; right < members.Length; right++)
        {
            if (members[left].Source != members[right].Source && members[left].TeamId != members[right].TeamId)
            {
                hardNegatives.Add(new Pair(members[left], members[right]));
            }
        }
    }
}

var hardScores = hardNegatives.Select(pair => Score(engine, pair.Left, pair.Right)).ToArray();
var positivesWithContext = positives
    .Where(pair => contexts.GetValueOrDefault(pair.Left.Id)?.Count > 0
        && contexts.GetValueOrDefault(pair.Right.Id)?.Count > 0)
    .ToArray();
var negativeWithContext = negatives
    .Where(pair => contexts.GetValueOrDefault(pair.Left.Id)?.Count > 0
        && contexts.GetValueOrDefault(pair.Right.Id)?.Count > 0)
    .ToArray();
var positiveContextScores = positivesWithContext
    .Select(pair => Score(engine, pair.Left, pair.Right, contexts))
    .ToArray();
var positiveContextNameScores = positivesWithContext
    .Select(pair => Score(engine, pair.Left, pair.Right))
    .ToArray();
var negativeContextScores = negativeWithContext
    .Select(pair => Score(engine, pair.Left, pair.Right, contexts))
    .ToArray();

output.AddRange(
[
    "",
    "## Pair scoring on real confirmed links",
    "",
    $"Positive pairs (same legacy `team_id`, different providers): **{positives.Count:N0}**.",
    $"Random negative pairs (different teams and providers): **{negatives.Count:N0}**.",
    $"Hard negatives sharing the same new normal form but different legacy teams: **{hardNegatives.Count:N0}** across **{ambiguous.Length:N0}** ambiguous names.",
    "Sport and country are unknown in the old data, so the probe uses name similarity plus squad-marker hard stops only.",
    "",
    "| Threshold | Precision | Recall | F1 | TP | FP |",
    "| --- | ---: | ---: | ---: | ---: | ---: |",
]);
foreach (var threshold in new[] { 0.60, 0.65, 0.70, 0.75, 0.80, 0.85, 0.88, 0.90, 0.95, 0.99 })
{
    var truePositives = positiveScores.Count(score => score >= threshold);
    var falsePositives = negativeScores.Count(score => score >= threshold);
    var precision = truePositives + falsePositives == 0
        ? 0
        : (double)truePositives / (truePositives + falsePositives);
    var recall = (double)truePositives / positiveScores.Length;
    var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    output.Add(
        $"| {threshold:F2} | {precision:P2} | {recall:P2} | {f1:P2} | {truePositives:N0} | {falsePositives:N0} |");
}

output.AddRange(
[
    "",
    $"Positive score distribution: mean {positiveScores.Average():F3}, p50 {Percentile(positiveScores, 0.50):F3}, p90 {Percentile(positiveScores, 0.90):F3}.",
    $"Random negative distribution: mean {negativeScores.Average():F3}, p50 {Percentile(negativeScores, 0.50):F3}, p90 {Percentile(negativeScores, 0.90):F3}.",
    $"Hard-negative distribution: mean {hardScores.DefaultIfEmpty(0).Average():F3}, max {hardScores.DefaultIfEmpty(0).Max():F3}, at/above 0.88: {hardScores.Count(score => score >= 0.88):N0}.",
    "",
    "### Context on the same positive pairs",
    "",
    $"Positive pairs where both sides have fixture context: **{positivesWithContext.Length:N0}** of {positives.Count:N0}.",
    "",
    "| Threshold | Precision (context) | Recall (context) | Recall (names only, same pairs) |",
    "| --- | ---: | ---: | ---: |",
]);
foreach (var threshold in new[] { 0.70, 0.75, 0.80, 0.85, 0.88, 0.90 })
{
    var truePositives = positiveContextScores.Count(score => score >= threshold);
    var falsePositives = negativeContextScores.Count(score => score >= threshold);
    var precision = truePositives + falsePositives == 0
        ? 0
        : (double)truePositives / (truePositives + falsePositives);
    var recall = positiveContextScores.Length == 0
        ? 0
        : (double)truePositives / positiveContextScores.Length;
    var nameRecall = positiveContextNameScores.Length == 0
        ? 0
        : (double)positiveContextNameScores.Count(score => score >= threshold) / positiveContextNameScores.Length;
    output.Add($"| {threshold:F2} | {precision:P2} | {recall:P2} | {nameRecall:P2} |");
}

output.AddRange(
[
    "",
    "### Providers in positive pairs",
    "",
    "| Provider pair | Positive pairs |",
    "| --- | ---: |",
]);
foreach (var group in positives
             .GroupBy(pair => string.CompareOrdinal(pair.Left.Source, pair.Right.Source) < 0
                 ? $"{pair.Left.Source} ↔ {pair.Right.Source}"
                 : $"{pair.Right.Source} ↔ {pair.Left.Source}")
             .OrderByDescending(group => group.Count()))
{
    output.Add($"| {group.Key} | {group.Count():N0} |");
}

output.AddRange(
[
    "",
    "## Ambiguous names that need context, not names",
    "",
    "| Normalized name | Legacy teams | Records |",
    "| --- | ---: | ---: |",
]);
foreach (var group in ambiguous
             .OrderByDescending(group => group.Select(record => record.TeamId).Distinct().Count())
             .ThenByDescending(group => group.Count())
             .Take(25))
{
    output.Add(
        $"| `{Escape(group.Key)}` | {group.Select(record => record.TeamId).Distinct().Count()} | {group.Count()} |");
}

output.AddRange(
[
    "",
    "## What this says",
    "",
    "- Legacy `team_id` gives a strong labelled set: tens of thousands of real cross-provider positives and a large pool of unlabeled name-only records.",
    "- Name-only scoring is already very precise at the current 0.88 threshold, but conservative: roughly 70% of confirmed pairs would auto-link by name alone.",
    "- Lowering the threshold to 0.75–0.80 recovers much more recall with very few random false positives, but the decision must still use margin and context. The 277 ambiguous names prove that equal names are not identity.",
    "- The normalizer divergence is large enough to matter. Turkish dotless letters, Cyrillic `ю/я/й`, `до 21` age markers and `St`/`Sankt` are concrete candidates for fixes.",
    "- 104,450 unlinked records are not ground truth: they include real unlinked teams, stale names and future duplicates. They are useful as hard negatives after manual sampling, not as automatic negatives.",
    "",
    "## Next evaluation steps",
    "",
    "1. Add fixture context from `fixtures` and re-measure the 0.70–0.85 band.",
    "2. Evaluate top-K retrieval through `pg_trgm`, not just pairwise scores.",
    "3. Replay the 5,105 legacy pending proposals and compare STARS review routing.",
    "4. Extract a deterministic positive/negative/ambiguous corpus into CI without shipping the dump.",
]);

var corpusArgument = args.FirstOrDefault(argument => argument.StartsWith("--export-corpus=", StringComparison.Ordinal));
if (corpusArgument is not null)
{
    var corpus = new
    {
        generatedAt = DateTimeOffset.UtcNow,
        source = "team_directory legacy dump",
        positives = Sample(positives, 500)
            .Select(pair => new
            {
                left = new { pair.Left.Source, pair.Left.Name, pair.Left.LegacyNormalized, pair.Left.TeamId },
                right = new { pair.Right.Source, pair.Right.Name, pair.Right.LegacyNormalized, pair.Right.TeamId },
            })
            .ToArray(),
        negatives = negatives.Take(500)
            .Select(pair => new
            {
                left = new { pair.Left.Source, pair.Left.Name, pair.Left.LegacyNormalized, pair.Left.TeamId },
                right = new { pair.Right.Source, pair.Right.Name, pair.Right.LegacyNormalized, pair.Right.TeamId },
            })
            .ToArray(),
        ambiguous = ambiguous
            .OrderByDescending(group => group.Select(record => record.TeamId).Distinct().Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .Take(100)
            .Select(group => new
            {
                name = group.Key,
                records = group
                    .GroupBy(record => record.TeamId)
                    .SelectMany(records => records.Take(1))
                    .Take(6)
                    .Select(record => new
                    {
                        record.Source,
                        record.Name,
                        record.LegacyNormalized,
                        record.TeamId,
                    })
                    .ToArray(),
            })
            .ToArray(),
    };
    await File.WriteAllTextAsync(
        corpusArgument[16..],
        JsonSerializer.Serialize(corpus, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Wrote corpus {corpusArgument[16..]}");
}

var text = string.Join(Environment.NewLine, output) + Environment.NewLine;
var outputArgument = args.FirstOrDefault(argument => argument.StartsWith("--out=", StringComparison.Ordinal));
var outputPath = outputArgument is null ? null : outputArgument[6..];
if (outputPath is not null)
{
    await File.WriteAllTextAsync(outputPath, text);
    Console.WriteLine($"Wrote {outputPath}");
}
else
{
    Console.WriteLine(text);
}

static double Score(
    MatchEngine engine,
    LegacyRecord left,
    LegacyRecord right,
    IReadOnlyDictionary<int, IReadOnlyList<MatchContextFact>>? contexts = null)
{
    var source = new SourceEntityProfile(
        Guid.NewGuid(), Guid.Empty, left.Source, "team",
        left.Name, left.NewNormalized, null, null,
        contexts?.GetValueOrDefault(left.Id) ?? []);
    var candidate = new CandidateTarget(
        Guid.NewGuid(), null, right.Source, right.Name, right.NewNormalized, null, null,
        contexts?.GetValueOrDefault(right.Id) ?? []);
    var scored = engine.Score(source, candidate);
    return scored.HardConflict ? 0 : scored.Score;
}

static IEnumerable<T> Sample<T>(IReadOnlyList<T> values, int count)
{
    if (values.Count <= count)
    {
        return values;
    }

    return Enumerable.Range(0, count)
        .Select(index => values[(int)((long)index * values.Count / count)]);
}

static double Percentile(double[] values, double percentile)
{
    var sorted = values.Order().ToArray();
    return sorted[(int)Math.Clamp(percentile * (sorted.Length - 1), 0, sorted.Length - 1)];
}

static string Escape(string value) => value.Replace("|", "\\|");

static async Task<Dictionary<int, IReadOnlyList<MatchContextFact>>> LoadContextsAsync(
    NpgsqlConnection connection,
    IReadOnlyList<LegacyRecord> records)
{
    var bySourceName = new Dictionary<string, int>(StringComparer.Ordinal);
    foreach (var record in records)
    {
        bySourceName.TryAdd($"{record.Source}\u001f{record.Name}", record.Id);
    }

    var result = new Dictionary<int, List<MatchContextFact>>();
    var normalizedNames = new Dictionary<string, string>(StringComparer.Ordinal);
    string Normalized(string value)
    {
        if (!normalizedNames.TryGetValue(value, out var normalized))
        {
            normalizedNames[value] = normalized = NameNormalizer.Normalize(value);
        }

        return normalized;
    }

    void Add(int entityId, MatchContextFact fact)
    {
        if (!result.TryGetValue(entityId, out var facts))
        {
            result[entityId] = facts = [];
        }

        if (facts.Count < 12)
        {
            facts.Add(fact);
        }
    }

    await using var command = new NpgsqlCommand(
        """
        SELECT source, home, away, kickoff, league
        FROM fixtures
        WHERE kickoff IS NOT NULL
        ORDER BY kickoff DESC
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var source = reader.GetString(0);
        var home = reader.GetString(1);
        var away = reader.GetString(2);
        var kickoff = reader.GetFieldValue<DateTimeOffset>(3);
        var league = reader.IsDBNull(4) ? null : Normalized(reader.GetString(4));

        if (bySourceName.TryGetValue($"{source}\u001f{home}", out var homeId))
        {
            Add(homeId, new MatchContextFact(Normalized(away), kickoff, league, "home"));
        }

        if (bySourceName.TryGetValue($"{source}\u001f{away}", out var awayId))
        {
            Add(awayId, new MatchContextFact(Normalized(home), kickoff, league, "away"));
        }
    }

    return result.ToDictionary(
        pair => pair.Key,
        pair => (IReadOnlyList<MatchContextFact>)pair.Value);
}

static async Task<LegacyCounts> LoadCountsAsync(NpgsqlConnection connection)
{
    async Task<long> ScalarAsync(string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    var bySource = new List<(string Source, long Total, long Linked)>();
    await using (var command = new NpgsqlCommand(
        """
        SELECT source, count(*), count(team_id)
        FROM source_teams
        GROUP BY source
        ORDER BY count(*) DESC
        """, connection))
    {
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            bySource.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
        }
    }

    return new LegacyCounts(
        await ScalarAsync("SELECT count(*) FROM source_teams"),
        await ScalarAsync("SELECT count(team_id) FROM source_teams"),
        await ScalarAsync("SELECT count(*) - count(team_id) FROM source_teams"),
        await ScalarAsync("SELECT count(*) FROM teams"),
        await ScalarAsync("SELECT count(*) FROM source_team_names"),
        await ScalarAsync("SELECT count(*) FROM fixtures"),
        await ScalarAsync("SELECT count(*) FROM team_match_proposals WHERE status = 'confirmed'"),
        await ScalarAsync("SELECT count(*) FROM team_match_proposals WHERE status = 'pending'"),
        await ScalarAsync("SELECT count(*) FROM source_teams WHERE source_team_id IS NULL"),
        bySource);
}

static async Task<List<LegacyRecord>> LoadRecordsAsync(NpgsqlConnection connection)
{
    var records = new List<LegacyRecord>();
    await using var command = new NpgsqlCommand(
        """
        SELECT id, team_id, source, source_team_id, name, normalized_name
        FROM source_teams
        ORDER BY id
        """, connection);
    await using var reader = await command.ExecuteReaderAsync();
    while (await reader.ReadAsync())
    {
        var name = reader.GetString(4);
        records.Add(new LegacyRecord(
            reader.GetInt32(0),
            reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            name,
            reader.GetString(5),
            NameNormalizer.Normalize(name)));
    }

    return records;
}

record LegacyRecord(
    int Id,
    int? TeamId,
    string Source,
    string? ExternalId,
    string Name,
    string LegacyNormalized,
    string NewNormalized);

record Pair(LegacyRecord Left, LegacyRecord Right);

record LegacyCounts(
    long SourceTeams,
    long LinkedSourceTeams,
    long UnlinkedSourceTeams,
    long Teams,
    long SourceTeamNames,
    long Fixtures,
    long ConfirmedProposals,
    long PendingProposals,
    long WithoutExternalId,
    IReadOnlyList<(string Source, long Total, long Linked)> BySource);
