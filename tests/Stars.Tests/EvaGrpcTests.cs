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
    [Fact]
    public async Task Grpc_import_limits_rows_and_preserves_pinned_resume_offset()
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
            app.Urls.Single(), JsonSerializer.SerializeToElement(new { dataset = "test" }),
            "eva:test:7:7:10", 3, null);

        var batches = new List<SourceBatch>();
        await foreach (var batch in new EvaDataHubConnector().ReadSnapshotAsync(context, default))
        {
            batches.Add(batch);
        }

        Assert.Equal(3, batches.Sum(batch => batch.RecordCount));
        Assert.Equal(3, batches.Sum(batch => batch.Events.Count));
        Assert.Equal("eva:test:7:7:13", batches[0].Checkpoint);
        Assert.Equal("eva:test:7:7:13:complete", batches[^1].Checkpoint);
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
            Assert.Equal("test", request.DatasetId);
            Assert.Equal(7, request.SnapshotId);
            using var query = JsonDocument.Parse(request.QueryJson);
            Assert.Equal(3, query.RootElement.GetProperty("limit").GetInt32());
            Assert.Equal(10, query.RootElement.GetProperty("offset").GetInt32());
            var fields = new List<Field>
            {
                new Field("match_id", Int64Type.Default, true),
                new Field("home_team_name", StringType.Default, true),
                new Field("away_team_name", StringType.Default, true),
            };
            var columns = new List<IArrowArray>
            {
                new Int64Array.Builder().AppendRange([11L, 12L, 13L, 14L]).Build(),
                new StringArray.Builder().AppendRange(["Alpha", "Alpha", "Alpha", "Alpha"]).Build(),
                new StringArray.Builder().AppendRange(["Beta", "Gamma", "Delta", "Epsilon"]).Build(),
            };
            foreach (var name in new[] { "match_date", "kickoff_time", "league", "home_team_id", "away_team_id", "first_seen_at", "collected_at" })
            {
                fields.Add(new Field(name, NullType.Default, true));
                columns.Add(new NullArray(4));
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
