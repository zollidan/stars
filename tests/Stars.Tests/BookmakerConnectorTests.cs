using System.Text.Json;
using Stars.Application.Ingestion;
using Stars.Connectors.Abstractions;
using Stars.Connectors.Eva;

namespace Stars.Tests;

public class BookmakerConnectorTests
{
    private static EvaDataHubConnector.EvaMatchRow Row(
        long eventId,
        string home,
        string away,
        string? league = null,
        DateTimeOffset? kickoff = null,
        DateTimeOffset? observedAt = null) =>
        new(eventId, null, kickoff, league, null, home, null, away, observedAt);

    [Theory]
    [InlineData("odds_v1", false)]
    [InlineData("winline_odds_v1", true)]
    [InlineData("marathon_odds_v1", true)]
    public void Bookmaker_rows_preserve_match_context_and_ignore_odds(string dataset, bool hasLeague)
    {
        var observed = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        var mapped = EvaDataHubConnector.MapRows(
            [
                Row(101, "Alpha", "Beta", hasLeague ? "Premier League" : null,
                    DateTimeOffset.Parse("2026-11-20T18:00:00Z"), observed),
                Row(102, "Alpha", "Gamma", hasLeague ? "Premier League" : null,
                    DateTimeOffset.Parse("2026-11-21T18:00:00Z"),
                    DateTimeOffset.Parse("2026-11-14T12:00:00Z")),
            ],
            dataset,
            7);

        Assert.Equal(2, mapped.Events.Count);
        var teams = mapped.Entities.Where(entity => entity.EntityKind == "team").ToArray();
        Assert.Equal(3, teams.Length);
        var alpha = Assert.Single(teams, team => team.DisplayName == "Alpha");
        Assert.Equal(hasLeague ? "team:name:alpha:league:premier%20league" : "team:name:alpha", alpha.SourceKey);
        Assert.Null(alpha.ExternalId);
        Assert.Equal(hasLeague ? 1 : 0, mapped.Entities.Count(entity => entity.EntityKind == "competition"));
        var events = mapped.Events.OrderBy(value => value.SourceKey).ToArray();
        Assert.Equal("match:101", events[0].SourceKey);
        Assert.Equal("101", events[0].ExternalId);
        Assert.Equal(DateTimeOffset.Parse("2026-11-20T18:00:00Z"), events[0].KickoffAt);
        Assert.Equal(observed, events[0].ObservedAt);
        Assert.Equal(DateTimeOffset.Parse("2026-11-14T12:00:00Z"), events[1].ObservedAt);
        Assert.Equal(alpha.SourceKey, events[0].Participants.Single(participant => participant.Role == "home").SourceEntityKey);
        Assert.Equal(hasLeague ? "league:premier league" : null, events[0].CompetitionSourceKey);
        Assert.All(events, value =>
        {
            Assert.False(value.Attributes!.Value.TryGetProperty("p1", out _));
            Assert.False(value.Attributes.Value.TryGetProperty("main_total_line", out _));
        });

        var original = events.Select(value => IdentityHasher.Event(value, hasLeague ? "premier league" : "")).ToArray();
        var replay = EvaDataHubConnector.MapRows(
            [
                Row(101, "Alpha", "Beta", hasLeague ? "Premier League" : null,
                    DateTimeOffset.Parse("2026-11-20T18:00:00Z"), observed),
                Row(102, "Alpha", "Gamma", hasLeague ? "Premier League" : null,
                    DateTimeOffset.Parse("2026-11-21T18:00:00Z"),
                    DateTimeOffset.Parse("2026-11-14T12:00:00Z")),
            ],
            dataset,
            8);
        Assert.Equal(
            original,
            replay.Events.OrderBy(value => value.SourceKey)
                .Select(value => IdentityHasher.Event(value, hasLeague ? "premier league" : "")));
    }

    [Fact]
    public void Bookmaker_without_kickoff_or_league_still_uses_event_id()
    {
        var mapped = EvaDataHubConnector.MapRows(
            [Row(10, "Alpha", "Beta"), Row(11, "Alpha", "Gamma")],
            "winline_odds_v1",
            1);

        var sourceEvent = Assert.Single(mapped.Events, value => value.SourceKey == "match:10");
        Assert.Null(sourceEvent.KickoffAt);
        Assert.Null(sourceEvent.CompetitionSourceKey);
    }

    [Theory]
    [InlineData(0L, "", "Beta")]
    [InlineData(1L, " ", "Beta")]
    public void Invalid_bookmaker_identity_is_rejected(long eventId, string home, string away)
    {
        Assert.Throws<InvalidOperationException>(() =>
            EvaDataHubConnector.MapRows([Row(eventId, home, away)], "odds_v1", 1));
    }

    [Fact]
    public void Homonymous_teams_in_different_leagues_keep_distinct_source_keys()
    {
        var mapped = EvaDataHubConnector.MapRows(
            [
                Row(1, "United", "Beta", "England"),
                Row(2, "United", "Gamma", "Scotland"),
            ],
            "marathon_odds_v1",
            1);

        var teams = mapped.Entities.Where(entity => entity.DisplayName == "United").ToArray();
        Assert.Equal(2, teams.Length);
        Assert.Equal(2, teams.Select(team => team.SourceKey).Distinct().Count());
    }

    [Fact]
    public async Task Unsupported_dataset_is_rejected_before_connecting()
    {
        var context = new ConnectorContext(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "bookmaker",
            "match",
            null,
            JsonSerializer.SerializeToElement(new { dataset = "unknown_v1" }),
            null,
            100,
            null);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in new EvaDataHubConnector().ReadSnapshotAsync(context, CancellationToken.None))
            {
            }
        });
    }
}
