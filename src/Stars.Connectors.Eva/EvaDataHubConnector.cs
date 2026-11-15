using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
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
/// itself: it runs only for an explicit manual/bounded import.
/// </summary>
public sealed class EvaDataHubConnector : IConnector
{
    public const string ConnectorKey = "eva.datahub";
    private const string ApiSportDataset = "api_sport_matches_v1";
    private static readonly string[] ApiSportColumns =
    [
        "match_id", "match_date", "kickoff_time", "league",
        "home_team_id", "home_team_name", "away_team_id", "away_team_name",
        "first_seen_at", "collected_at",
    ];

    private static string[] ColumnsFor(string dataset) => dataset switch
    {
        ApiSportDataset => ApiSportColumns,
        "odds_v1" => ["event_id", "team1", "team2", "start_time", "first_seen_at", "last_seen_at"],
        "winline_odds_v1" or "marathon_odds_v1" =>
            ["event_id", "team1", "team2", "start_time", "league", "first_seen_at", "last_seen_at"],
        _ => throw new InvalidOperationException($"Unsupported EVA dataset '{dataset}'."),
    };

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
        var dataset = GetString(settings, "dataset") ?? ApiSportDataset;
        var columns = ColumnsFor(dataset);

        await foreach (var batch in ReadGrpcAsync(context, settings, dataset, columns, cancellationToken))
        {
            yield return batch;
        }
    }

    private static async IAsyncEnumerable<SourceBatch> ReadGrpcAsync(
        ConnectorContext context,
        JsonElement settings,
        string dataset,
        string[] columns,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var address = GetString(settings, "address")
            ?? context.BaseUrl
            ?? throw new InvalidOperationException("EVA connector requires settings.address or baseUrl.");
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
            columns,
            order = dataset == ApiSportDataset
                ? new[] { new[] { "match_date", "asc" }, new[] { "match_id", "asc" } }
                : new[] { new[] { "start_time", "asc" }, new[] { "event_id", "asc" } },
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

            var rows = ParseArrow(message.ArrowIpc.Memory, dataset, columns)
                .Take((int)Math.Min(limit - rowsRead, int.MaxValue)).ToArray();
            if (rows.Length == 0)
            {
                continue;
            }

            var mapped = MapRows(rows, dataset, message.SnapshotId);
            rowsRead += rows.Length;
            emittedAny = true;
            sequence++;
            yield return new SourceBatch(
                mapped.Entities,
                mapped.Events,
                $"eva:{dataset}:{snapshotId}:{datasetSnapshotId}:{offset + rowsRead}",
                sequence,
                rows.Length);

            if (rowsRead >= limit)
            {
                break;
            }
        }

        yield return SourceBatch.Empty(
            $"eva:{dataset}:{snapshotId ?? 0}:{datasetSnapshotId ?? 0}:{offset + rowsRead}:{(emittedAny ? "complete" : "empty")}");
    }

    internal static MappedRows MapRows(
        IReadOnlyList<EvaMatchRow> rows,
        string dataset,
        long snapshotId)
    {
        var entities = new Dictionary<string, SourceEntityRecord>(StringComparer.Ordinal);
        var events = new Dictionary<string, SourceEventRecord>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.MatchId is null || string.IsNullOrWhiteSpace(row.HomeTeamName)
                || string.IsNullOrWhiteSpace(row.AwayTeamName))
            {
                throw new InvalidOperationException($"EVA dataset '{dataset}' requires an event ID and two non-empty team names.");
            }

            var homeKey = dataset == ApiSportDataset
                ? TeamKey(row.HomeTeamId, row.HomeTeamName)
                : BookmakerTeamKey(row.HomeTeamName, row.League);
            var awayKey = dataset == ApiSportDataset
                ? TeamKey(row.AwayTeamId, row.AwayTeamName)
                : BookmakerTeamKey(row.AwayTeamName, row.League);
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

            var matchId = row.MatchId.Value.ToString(CultureInfo.InvariantCulture);
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

    // shortcut: names without a league cannot distinguish homonyms; use provider team IDs when available.
    private static string BookmakerTeamKey(string name, string? league) =>
        $"team:name:{Uri.EscapeDataString(name.Trim().ToLowerInvariant())}"
        + (string.IsNullOrWhiteSpace(league) ? string.Empty
            : $":league:{Uri.EscapeDataString(league.Trim().ToLowerInvariant())}");

    private static string? CompetitionKey(string? league) =>
        string.IsNullOrWhiteSpace(league)
            ? null
            : $"league:{league.Trim().ToLowerInvariant()}";

    private static List<EvaMatchRow> ParseArrow(ReadOnlyMemory<byte> payload, string dataset, string[] columns)
    {
        var rows = new List<EvaMatchRow>();
        using var stream = new MemoryStream(payload.ToArray(), writable: false);
        using var reader = new ArrowStreamReader(stream);
        ArrowRecordBatch? batch;
        while ((batch = reader.ReadNextRecordBatch()) is not null)
        {
            using (batch)
            {
                var isBookmaker = dataset != ApiSportDataset;
                // Only declared fields may be read: Fonbet has no league or team ID columns.
                IArrowArray? Column(string name) => columns.Contains(name, StringComparer.Ordinal)
                    ? batch.Column(name)
                    : null;
                var matchId = Column(isBookmaker ? "event_id" : "match_id");
                var matchDate = Column("match_date");
                var kickoff = Column(isBookmaker ? "start_time" : "kickoff_time");
                var league = Column("league");
                var homeId = Column("home_team_id");
                var homeName = Column(isBookmaker ? "team1" : "home_team_name");
                var awayId = Column("away_team_id");
                var awayName = Column(isBookmaker ? "team2" : "away_team_name");
                var firstSeen = Column("first_seen_at");
                var collected = Column(isBookmaker ? "last_seen_at" : "collected_at");

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

    internal sealed record EvaMatchRow(
        long? MatchId,
        DateTimeOffset? MatchDate,
        DateTimeOffset? KickoffAt,
        string? League,
        long? HomeTeamId,
        string HomeTeamName,
        long? AwayTeamId,
        string AwayTeamName,
        DateTimeOffset? ObservedAt);

    internal sealed record MappedRows(
        IReadOnlyList<SourceEntityRecord> Entities,
        IReadOnlyList<SourceEventRecord> Events);
}
