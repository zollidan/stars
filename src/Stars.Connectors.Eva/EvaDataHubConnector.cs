using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Apache.Arrow;
using ArrowRecordBatch = Apache.Arrow.RecordBatch;
using Apache.Arrow.Ipc;
using Eva.Datahub.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Stars.Connectors.Abstractions;

namespace Stars.Connectors.Eva;

/// <summary>
/// Read-only EVA DataHub connector.
///
/// Primary mode is gRPC <c>ReadDataset</c> with a pinned snapshot and bounded
/// <c>limit</c>/<c>offset</c> query. Arrow rows are reduced to teams, competitions and
/// compact fixture context; odds and full bookmaker snapshots are never stored.
///
/// DataHub has no safe change-feed contract. This connector therefore never schedules
/// itself: it runs only for an explicit manual/bounded import. A local EVA-shaped JSON
/// fixture mode exists for development and tests; it uses the same row mapping.
/// </summary>
public sealed class EvaDataHubConnector : IConnector
{
    public const string ConnectorKey = "eva.datahub";
    private static readonly string[] Columns =
    [
        "match_id", "match_date", "kickoff_time", "league",
        "home_team_id", "home_team_name", "away_team_id", "away_team_name",
        "first_seen_at", "collected_at",
    ];

    public string Key => ConnectorKey;

    public ConnectorCapabilities Capabilities => new(
        SupportsSnapshot: true,
        SupportsChanges: false,
        SupportsCursor: true,
        SupportsPush: false,
        SyncMode: "snapshot",
        Notes: "No ReadChanges contract. Manual bounded imports only; resumable within a pinned snapshot.");

    public async IAsyncEnumerable<SourceBatch> ReadSnapshotAsync(
        ConnectorContext context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var settings = context.Settings;
        var mode = GetString(settings, "mode") ?? "grpc";
        if (string.Equals(mode, "fixture", StringComparison.OrdinalIgnoreCase))
        {
            await foreach (var batch in ReadFixtureAsync(context, settings, cancellationToken))
            {
                yield return batch;
            }

            yield break;
        }

        await foreach (var batch in ReadGrpcAsync(context, settings, cancellationToken))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<SourceBatch> ReadFixtureAsync(
        ConnectorContext context,
        JsonElement settings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fixturePath = GetString(settings, "fixturePath")
            ?? throw new InvalidOperationException("EVA fixture mode requires settings.fixturePath.");
        var path = Path.IsPathRooted(fixturePath)
            ? fixturePath
            : Path.Combine(AppContext.BaseDirectory, fixturePath);
        if (!File.Exists(path))
        {
            path = Path.Combine(Directory.GetCurrentDirectory(), fixturePath);
        }

        await using var stream = File.OpenRead(path);
        var fixture = await JsonSerializer.DeserializeAsync<EvaFixture>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web),
            cancellationToken)
            ?? throw new InvalidOperationException($"EVA fixture '{path}' is empty.");

        var rows = (fixture.Rows ?? [])
            .OrderBy(row => row.MatchDate)
            .ThenBy(row => row.MatchId)
            .Select(row => new EvaMatchRow(
                row.MatchId,
                row.MatchDate is null
                    ? null
                    : new DateTimeOffset(row.MatchDate.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero),
                row.KickoffTime,
                row.League,
                row.HomeTeamId,
                row.HomeTeamName,
                row.AwayTeamId,
                row.AwayTeamName,
                row.CollectedAt ?? row.FirstSeenAt))
            .ToArray();
        var pageSize = Math.Min(
            Math.Clamp(GetInt(settings, "pageSize") ?? 100, 1, 1000),
            (int)Math.Max(1, context.MaxRecords));
        var offset = ParseOffset(context.Cursor, out _);
        var dataset = GetString(settings, "dataset") ?? "api_sport_matches_v1";
        var snapshot = fixture.SnapshotId ?? 1;

        var emitted = 0;
        var page = 0;
        foreach (var chunk in rows.Skip((int)Math.Min(offset, rows.Length)).Chunk(pageSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (emitted >= context.MaxRecords)
            {
                break;
            }

            var mapped = MapRows(chunk, dataset, snapshot);
            emitted += chunk.Length;
            page++;
            yield return new SourceBatch(
                mapped.Entities,
                mapped.Events,
                $"eva:{dataset}:{snapshot}:{snapshot}:{offset + emitted}",
                page);
        }

        yield return SourceBatch.Empty($"eva:{dataset}:{snapshot}:{snapshot}:{offset + emitted}:complete");
    }

    private static async IAsyncEnumerable<SourceBatch> ReadGrpcAsync(
        ConnectorContext context,
        JsonElement settings,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var address = GetString(settings, "address")
            ?? context.BaseUrl
            ?? throw new InvalidOperationException("EVA connector requires settings.address or baseUrl.");
        var dataset = GetString(settings, "dataset") ?? "api_sport_matches_v1";
        var pageSize = Math.Clamp(GetInt(settings, "pageSize") ?? 1000, 1, 5000);
        var offset = ParseOffset(context.Cursor, out var pinnedSnapshot);
        var remaining = Math.Max(1, context.MaxRecords);
        var limit = (int)Math.Min(Math.Min(remaining, 100_000), int.MaxValue);

        using var channel = GrpcChannel.ForAddress(address);
        var client = new DataHubService.DataHubServiceClient(channel);
        var metadata = new Metadata();
        if (!string.IsNullOrWhiteSpace(context.Secret))
        {
            metadata.Add("authorization", $"Bearer {context.Secret}");
        }

        var query = JsonSerializer.Serialize(new
        {
            columns = Columns,
            order = new[] { new[] { "match_date", "asc" }, new[] { "match_id", "asc" } },
            limit,
            offset,
        });

        var request = new ReadDatasetRequest
        {
            DatasetId = dataset,
            QueryJson = query,
        };
        if (pinnedSnapshot is > 0)
        {
            request.SnapshotId = pinnedSnapshot.Value;
        }

        using var call = client.ReadDataset(
            request,
            metadata,
            deadline: DateTime.UtcNow.AddMinutes(2),
            cancellationToken: cancellationToken);

        long rowsRead = 0;
        long sequence = 0;
        long? snapshotId = pinnedSnapshot;
        long? datasetSnapshotId = null;
        var emittedAny = false;

        await foreach (var message in call.ResponseStream.ReadAllAsync(cancellationToken))
        {
            snapshotId = message.SnapshotId;
            datasetSnapshotId = message.DatasetSnapshotId;

            if (message.ArrowIpc.Length == 0)
            {
                continue;
            }

            var rows = ParseArrow(message.ArrowIpc.Memory);
            if (rows.Count == 0)
            {
                continue;
            }

            var mapped = MapRows(rows, dataset, message.SnapshotId);
            rowsRead += rows.Count;
            emittedAny = true;
            sequence++;
            yield return new SourceBatch(
                mapped.Entities,
                mapped.Events,
                $"eva:{dataset}:{snapshotId}:{datasetSnapshotId}:{offset + rowsRead}",
                sequence);
        }

        yield return SourceBatch.Empty(
            $"eva:{dataset}:{snapshotId ?? 0}:{datasetSnapshotId ?? 0}:{offset + rowsRead}:{(emittedAny ? "complete" : "empty")}");
    }

    private static MappedRows MapRows(
        IReadOnlyList<EvaMatchRow> rows,
        string dataset,
        long snapshotId)
    {
        var entities = new Dictionary<string, SourceEntityRecord>(StringComparer.Ordinal);
        var events = new Dictionary<string, SourceEventRecord>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var homeKey = TeamKey(row.HomeTeamId, row.HomeTeamName);
            var awayKey = TeamKey(row.AwayTeamId, row.AwayTeamName);
            var competitionKey = CompetitionKey(row.League);
            var sport = "football";

            entities[homeKey] = Team(homeKey, row.HomeTeamId, row.HomeTeamName, sport, dataset, snapshotId, row.ObservedAt);
            entities[awayKey] = Team(awayKey, row.AwayTeamId, row.AwayTeamName, sport, dataset, snapshotId, row.ObservedAt);
            if (competitionKey is not null && !string.IsNullOrWhiteSpace(row.League))
            {
                entities[competitionKey] = new SourceEntityRecord(
                    EntityKinds.Competition,
                    competitionKey,
                    null,
                    row.League,
                    sport,
                    null,
                    null,
                    JsonSerializer.SerializeToElement(new { dataset }),
                    row.ObservedAt);
            }

            var matchId = row.MatchId?.ToString(CultureInfo.InvariantCulture) ?? row.MatchDate?.ToString("yyyyMMdd");
            var eventKey = $"match:{matchId}";
            var kickoff = row.KickoffAt ?? row.MatchDate;
            events[eventKey] = new SourceEventRecord(
                eventKey,
                matchId,
                sport,
                competitionKey,
                kickoff,
                null,
                [
                    new SourceEventParticipantRecord(homeKey, "home"),
                    new SourceEventParticipantRecord(awayKey, "away"),
                ],
                JsonSerializer.SerializeToElement(new
                {
                    dataset,
                    snapshot_id = snapshotId,
                    match_date = row.MatchDate?.ToString("yyyy-MM-dd"),
                    league = row.League,
                }),
                row.ObservedAt);
        }

        return new MappedRows(entities.Values.ToArray(), events.Values.ToArray());
    }

    private static SourceEntityRecord Team(
        string key,
        long? id,
        string name,
        string sport,
        string dataset,
        long snapshotId,
        DateTimeOffset? observedAt) =>
        new(
            EntityKinds.Team,
            key,
            id?.ToString(CultureInfo.InvariantCulture),
            name,
            sport,
            null,
            null,
            JsonSerializer.SerializeToElement(new { dataset, snapshot_id = snapshotId }),
            observedAt);

    private static string TeamKey(long? id, string name) =>
        id is null
            ? $"team:name:{name.Trim().ToLowerInvariant()}"
            : $"team:{id.Value.ToString(CultureInfo.InvariantCulture)}";

    private static string? CompetitionKey(string? league) =>
        string.IsNullOrWhiteSpace(league)
            ? null
            : $"league:{league.Trim().ToLowerInvariant()}";

    private static List<EvaMatchRow> ParseArrow(ReadOnlyMemory<byte> payload)
    {
        var rows = new List<EvaMatchRow>();
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new ArrowStreamReader(stream);
        ArrowRecordBatch? batch;
        while ((batch = reader.ReadNextRecordBatch()) is not null)
        {
            using (batch)
            {
                var matchId = batch.Column("match_id");
                var matchDate = batch.Column("match_date");
                var kickoff = batch.Column("kickoff_time");
                var league = batch.Column("league");
                var homeId = batch.Column("home_team_id");
                var homeName = batch.Column("home_team_name");
                var awayId = batch.Column("away_team_id");
                var awayName = batch.Column("away_team_name");
                var firstSeen = batch.Column("first_seen_at");
                var collected = batch.Column("collected_at");

                for (var index = 0; index < batch.Length; index++)
                {
                    rows.Add(new EvaMatchRow(
                        GetLong(matchId, index),
                        GetDate(matchDate, index),
                        GetTimestamp(kickoff, index),
                        GetString(league, index),
                        GetLong(homeId, index),
                        GetString(homeName, index) ?? string.Empty,
                        GetLong(awayId, index),
                        GetString(awayName, index) ?? string.Empty,
                        GetTimestamp(collected, index) ?? GetTimestamp(firstSeen, index)));
                }
            }
        }

        return rows;
    }

    private static long? GetLong(IArrowArray? array, int index)
    {
        if (array is null || array.IsNull(index))
        {
            return null;
        }

        return array switch
        {
            Int64Array value => value.Values[index],
            Int32Array value => value.Values[index],
            UInt64Array value => (long)value.Values[index],
            UInt32Array value => value.Values[index],
            _ => null,
        };
    }

    private static string? GetString(IArrowArray? array, int index)
    {
        if (array is null || array.IsNull(index))
        {
            return null;
        }

        return array switch
        {
            StringArray value => value.GetString(index),
            _ => null,
        };
    }

    private static DateTimeOffset? GetTimestamp(IArrowArray? array, int index)
    {
        if (array is null || array.IsNull(index))
        {
            return null;
        }

        return array switch
        {
            TimestampArray value => value.GetTimestamp(index),
            _ => null,
        };
    }

    private static DateTimeOffset? GetDate(IArrowArray? array, int index)
    {
        if (array is null || array.IsNull(index))
        {
            return null;
        }

        return array switch
        {
            Date32Array value => value.GetDateTimeOffset(index),
            Date64Array value => value.GetDateTimeOffset(index),
            _ => null,
        };
    }

    private static long ParseOffset(string? cursor, out long? snapshotId)
    {
        snapshotId = null;
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return 0;
        }

        var parts = cursor.Split(':');
        if (parts.Length < 5 || parts[0] != "eva")
        {
            return 0;
        }

        // A completed cursor must not pin the next manual snapshot to the previous one;
        // only an interrupted run (numeric trailing offset) is resumable.
        if (!long.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            return 0;
        }

        if (long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSnapshot))
        {
            snapshotId = parsedSnapshot;
        }

        return long.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var offset)
            ? offset
            : 0;
    }

    private static string? GetString(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement settings, string name) =>
        settings.ValueKind == JsonValueKind.Object
        && settings.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : null;

    public sealed class EvaFixture
    {
        public long? SnapshotId { get; set; }

        public List<EvaFixtureRow>? Rows { get; set; }
    }

    public sealed class EvaFixtureRow
    {
        [JsonPropertyName("match_id")]
        public long MatchId { get; set; }

        [JsonPropertyName("match_date")]
        public DateOnly? MatchDate { get; set; }

        [JsonPropertyName("kickoff_time")]
        public DateTimeOffset? KickoffTime { get; set; }

        [JsonPropertyName("league")]
        public string? League { get; set; }

        [JsonPropertyName("home_team_id")]
        public long? HomeTeamId { get; set; }

        [JsonPropertyName("home_team_name")]
        public string HomeTeamName { get; set; } = string.Empty;

        [JsonPropertyName("away_team_id")]
        public long? AwayTeamId { get; set; }

        [JsonPropertyName("away_team_name")]
        public string AwayTeamName { get; set; } = string.Empty;

        [JsonPropertyName("collected_at")]
        public DateTimeOffset? CollectedAt { get; set; }

        [JsonPropertyName("first_seen_at")]
        public DateTimeOffset? FirstSeenAt { get; set; }
    }

    private sealed record EvaMatchRow(
        long? MatchId,
        DateTimeOffset? MatchDate,
        DateTimeOffset? KickoffAt,
        string? League,
        long? HomeTeamId,
        string HomeTeamName,
        long? AwayTeamId,
        string AwayTeamName,
        DateTimeOffset? ObservedAt);

    private sealed record MappedRows(
        IReadOnlyList<SourceEntityRecord> Entities,
        IReadOnlyList<SourceEventRecord> Events);
}
