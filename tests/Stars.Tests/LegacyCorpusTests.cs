using System.Text.Json;
using Stars.Application.Matching;
using Stars.Application.Normalization;

namespace Stars.Tests;

/// <summary>
/// A deterministic sample extracted from the old team-directory dump. It keeps CI
/// independent of the 29 MB dump while guarding normalizer and threshold regressions.
/// </summary>
public class LegacyCorpusTests
{
    private static readonly Lazy<Corpus> Data = new(Load);

    [Fact]
    public void High_confidence_name_matching_stays_precise()
    {
        var (precision, recall) = Metrics(0.88, out var truePositives, out var falsePositives);
        Assert.True(precision >= 0.98, $"precision {precision:P2}, tp={truePositives}, fp={falsePositives}");
        Assert.True(recall >= 0.65, $"recall {recall:P2}, tp={truePositives}");
    }

    [Fact]
    public void Relaxed_threshold_recovers_more_real_links_without_many_false_merges()
    {
        var (precision, recall) = Metrics(0.75, out var truePositives, out var falsePositives);
        Assert.True(precision >= 0.98, $"precision {precision:P2}, tp={truePositives}, fp={falsePositives}");
        Assert.True(recall >= 0.85, $"recall {recall:P2}, tp={truePositives}");
    }

    [Fact]
    public void Repeated_name_inside_one_feed_never_auto_links()
    {
        var engine = new MatchEngine();
        var checkedGroups = 0;

        foreach (var group in Data.Value.Ambiguous)
        {
            var bySource = group.Records
                .GroupBy(record => record.Source)
                .ToArray();
            var crowdedSource = bySource.FirstOrDefault(records => records.Count() > 1);
            if (crowdedSource is null)
            {
                // One record per provider with the same name is a genuine ambiguity that
                // only context/country can resolve. It is reported, not asserted here.
                continue;
            }

            var source = Profile(crowdedSource.First()) with
            {
                SameFeedNameCount = crowdedSource.Count(),
            };
            var targets = bySource
                .Where(records => records.Key != crowdedSource.Key)
                .Select(records => Target(records.First()))
                .ToArray();
            if (targets.Length == 0)
            {
                continue;
            }

            var result = engine.Run(source, targets);
            Assert.False(result.AutoLink, $"ambiguous name auto-linked: {group.Name}");
            checkedGroups++;
        }

        Assert.True(checkedGroups >= 4, $"only {checkedGroups} multi-record ambiguous groups were checked");
    }

    [Fact]
    public void Normalizer_keeps_real_world_variants_that_used_to_break()
    {
        Assert.Equal("bodo glimt", NameNormalizer.Normalize("Bodø/Glimt"));
        Assert.Equal("brondby if", NameNormalizer.Normalize("Brøndby IF"));
        Assert.Equal("zenit st peterburg", NameNormalizer.Normalize("Зенит Санкт-Петербург"));
        Assert.Equal("manchester yunayted u21", NameNormalizer.Normalize("Манчестер Юнайтед (до 21)"));
        Assert.Equal("kahramanmaras istiklalspor u16", NameNormalizer.Normalize("Kahramanmaraş İstiklalspor U16"));
    }

    private static (double Precision, double Recall) Metrics(
        double threshold,
        out int truePositives,
        out int falsePositives)
    {
        var engine = new MatchEngine();
        truePositives = Data.Value.Positives.Count(pair =>
            Score(engine, pair.Left, pair.Right) >= threshold);
        falsePositives = Data.Value.Negatives.Count(pair =>
            Score(engine, pair.Left, pair.Right) >= threshold);
        var precision = truePositives + falsePositives == 0
            ? 0
            : (double)truePositives / (truePositives + falsePositives);
        var recall = (double)truePositives / Data.Value.Positives.Length;
        return (precision, recall);
    }

    private static double Score(MatchEngine engine, LegacyRecord left, LegacyRecord right)
    {
        var scored = engine.Score(Profile(left), Target(right));
        return scored.HardConflict ? 0 : scored.Score;
    }

    private static SourceEntityProfile Profile(LegacyRecord record) =>
        new(
            Guid.NewGuid(),
            Guid.Empty,
            record.Source,
            "team",
            record.Name,
            NameNormalizer.Normalize(record.Name),
            null,
            null,
            []);

    private static CandidateTarget Target(LegacyRecord record) =>
        new(
            Guid.NewGuid(),
            null,
            record.Source,
            record.Name,
            NameNormalizer.Normalize(record.Name),
            null,
            null,
            []);

    private static Corpus Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "legacy-corpus.json");
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<Corpus>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("Legacy corpus is empty.");
    }

    public sealed record Corpus(
        PairCase[] Positives,
        PairCase[] Negatives,
        AmbiguousGroup[] Ambiguous);

    public sealed record PairCase(LegacyRecord Left, LegacyRecord Right);

    public sealed record LegacyRecord(
        string Source,
        string Name,
        string LegacyNormalized,
        int? TeamId);

    public sealed record AmbiguousGroup(string Name, LegacyRecord[] Records);
}
