using System.Text.Json;
using Stars.Application.Ingestion;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Eva;

namespace Stars.Tests;

public class BookmakerConnectorTests
{
    private static ConnectorContext Context(string path, string dataset, int maxRecords = 100) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "bookmaker", "match", null,
            JsonSerializer.SerializeToElement(new { mode = "fixture", dataset, fixturePath = path, pageSize = 2 }),
            null, maxRecords, null);

    [Theory]
    [InlineData("odds_v1", false)]
    [InlineData("winline_odds_v1", true)]
    [InlineData("marathon_odds_v1", true)]
    public async Task Bookmaker_rows_preserve_match_context_and_ignore_odds(string dataset, bool hasLeague)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-bookmaker-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {"snapshotId":7,"rows":[
              {"event_id":101,"team1":"Alpha","team2":"Beta","league":"Premier League",
               "start_time":"2026-11-20T18:00:00Z","first_seen_at":"2026-11-14T12:00:00Z",
               "last_seen_at":"2026-11-15T12:00:00Z","p1":1.25,"main_total_line":2.5},
              {"event_id":102,"team1":"Alpha","team2":"Gamma","league":"Premier League",
               "start_time":"2026-11-21T18:00:00Z","first_seen_at":"2026-11-14T12:00:00Z"}
            ]}
            """);
        try
        {
            var batches = new List<SourceBatch>();
            await foreach (var batch in new EvaDataHubConnector().ReadSnapshotAsync(Context(path, dataset), default))
            {
                batches.Add(batch);
            }

            Assert.Equal(2, batches.Sum(batch => batch.RecordCount));
            var teams = batches.SelectMany(b => b.Entities).Where(e => e.EntityKind == "team").ToArray();
            Assert.Equal(3, teams.Length);
            var alpha = Assert.Single(teams, team => team.DisplayName == "Alpha");
            Assert.Equal(hasLeague ? "team:name:alpha:league:premier%20league" : "team:name:alpha", alpha.SourceKey);
            Assert.Null(alpha.ExternalId);
            Assert.Equal(hasLeague ? 1 : 0, batches.Sum(b => b.Entities.Count(e => e.EntityKind == "competition")));
            var events = batches.SelectMany(b => b.Events).ToArray();
            Assert.Equal("match:101", events[0].SourceKey);
            Assert.Equal("101", events[0].ExternalId);
            Assert.Equal(DateTimeOffset.Parse("2026-11-20T18:00:00Z"), events[0].KickoffAt);
            Assert.Equal(DateTimeOffset.Parse("2026-11-15T12:00:00Z"), events[0].ObservedAt);
            Assert.Equal(DateTimeOffset.Parse("2026-11-14T12:00:00Z"), events[1].ObservedAt);
            Assert.Equal(alpha.SourceKey, events[0].Participants.Single(p => p.Role == "home").SourceEntityKey);
            Assert.Equal(hasLeague ? "league:premier league" : null, events[0].CompetitionSourceKey);
            Assert.All(events, value => Assert.False(value.Attributes!.Value.TryGetProperty("p1", out _)));
            Assert.EndsWith(":2:complete", batches[^1].Checkpoint);

            var original = events.Select(value => IdentityHasher.Event(value, hasLeague ? "premier league" : "")).ToArray();
            await File.WriteAllTextAsync(path, (await File.ReadAllTextAsync(path)).Replace("1.25", "9.99"));
            var replay = new List<string>();
            await foreach (var batch in new EvaDataHubConnector().ReadSnapshotAsync(Context(path, dataset), default))
            {
                replay.AddRange(batch.Events.Select(value => IdentityHasher.Event(value, hasLeague ? "premier league" : "")));
            }

            Assert.Equal(original, replay);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Bookmaker_without_kickoff_or_league_still_uses_event_id()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-bookmaker-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {"rows":[{"event_id":10,"team1":"Alpha","team2":"Beta"},
                     {"event_id":11,"team1":"Alpha","team2":"Gamma"}]}
            """);
        try
        {
            var batches = new List<SourceBatch>();
            await foreach (var batch in new EvaDataHubConnector().ReadSnapshotAsync(Context(path, "winline_odds_v1", 1), default))
            {
                batches.Add(batch);
            }

            Assert.Equal(1, batches.Sum(batch => batch.RecordCount));
            var sourceEvent = Assert.Single(batches.SelectMany(batch => batch.Events));
            Assert.Equal("match:10", sourceEvent.SourceKey);
            Assert.Null(sourceEvent.KickoffAt);
            Assert.Null(sourceEvent.CompetitionSourceKey);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"team1\":\"Alpha\",\"team2\":\"Beta\"}")]
    [InlineData("{\"event_id\":1,\"team1\":\" \",\"team2\":\"Beta\"}")]
    public async Task Invalid_bookmaker_identity_is_rejected(string row)
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-bookmaker-invalid-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, $"{{\"rows\":[{row}]}}");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await foreach (var _ in new EvaDataHubConnector().ReadSnapshotAsync(Context(path, "odds_v1"), default)) { }
            });
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Homonymous_teams_in_different_leagues_keep_distinct_source_keys()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-bookmaker-leagues-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {"rows":[{"event_id":1,"team1":"United","team2":"Beta","league":"England"},
                     {"event_id":2,"team1":"United","team2":"Gamma","league":"Scotland"}]}
            """);
        try
        {
            var teams = new List<SourceEntityRecord>();
            await foreach (var batch in new EvaDataHubConnector().ReadSnapshotAsync(Context(path, "marathon_odds_v1"), default))
            {
                teams.AddRange(batch.Entities.Where(e => e.DisplayName == "United"));
            }

            Assert.Equal(2, teams.Count);
            Assert.Equal(2, teams.Select(team => team.SourceKey).Distinct().Count());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Unsupported_dataset_is_rejected_before_connecting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new EvaDataHubConnector().ReadSnapshotAsync(Context("unused", "unknown_v1"), default)) { }
        });
    }
}
