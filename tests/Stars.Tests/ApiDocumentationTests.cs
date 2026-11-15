using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Stars.Tests;

public class ApiDocumentationTests
{
    [Fact]
    public async Task Production_serves_scalar_local_assets_and_documented_api_without_database_access()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.UseSetting("ConnectionStrings:Stars", "Host=localhost;Database=stars;Username=stars");
            builder.UseSetting("Stars:AutoMigrate", "false");
            builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        });
        using var client = factory.CreateClient();
        var html = await client.GetStringAsync("/scalar");
        Assert.Contains("STARS HTTP API", html);
        Assert.Contains("openapi/v1.json", html);
        var bundle = Regex.Match(html, "<script[^>]+src=\"([^\"]+)\"");
        Assert.True(bundle.Success, html);
        var bundleUri = new Uri(new Uri(client.BaseAddress!, "/scalar/"), bundle.Groups[1].Value);
        Assert.Equal(client.BaseAddress!.Host, bundleUri.Host);
        using var asset = await client.GetAsync(bundleUri);
        asset.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await client.GetStringAsync("/openapi/v1.json"));
        var paths = document.RootElement.GetProperty("paths");
        Assert.Equal(12, paths.EnumerateObject().Count());
        var sources = paths.GetProperty("/api/sources").GetProperty("get");
        Assert.Equal("Sources", sources.GetProperty("tags")[0].GetString());
        Assert.True(sources.GetProperty("responses").GetProperty("200").TryGetProperty("content", out _));
        var confirm = paths.GetProperty("/api/review/{candidateId}/confirm").GetProperty("post");
        Assert.True(confirm.TryGetProperty("requestBody", out _));
        Assert.True(confirm.GetProperty("responses").GetProperty("200").TryGetProperty("content", out _));
        Assert.True(confirm.GetProperty("responses").TryGetProperty("409", out _));
        var schemas = document.RootElement.GetProperty("components").GetProperty("schemas");
        Assert.True(schemas.TryGetProperty("DirectoryEntryView", out _));
        Assert.True(schemas.TryGetProperty("ReviewDecisionRequest", out _));
        Assert.True(schemas.TryGetProperty("SyncFeedResult", out _));
    }
}
