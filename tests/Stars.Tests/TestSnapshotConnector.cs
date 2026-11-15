using System.Runtime.CompilerServices;
using System.Text.Json;
using Stars.Connectors.Abstractions;

namespace Stars.Tests;

/// <summary>
/// Deterministic in-process provider used by integration tests. It deliberately exercises
/// two independent feeds with the same real-world teams, transliterations and one
/// ambiguous short name, without reading files or calling external services.
/// </summary>
internal sealed class TestSnapshotConnector : IConnector
{
    public const string ConnectorKey = "test.snapshot";
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-11-15T12:00:00Z");

    public string Key => ConnectorKey;

    public ConnectorCapabilities Capabilities =>
        ConnectorCapabilities.SnapshotOnly("Deterministic in-process test feed.");

    public async IAsyncEnumerable<SourceBatch> ReadSnapshotAsync(
        ConnectorContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();

        yield return context.FeedKey switch
        {
            "eva-matches" => EvaBatch(context.MaxRecords),
            "api-sport-snapshot" => RestBatch(context.MaxRecords),
            _ => throw new InvalidOperationException($"Unknown test feed '{context.FeedKey}'."),
        };
    }

    private static SourceBatch EvaBatch(long maxRecords)
    {
        var rows = new[]
        {
            new EvaRow(5001, "Russian Premier League", 10, "Спартак Москва", 20, "Зенит Санкт-Петербург",
                DateTimeOffset.Parse("2026-11-20T18:00:00Z")),
            new EvaRow(5002, "Ukrainian Premier League", 30, "Динамо", 40, "Шахтёр",
                DateTimeOffset.Parse("2026-11-23T17:00:00Z")),
            new EvaRow(5003, "Russian Premier League", 50, "ЦСКА", 60, "Рубин",
                DateTimeOffset.Parse("2026-11-24T18:00:00Z")),
            new EvaRow(5004, "Russian Premier League", 10, "Спартак Москва", 50, "ЦСКА",
                DateTimeOffset.Parse("2026-11-27T18:00:00Z")),
        }.Take((int)Math.Min(maxRecords, int.MaxValue)).ToArray();

        var entities = new Dictionary<string, SourceEntityRecord>(StringComparer.Ordinal);
        var events = new Dictionary<string, SourceEventRecord>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var homeKey = $"team:{row.HomeId}";
            var awayKey = $"team:{row.AwayId}";
            var competitionKey = $"league:{row.League.ToLowerInvariant()}";
            AddTeam(entities, homeKey, row.HomeId, row.HomeName, null);
            AddTeam(entities, awayKey, row.AwayId, row.AwayName, null);
            entities[competitionKey] = new SourceEntityRecord(
                EntityKinds.Competition, competitionKey, null, row.League, "football", null, null,
                JsonSerializer.SerializeToElement(new { dataset = "api_sport_matches_v1" }), ObservedAt);

            var eventKey = $"match:{row.MatchId}";
            events[eventKey] = new SourceEventRecord(
                eventKey,
                row.MatchId.ToString(),
                "football",
                competitionKey,
                row.Kickoff,
                null,
                [new SourceEventParticipantRecord(homeKey, "home"), new SourceEventParticipantRecord(awayKey, "away")],
                JsonSerializer.SerializeToElement(new { dataset = "api_sport_matches_v1" }),
                ObservedAt);
        }

        return new SourceBatch(entities.Values.ToArray(), events.Values.ToArray(), $"test:eva:{rows.Length}:complete", 1, rows.Length);
    }

    private static SourceBatch RestBatch(long maxRecords)
    {
        SourceEntityRecord Team(string key, string externalId, string name, string? country, params string[] aliases) =>
            new(EntityKinds.Team, key, externalId, name, "football", country,
                aliases.Select(alias => new SourceNameRecord(alias)).ToArray(),
                JsonSerializer.SerializeToElement(new { source = "rest" }), ObservedAt);

        var entities = new[]
        {
            new SourceEntityRecord(EntityKinds.Competition, "competition:rpl", null,
                "Russian Premier League", "football", "RU", null, null, ObservedAt),
            Team("team:spartak-msk", "100", "Spartak Moscow", "RU", "FC Spartak", "Spartak Moskva"),
            Team("team:zenit", "120", "Zenit St Petersburg", "RU", "Zenit"),
            Team("team:cska-msk", "130", "CSKA Moscow", "RU", "CSKA"),
            Team("team:rubin", "140", "Rubin Kazan", "RU", "Rubin"),
            Team("team:dynamo-msk", "110", "Dynamo Moscow", "RU", "Dinamo Moscow"),
            Team("team:dynamo-minsk", "150", "Dinamo Minsk", "BY", "Dynamo Minsk"),
            Team("team:bate", "160", "BATE Borisov", "BY"),
        }.Take((int)Math.Min(maxRecords, int.MaxValue)).ToArray();

        SourceEventRecord Event(string key, string externalId, string competition, string kickoff,
            string home, string away) =>
            new(key, externalId, "football", competition, DateTimeOffset.Parse(kickoff), "scheduled",
                [new SourceEventParticipantRecord(home, "home"), new SourceEventParticipantRecord(away, "away")],
                JsonSerializer.SerializeToElement(new { source = "rest" }), ObservedAt);

        var remaining = Math.Max(0, maxRecords - entities.Length);
        var events = new[]
        {
            Event("event:1001", "1001", "competition:rpl", "2026-11-20T18:00:00Z", "team:spartak-msk", "team:zenit"),
            Event("event:1002", "1002", "competition:rpl", "2026-11-21T18:00:00Z", "team:dynamo-msk", "team:cska-msk"),
            Event("event:1003", "1003", "competition:rpl", "2026-11-24T18:00:00Z", "team:rubin", "team:cska-msk"),
            Event("event:1004", "1004", "competition:belarus", "2026-11-22T16:00:00Z", "team:dynamo-minsk", "team:bate"),
        }.Take((int)Math.Min(remaining, int.MaxValue)).ToArray();

        return new SourceBatch(entities, events, $"test:rest:{entities.Length + events.Length}:complete", 1);
    }

    private static void AddTeam(
        IDictionary<string, SourceEntityRecord> entities,
        string key,
        long id,
        string name,
        string? country) =>
        entities[key] = new SourceEntityRecord(
            EntityKinds.Team,
            key,
            id.ToString(),
            name,
            "football",
            country,
            null,
            JsonSerializer.SerializeToElement(new { dataset = "api_sport_matches_v1" }),
            ObservedAt);

    private sealed record EvaRow(
        long MatchId,
        string League,
        long HomeId,
        string HomeName,
        long AwayId,
        string AwayName,
        DateTimeOffset Kickoff);
}
