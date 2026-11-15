using Stars.Application.Matching;

namespace Stars.Tests;

public class NameSimilarityTests
{
    [Fact]
    public void Matches_legacy_ratio_values()
    {
        Assert.Equal(0.965517, NameSimilarity.Ratio("this is a test", "this is a test!"), 6);
        Assert.Equal(1.0, NameSimilarity.TokenSetRatio("fuzzy was a bear", "fuzzy fuzzy was a bear"));
    }

    [Fact]
    public void Matches_legacy_token_set_value()
    {
        var value = NameSimilarity.TokenSetRatio(
            "fuzzy was a bear but not a dog",
            "fuzzy was a bear but not a cat");
        Assert.Equal(0.946429, value, 6);
    }

    [Theory]
    [InlineData("zenit", "zenit", 1.0)]
    [InlineData("zenit", "zenit sankt peterburg", 1.0)]
    [InlineData("spartak moskva", "spartak moskva", 1.0)]
    [InlineData("dinamo", "dinamo moskva", 1.0)]
    public void Subset_names_are_high_confidence(string left, string right, double expected) =>
        Assert.Equal(expected, NameSimilarity.Compare(left, right));

    [Fact]
    public void Empty_names_are_not_similar() =>
        Assert.Equal(0.0, NameSimilarity.Compare("", "zenit"));
}
