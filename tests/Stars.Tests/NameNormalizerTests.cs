using Stars.Application.Normalization;

namespace Stars.Tests;

public class NameNormalizerTests
{
    [Theory]
    [InlineData("Manchester United FC", "manchester united")]
    [InlineData("FC Bayern München", "bayern munchen")]
    [InlineData("Реал Мадрид", "real madrid")]
    [InlineData("ФК Зенит", "zenit")]
    [InlineData("Club León", "leon")]
    [InlineData("Juventus Under 19", "juventus u19")]
    [InlineData("Juventus U19", "juventus u19")]
    [InlineData("Arsenal Women", "arsenal w")]
    [InlineData("FC", "fc")]
    [InlineData("Спартак Москва", "spartak moskva")]
    [InlineData("Spartak Moscow", "spartak moskva")]
    [InlineData("Zenit St Petersburg", "zenit sankt peterburg")]
    [InlineData("Зенит Санкт-Петербург", "zenit sankt peterburg")]
    [InlineData("Динамо", "dinamo")]
    [InlineData("Dynamo Moscow", "dinamo moskva")]
    [InlineData("ЦСКА", "cska")]
    [InlineData("CSKA Moscow", "cska moskva")]
    public void Normalizes_provider_names_to_one_form(string input, string expected) =>
        Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Theory]
    [InlineData("juventus u19", "u19")]
    [InlineData("arsenal w", "w")]
    [InlineData("dynamo 2 res", "2 res")]
    [InlineData("u2 under", "u2")]
    [InlineData("zenit", "")]
    public void Keeps_squad_markers(string input, string expected) =>
        Assert.Equal(expected, NameNormalizer.Markers(input));
}
