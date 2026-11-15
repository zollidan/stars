using System.Text.Json;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using Eva.Datahub.V1;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Eva;

namespace Stars.Tests;

public class EvaGrpcTests
{
    [Theory]
    [InlineData("api_sport_matches_v1")]
    [InlineData("odds_v1")]
    [InlineData("winline_odds_v1")]
    [InlineData("marathon_odds_v1")]
    public async Task Grpc_import_limits_rows_and_preserves_pinned_resume_offset(string dataset)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = Path.GetTempPath(),
        });
        builder.Configuration.Sources.Clear();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        await using var app = builder.Build();
        app.MapGrpcService<SnapshotService>();
        await app.StartAsync();
        var context = new ConnectorContext(Guid.NewGuid(), Guid.NewGuid(), "test", "match",
            app.Urls.Single(), JsonSerializer.SerializeToElement(new { dataset }),
            $"eva:{dataset}:7:7:10", 3, null);

        var batches = new List<SourceBatch>();
        await foreach (var batch in new EvaDataHubConnector().ReadSnapshotAsync(context, default))
        {
            batches.Add(batch);
        }

        Assert.Equal(3, batches.Sum(batch => batch.RecordCount));
        Assert.Equal(3, batches.Sum(batch => batch.Events.Count));
        Assert.Equal("match:11", batches[0].Events[0].SourceKey);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T18:00:00Z"), batches[0].Events[0].KickoffAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-15T12:00:00Z"), batches[0].Events[0].ObservedAt);
        Assert.Equal("Alpha", batches[0].Entities[0].DisplayName);
        if (dataset != "api_sport_matches_v1")
        {
            Assert.All(batches.SelectMany(batch => batch.Entities), entity => Assert.Null(entity.ExternalId));
            Assert.All(batches.SelectMany(batch => batch.Events), sourceEvent => Assert.Equal("football", sourceEvent.SportCode));
        }
        Assert.Equal($"eva:{dataset}:7:7:13", batches[0].Checkpoint);
        Assert.Equal($"eva:{dataset}:7:7:13:complete", batches[^1].Checkpoint);
    }

    [BindServiceMethod(typeof(SnapshotService), nameof(BindService))]
    public class SnapshotService
    {
        public static void BindService(ServiceBinderBase binder, SnapshotService? service) =>
            binder.AddMethod(new Method<ReadDatasetRequest, DatasetBatch>(
                MethodType.ServerStreaming, "eva.datahub.v1.DataHubService", "ReadDataset",
                Marshallers.Create<ReadDatasetRequest>(value => value.ToByteArray(), data => ReadDatasetRequest.Parser.ParseFrom(data)),
                Marshallers.Create<DatasetBatch>(value => value.ToByteArray(), data => DatasetBatch.Parser.ParseFrom(data))),
                service is null ? null : service.ReadDataset);

        public virtual async Task ReadDataset(ReadDatasetRequest request,
            IServerStreamWriter<DatasetBatch> responseStream, ServerCallContext context)
        {
            Assert.Equal(7, request.SnapshotId);
            using var query = JsonDocument.Parse(request.QueryJson);
            Assert.Equal(3, query.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(10, query.RootElement.GetProperty("offset").GetInt32());
            var bookmaker = request.DatasetId != "api_sport_matches_v1";
            string[] expected = request.DatasetId switch
            {
                "api_sport_matches_v1" => ["match_id", "match_date", "kickoff_time", "league", "home_team_id", "home_team_name", "away_team_id", "away_team_name", "first_seen_at", "collected_at"],
                "odds_v1" => ["event_id", "team1", "team2", "start_time", "first_seen_at", "last_seen_at"],
                _ => ["event_id", "team1", "team2", "start_time", "league", "first_seen_at", "last_seen_at"],
            };
            Assert.Equal(expected, query.RootElement.GetProperty("columns").EnumerateArray().Select(value => value.GetString()));
            var order = query.RootElement.GetProperty("order");
            Assert.Equal(bookmaker ? "start_time" : "match_date", order[0][0].GetString());
            Assert.Equal(bookmaker ? "event_id" : "match_id", order[1][0].GetString());
            var fields = new List<Field>();
            var columns = new List<IArrowArray>();
            static TimestampArray Times(string value) =>
                new TimestampArray.Builder(new TimestampType(TimeUnit.Microsecond, "UTC"))
                    .AppendRange(Enumerable.Repeat(DateTimeOffset.Parse(value), 4)).Build();
            foreach (var name in expected)
            {
                IArrowArray column = name switch
                {
                    "match_id" or "event_id" => new Int64Array.Builder().AppendRange([11L, 12L, 13L, 14L]).Build(),
                    "home_team_name" or "team1" => new StringArray.Builder().AppendRange(["Alpha", "Alpha", "Alpha", "Alpha"]).Build(),
                    "away_team_name" or "team2" => new StringArray.Builder().AppendRange(["Beta", "Gamma", "Delta", "Epsilon"]).Build(),
                    "start_time" or "kickoff_time" => Times("2026-11-20T18:00:00Z"),
                    "last_seen_at" or "collected_at" => Times("2026-11-15T12:00:00Z"),
                    "first_seen_at" => Times("2026-11-14T12:00:00Z"),
                    _ => new NullArray(4),
                };
                fields.Add(new Field(name, column.Data.DataType, true));
                columns.Add(column);
            }

            var schema = new Schema(fields, null);
            using var batch = new Apache.Arrow.RecordBatch(schema, columns, 4);
            using var stream = new MemoryStream();
            using var writer = new ArrowStreamWriter(stream, schema);
            await writer.WriteRecordBatchAsync(batch);
            await writer.WriteEndAsync();
            await responseStream.WriteAsync(new DatasetBatch
            {
                SnapshotId = 7, DatasetSnapshotId = 7, ArrowIpc = ByteString.CopyFrom(stream.ToArray()),
            });
        }
    }
}
