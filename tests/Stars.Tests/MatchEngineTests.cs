using Stars.Application.Matching;

namespace Stars.Tests;

public class MatchEngineTests
{
    private static readonly DateTimeOffset Kickoff = new(2026, 11, 20, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Links_spartak_across_transliterations_with_shared_fixture()
    {
        var source = new SourceEntityProfile(
            Guid.NewGuid(), Guid.NewGuid(), "rest", "team",
            "Spartak Moscow", "spartak moskva", "football", "RU",
            [new MatchContextFact("zenit sankt peterburg", Kickoff, "russian premier league", "home")]);

        var candidate = new CandidateTarget(
            Guid.NewGuid(), null, "eva", "Спартак Москва", "spartak moskva", "football", null,
            [new MatchContextFact("zenit sankt peterburg", Kickoff, "russian premier league", "home")]);

        var result = new MatchEngine().Run(source, [candidate]);

        Assert.True(result.AutoLink, result.Reason);
        Assert.Equal(1.0, result.TopScore, 3);
        Assert.Contains(result.Top!.Evidence, item => item.Key == "context" && item.Value > 0.9);
    }

    [Fact]
    public void Ambiguous_dynamo_stays_in_review()
    {
        var source = new SourceEntityProfile(
            Guid.NewGuid(), Guid.NewGuid(), "eva", "team",
            "Динамо", "dinamo", "football", null,
            [new MatchContextFact("shakhtar", Kickoff, "ukrainian premier league", "home")]);

        var moscow = new CandidateTarget(
            Guid.NewGuid(), null, "rest", "Dynamo Moscow", "dinamo moskva", "football", "RU", []);
        var minsk = new CandidateTarget(
            Guid.NewGuid(), null, "rest", "Dinamo Minsk", "dinamo minsk", "football", "BY", []);

        var result = new MatchEngine().Run(source, [moscow, minsk]);

        Assert.False(result.AutoLink);
        Assert.Equal(DecisionReasons.Ambiguous, result.Reason);
        Assert.Equal(0, result.Margin, 3);
    }

    [Fact]
    public void Single_token_name_without_context_requires_review()
    {
        var source = new SourceEntityProfile(
            Guid.NewGuid(), Guid.NewGuid(), "eva", "team",
            "Зенит", "zenit", "football", null, []);
        var candidate = new CandidateTarget(
            Guid.NewGuid(), null, "rest", "Zenit St Petersburg", "zenit sankt peterburg", "football", "RU", []);

        var result = new MatchEngine().Run(source, [candidate]);

        Assert.False(result.AutoLink);
        Assert.Equal(DecisionReasons.ShortNameWithoutContext, result.Reason);
    }

    [Fact]
    public void Country_conflict_is_hard()
    {
        var source = new SourceEntityProfile(
            Guid.NewGuid(), Guid.NewGuid(), "eva", "team",
            "Динамо", "dinamo", "football", "RU", []);
        var candidate = new CandidateTarget(
            Guid.NewGuid(), null, "rest", "Dinamo Minsk", "dinamo", "football", "BY", []);

        var result = new MatchEngine().Run(source, [candidate]);

        Assert.False(result.AutoLink);
        Assert.Equal(DecisionReasons.HardConflict, result.Reason);
        Assert.Equal("country", result.Top!.ConflictReason);
    }

    [Fact]
    public void Age_markers_never_merge_into_senior_team()
    {
        var source = new SourceEntityProfile(
            Guid.NewGuid(), Guid.NewGuid(), "eva", "team",
            "Juventus U19", "juventus u19", "football", null, []);
        var candidate = new CandidateTarget(
            Guid.NewGuid(), null, "rest", "Juventus", "juventus", "football", "IT", []);

        var result = new MatchEngine().Run(source, [candidate]);

        Assert.False(result.AutoLink);
        Assert.Equal(DecisionReasons.HardConflict, result.Reason);
        Assert.Equal("squad_markers", result.Top!.ConflictReason);
    }

    [Fact]
    public void No_shared_context_falls_below_auto_threshold()
    {
        var source = new SourceEntityProfile(
            Guid.NewGuid(), Guid.NewGuid(), "rest", "team",
            "Dinamo Minsk", "dinamo minsk", "football", "BY",
            [new MatchContextFact("bate", Kickoff, "belarusian league", "home")]);
        var candidate = new CandidateTarget(
            Guid.NewGuid(), null, "eva", "Динамо", "dinamo", "football", null,
            [new MatchContextFact("shakhtar", Kickoff.AddDays(2), "ukrainian league", "home")]);

        var result = new MatchEngine().Run(source, [candidate]);

        Assert.False(result.AutoLink);
        Assert.Equal(DecisionReasons.BelowThreshold, result.Reason);
        Assert.True(result.TopScore < MatchEngine.AutoLinkThreshold);
    }
}
