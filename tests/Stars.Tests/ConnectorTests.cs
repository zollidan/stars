using System.Net;
using System.Text;
using System.Text.Json;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Rest;

namespace Stars.Tests;

public class ConnectorTests
{
    private static ConnectorContext Context(string json, int maxRecords = 100) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "feed",
            "match",
            "http://provider",
            JsonSerializer.SerializeToElement(new { pageSize = 2 }),
            null,
            maxRecords,
            null,
            new HttpClient(new SnapshotHandler(json)));

    [Fact]
    public async Task Rest_snapshot_streams_pages_and_stable_checkpoint()
    {
        const string json = """
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
            """;

        var batches = new List<SourceBatch>();
        await foreach (var batch in new RestSnapshotConnector().ReadSnapshotAsync(Context(json), CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(4, batches.Count);
        Assert.Equal(4, batches.Sum(batch => batch.RecordCount));
        Assert.Equal("v1:complete", batches[^1].Checkpoint);
        Assert.Equal(["team:1", "team:2"], batches[0].Entities.Select(entity => entity.SourceKey));
    }

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public async Task Rest_snapshot_limits_partial_entity_and_event_pages(int limit)
    {
        var json = JsonSerializer.Serialize(new
        {
            entities = Enumerable.Range(1, 4).Select(id => new { key = $"team:{id}", name = $"Team {id}" }),
            events = Enumerable.Range(1, 4).Select(id => new { key = $"event:{id}" }),
        });

        var batches = new List<SourceBatch>();
        await foreach (var batch in new RestSnapshotConnector().ReadSnapshotAsync(
                           Context(json, maxRecords: limit), CancellationToken.None))
        {
            batches.Add(batch);
        }

        Assert.Equal(limit, batches.Sum(batch => batch.RecordCount));
        Assert.Equal(Math.Min(4, limit), batches.Sum(batch => batch.Entities.Count));
        Assert.Equal(Math.Max(0, limit - 4), batches.Sum(batch => batch.Events.Count));
    }

    [Fact]
    public async Task Rest_snapshot_requires_base_url_and_http_client()
    {
        var context = new ConnectorContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "feed",
            "match",
            null,
            JsonSerializer.SerializeToElement(new { }),
            null,
            100,
            null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new RestSnapshotConnector().ReadSnapshotAsync(context, CancellationToken.None))
            {
            }
        });
    }

    private sealed class SnapshotHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Assert.Equal("http://provider/snapshot", request.RequestUri?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
