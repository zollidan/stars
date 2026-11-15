using System.Text.Json;
using Stars.Application.Normalization;

namespace Stars.Application.Matching;

public sealed record MatchContextFact(
    string OpponentNormalized,
    DateTimeOffset? KickoffAt,
    string? CompetitionNormalized,
    string? Role);

public sealed record SourceEntityProfile(
    Guid SourceEntityId,
    Guid SourceFeedId,
    string FeedKey,
    string EntityKind,
    string DisplayName,
    string NormalizedName,
    string? SportCode,
    string? CountryCode,
    IReadOnlyList<MatchContextFact> Context);

/// <summary>
/// A possible target. Exactly one of <paramref name="CandidateSourceEntityId"/> and
/// <paramref name="MasterEntityId"/> identifies the target; when a source entity already
/// has a canonical master, the candidate is represented as that master.
/// </summary>
public sealed record CandidateTarget(
    Guid? CandidateSourceEntityId,
    Guid? MasterEntityId,
    string FeedKey,
    string DisplayName,
    string NormalizedName,
    string? SportCode,
    string? CountryCode,
    IReadOnlyList<MatchContextFact> Context);

public sealed record EvidenceFact(
    string Key,
    string EvidenceType,
    double Weight,
    double Value,
    string Explanation,
    JsonElement Details);

public sealed record ScoredCandidate(
    CandidateTarget Target,
    double Score,
    double ContextValue,
    bool HardConflict,
    string? ConflictReason,
    IReadOnlyList<EvidenceFact> Evidence);

public sealed record MatchRunResult(
    double TopScore,
    double Margin,
    ScoredCandidate? Top,
    IReadOnlyList<ScoredCandidate> Candidates,
    string Reason)
{
    public bool AutoLink => Top is not null && Reason == DecisionReasons.AutoLink;
}

public static class DecisionReasons
{
    public const string AutoLink = "auto_link";
    public const string NoCandidates = "no_candidates";
    public const string HardConflict = "hard_conflict";
    public const string BelowThreshold = "below_threshold";
    public const string Ambiguous = "ambiguous";
    public const string ShortNameWithoutContext = "short_name_without_context";
}

/// <summary>
/// Pure, reproducible scoring. Given the same profiles and ruleset version it always
/// returns the same score and the same evidence.
/// </summary>
public sealed class MatchEngine
{
    public const string Version = "teams-v1";

    public const double AutoLinkThreshold = 0.88;
    public const double MinimumMargin = 0.03;

    // Weights sum to 1. Components with no information on either side are excluded and
    // remaining weights are renormalised, so missing provider metadata is not a penalty.
    private const double NameWeight = 0.65;
    private const double SportWeight = 0.10;
    private const double CountryWeight = 0.05;
    private const double ContextWeight = 0.20;

    public MatchRunResult Run(SourceEntityProfile source, IReadOnlyList<CandidateTarget> candidates)
    {
        var ranked = candidates
            .Where(candidate => candidate.NormalizedName.Length > 0)
            .Select(candidate => Score(source, candidate))
            .Where(candidate => candidate.Score > 0.15)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Target.NormalizedName, StringComparer.Ordinal)
            .Take(8)
            .ToArray();

        if (ranked.Length == 0)
        {
            return new MatchRunResult(0, 0, null, [], DecisionReasons.NoCandidates);
        }

        // Keep the top candidate and only near-ties. Low-scoring noise ("Dynamo Moscow"
        // versus "CSKA Moscow" because of the shared city token) is not useful evidence.
        var scored = new[] { ranked[0] }
            .Concat(ranked.Skip(1).Where(candidate => candidate.Score >= ranked[0].Score - 0.10))
            .Take(3)
            .ToArray();

        var top = scored[0];
        var margin = scored.Length == 1 ? 1.0 : top.Score - scored[1].Score;

        if (top.HardConflict)
        {
            return new MatchRunResult(top.Score, margin, top, scored, DecisionReasons.HardConflict);
        }

        if (margin < MinimumMargin)
        {
            return new MatchRunResult(top.Score, margin, top, scored, DecisionReasons.Ambiguous);
        }

        if (top.Score < AutoLinkThreshold)
        {
            return new MatchRunResult(top.Score, margin, top, scored, DecisionReasons.BelowThreshold);
        }

        // A bare "Dynamo" or "Zenit" must not become canonical just because one provider
        // uses the same word. A shared fixture context (or a longer distinctive name) is
        // required before automation is allowed to create a canonical team.
        if (NameNormalizer.TokenCount(source.NormalizedName) <= 1 && top.ContextValue <= 0)
        {
            return new MatchRunResult(top.Score, margin, top, scored, DecisionReasons.ShortNameWithoutContext);
        }

        return new MatchRunResult(top.Score, margin, top, scored, DecisionReasons.AutoLink);
    }

    public ScoredCandidate Score(SourceEntityProfile source, CandidateTarget candidate)
    {
        var evidence = new List<EvidenceFact>();
        var weighted = 0.0;
        var weights = 0.0;

        var name = NameSimilarity.Compare(source.NormalizedName, candidate.NormalizedName);
        weighted += name * NameWeight;
        weights += NameWeight;
        evidence.Add(Fact("name", "name_similarity", NameWeight, name,
            $"Normalised names '{source.NormalizedName}' and '{candidate.NormalizedName}' are {name:P0} similar.",
            new { source = source.NormalizedName, candidate = candidate.NormalizedName, similarity = name }));

        var markersLeft = NameNormalizer.Markers(source.NormalizedName);
        var markersRight = NameNormalizer.Markers(candidate.NormalizedName);
        var markersConflict = markersLeft != markersRight;
        if (!markersConflict)
        {
            evidence.Add(Fact("squad", "squad_markers", 0, 1,
                "Age, gender and reserve markers agree.",
                new { left = markersLeft, right = markersRight }));
        }
        else
        {
            evidence.Add(Fact("squad", "squad_markers", 0, 0,
                "Squad markers differ; automatic linking is forbidden.",
                new { left = markersLeft, right = markersRight }));
        }

        var sportConflict = false;
        if (!string.IsNullOrWhiteSpace(source.SportCode) && !string.IsNullOrWhiteSpace(candidate.SportCode))
        {
            var sameSport = string.Equals(source.SportCode, candidate.SportCode, StringComparison.OrdinalIgnoreCase);
            sportConflict = !sameSport;
            weighted += (sameSport ? 1.0 : 0.0) * SportWeight;
            weights += SportWeight;
            evidence.Add(Fact("sport", "sport_match", SportWeight, sameSport ? 1 : 0,
                sameSport ? "Sports agree." : "Sports differ; automatic linking is forbidden.",
                new { source = source.SportCode, candidate = candidate.SportCode }));
        }

        var countryConflict = false;
        if (!string.IsNullOrWhiteSpace(source.CountryCode) && !string.IsNullOrWhiteSpace(candidate.CountryCode))
        {
            var sameCountry = string.Equals(source.CountryCode, candidate.CountryCode, StringComparison.OrdinalIgnoreCase);
            countryConflict = !sameCountry;
            weighted += (sameCountry ? 1.0 : 0.0) * CountryWeight;
            weights += CountryWeight;
            evidence.Add(Fact("country", "country_match", CountryWeight, sameCountry ? 1 : 0,
                sameCountry ? "Countries agree." : "Countries differ; automatic linking is forbidden.",
                new { source = source.CountryCode, candidate = candidate.CountryCode }));
        }

        var context = ContextSimilarity(source.Context, candidate.Context);
        if (context is not null)
        {
            weighted += context.Value * ContextWeight;
            weights += ContextWeight;
            evidence.Add(Fact("context", "shared_fixture", ContextWeight, context.Value,
                context.Value > 0
                    ? $"Shared fixture context found ({context.Description})."
                    : "No shared opponent/kickoff context was found.",
                context.Details));
        }

        var score = weights == 0 ? 0 : weighted / weights;
        var conflict = markersConflict || sportConflict || countryConflict;
        var reason = markersConflict ? "squad_markers"
            : sportConflict ? "sport"
            : countryConflict ? "country"
            : null;

        return new ScoredCandidate(candidate, score, context?.Value ?? 0, conflict, reason, evidence);
    }

    private static ContextMatch? ContextSimilarity(
        IReadOnlyList<MatchContextFact> left,
        IReadOnlyList<MatchContextFact> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return null;
        }

        var best = 0.0;
        string? description = null;
        object? details = null;

        foreach (var a in left)
        {
            foreach (var b in right)
            {
                var opponent = NameSimilarity.Compare(a.OpponentNormalized, b.OpponentNormalized);
                if (opponent < 0.90)
                {
                    continue;
                }

                var hours = a.KickoffAt is not null && b.KickoffAt is not null
                    ? Math.Abs((a.KickoffAt.Value - b.KickoffAt.Value).TotalHours)
                    : (double?)null;

                if (hours is > 18)
                {
                    continue;
                }

                var competition = string.IsNullOrWhiteSpace(a.CompetitionNormalized)
                    || string.IsNullOrWhiteSpace(b.CompetitionNormalized)
                    ? 0.5
                    : NameSimilarity.Compare(a.CompetitionNormalized, b.CompetitionNormalized) >= 0.8 ? 1.0 : 0.0;

                var kickoff = hours is null ? 0.75 : Math.Max(0, 1.0 - hours.Value / 18.0);
                var value = 0.6 + 0.25 * contextRoleCompatibility(a.Role, b.Role) + 0.15 * competition;
                value = Math.Min(1.0, value * (0.75 + 0.25 * kickoff));

                if (value > best)
                {
                    best = value;
                    description = $"opponent '{a.OpponentNormalized}', kickoff delta {hours?.ToString("0.##") ?? "unknown"}h";
                    details = new
                    {
                        opponent = a.OpponentNormalized,
                        kickoffDeltaHours = hours,
                        leftRole = a.Role,
                        rightRole = b.Role,
                        competitionMatch = competition,
                    };
                }
            }
        }

        return new ContextMatch(best, description, details ?? new { });
    }

    private static double contextRoleCompatibility(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
        {
            return 0.5;
        }

        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    private static EvidenceFact Fact(
        string key,
        string type,
        double weight,
        double value,
        string explanation,
        object details) =>
        new(key, type, weight, value, explanation, JsonSerializer.SerializeToElement(details));

    private sealed record ContextMatch(double Value, string? Description, object Details);
}
