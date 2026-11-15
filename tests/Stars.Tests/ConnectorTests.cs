using System.Text.Json;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Eva;
using Stars.Connectors.Rest;

namespace Stars.Tests;

public class ConnectorTests
{
    private static ConnectorContext Context(string path, string mode = "fixture", int maxRecords = 100) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "feed",
            "match",
            null,
            JsonSerializer.SerializeToElement(new { mode, fixturePath = path, pageSize = 2 }),
            null,
            maxRecords,
            null);

    [Fact]
    public async Task Rest_fixture_streams_pages_and_stable_checkpoint()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-rest-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "version": "v1",
              "entities": [
                {"kind":"team","key":"team:1","externalId":"1","name":"Alpha","sport":"football"},
                {"kind":"team","key":"team:2","externalId":"2","name":"Beta","sport":"football"},
                {"kind":"team","key":"team:3","externalId":"3","name":"Gamma","sport":"football"}
              ],
              "events": [
                {"key":"event:1","kickoff":"2026-11-20T18:00:00Z","participants":[
                  {"key":"team:1","role":"home"},{"key":"team:2","role":"away"}]}
              ]
            }
            """);

        try
        {
            var connector = new FixtureRestConnector();
            var batches = new List<SourceBatch>();
            await foreach (var batch in connector.ReadSnapshotAsync(Context(path), CancellationToken.None))
            {
                batches.Add(batch);
            }

            Assert.Equal(4, batches.Count);
            Assert.Equal(4, batches.Sum(batch => batch.RecordCount));
            Assert.Equal("v1:complete", batches[^1].Checkpoint);
            Assert.Equal(["team:1", "team:2"], batches[0].Entities.Select(entity => entity.SourceKey));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Eva_fixture_uses_dataset_shape_and_snake_case()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-eva-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "snapshotId": 7,
              "rows": [
                {"match_id": 1, "match_date": "2026-11-20", "kickoff_time": "2026-11-20T18:00:00Z",
                 "league": "Russian Premier League", "home_team_id": 10, "home_team_name": "Спартак Москва",
                 "away_team_id": 20, "away_team_name": "Зенит"},
                {"match_id": 2, "match_date": "2026-11-21", "kickoff_time": "2026-11-21T16:00:00Z",
                 "league": "Russian Premier League", "home_team_id": 30, "home_team_name": "Рубин",
                 "away_team_id": 20, "away_team_name": "Зенит"}
              ]
            }
            """);

        try
        {
            var connector = new EvaDataHubConnector();
            var batches = new List<SourceBatch>();
            await foreach (var batch in connector.ReadSnapshotAsync(Context(path), CancellationToken.None))
            {
                batches.Add(batch);
            }

            var entities = batches.SelectMany(batch => batch.Entities).ToArray();
            var events = batches.SelectMany(batch => batch.Events).ToArray();
            Assert.Contains(entities, entity => entity.SourceKey == "team:10" && entity.DisplayName == "Спартак Москва");
            Assert.Contains(entities, entity => entity.SourceKey == "league:russian premier league");
            Assert.Equal(2, events.Length);
            Assert.All(events, sourceEvent => Assert.Equal(2, sourceEvent.Participants.Count));
            Assert.Contains(batches, batch => batch.Checkpoint?.EndsWith(":complete") == true);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Eva_fixture_honours_record_ceiling()
    {
        var path = Path.Combine(Path.GetTempPath(), $"stars-eva-limit-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, """
            {
              "rows": [
                {"match_id": 1, "match_date": "2026-11-20", "home_team_id": 1, "home_team_name": "A",
                 "away_team_id": 2, "away_team_name": "B"},
                {"match_id": 2, "match_date": "2026-11-21", "home_team_id": 1, "home_team_name": "A",
                 "away_team_id": 3, "away_team_name": "C"}
              ]
            }
            """);

        try
        {
            var connector = new EvaDataHubConnector();
            var batches = new List<SourceBatch>();
            await foreach (var batch in connector.ReadSnapshotAsync(
                               Context(path, maxRecords: 1),
                               CancellationToken.None))
            {
                batches.Add(batch);
            }

            Assert.Single(batches.SelectMany(batch => batch.Events));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
