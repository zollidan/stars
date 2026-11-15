using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stars.Connectors.Abstractions;
using Stars.Domain.Entities;
using Stars.Infrastructure.Persistence;

namespace Stars.Infrastructure.Bootstrap;

/// <summary>
/// Applies declarative provider and feed definitions from configuration on startup.
/// Existing cursors, links and sync history are never changed; only connection metadata
/// and feed configuration are reconciled. This keeps deployments reproducible without
/// requiring SQL edits while still leaving operational data under database control.
/// </summary>
public sealed class ProviderBootstrapService(
    StarsDbContext db,
    IConnectorRegistry connectors,
    IConfiguration configuration,
    ILogger<ProviderBootstrapService> logger)
{
    public const string SectionName = "Stars:Providers";

    public async Task ApplyAsync(CancellationToken cancellationToken)
    {
        var root = configuration.GetSection(SectionName);
        if (!root.Exists())
        {
            logger.LogInformation("No provider configuration at {Section}; registry left unchanged.", SectionName);
            return;
        }

        var providers = await db.ProviderConnections
            .Include(provider => provider.SourceFeeds)
            .ToListAsync(cancellationToken);

        var changes = 0;
        foreach (var providerSection in root.GetChildren())
        {
            var name = providerSection["Name"];
            if (string.IsNullOrWhiteSpace(name))
            {
                name = providerSection.Key;
            }

            var connectorKey = providerSection["ConnectorKey"];
            if (string.IsNullOrWhiteSpace(connectorKey))
            {
                throw new InvalidOperationException(
                    $"Provider '{name}' is missing the required '{SectionName}:{providerSection.Key}:ConnectorKey' setting.");
            }

            if (connectors.Resolve(connectorKey) is null)
            {
                throw new InvalidOperationException(
                    $"Provider '{name}' references connector '{connectorKey}', which is not registered.");
            }

            var now = DateTimeOffset.UtcNow;
            var provider = providers.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));

            if (provider is null)
            {
                provider = new ProviderConnection
                {
                    Name = name,
                    ConnectorKey = connectorKey,
                    Settings = JsonDocument.Parse("{}"),
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.ProviderConnections.Add(provider);
                providers.Add(provider);
                changes++;
            }

            changes += UpdateProvider(provider, providerSection, connectorKey, now);
            changes += ApplyFeeds(provider, providerSection, now);
        }

        if (changes > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Provider configuration applied: {Changes} change(s) across {Providers} provider(s).",
            changes,
            root.GetChildren().Count());
    }

    private static int UpdateProvider(
        ProviderConnection provider,
        IConfigurationSection section,
        string connectorKey,
        DateTimeOffset now)
    {
        var baseUrl = NullIfEmpty(section["BaseUrl"]);
        var authSecretRef = NullIfEmpty(section["AuthSecretRef"]);
        var settings = ToJsonObject(section.GetSection("Settings"));
        var enabled = ParseBool(section["Enabled"], true);

        if (provider.ConnectorKey == connectorKey
            && provider.BaseUrl == baseUrl
            && provider.AuthSecretRef == authSecretRef
            && provider.IsEnabled == enabled
            && JsonElement.DeepEquals(provider.Settings.RootElement, settings.RootElement))
        {
            return 0;
        }

        provider.ConnectorKey = connectorKey;
        provider.BaseUrl = baseUrl;
        provider.AuthSecretRef = authSecretRef;
        provider.Settings = settings;
        provider.IsEnabled = enabled;
        provider.UpdatedAt = now;
        return 1;
    }

    private static int ApplyFeeds(
        ProviderConnection provider,
        IConfigurationSection providerSection,
        DateTimeOffset now)
    {
        var changes = 0;
        foreach (var feedSection in providerSection.GetSection("Feeds").GetChildren())
        {
            var feedKey = feedSection["FeedKey"];
            if (string.IsNullOrWhiteSpace(feedKey))
            {
                feedKey = feedSection.Key;
            }

            var entityKind = feedSection["EntityKind"];
            if (string.IsNullOrWhiteSpace(entityKind))
            {
                throw new InvalidOperationException(
                    $"Feed '{feedKey}' for provider '{provider.Name}' is missing EntityKind.");
            }

            var datasetRef = NullIfEmpty(feedSection["DatasetRef"]);
            var syncMode = NullIfEmpty(feedSection["SyncMode"]) ?? "snapshot";
            var config = ToJsonObject(feedSection.GetSection("Config"));
            var enabled = ParseBool(feedSection["Enabled"], true);

            var feed = provider.SourceFeeds.SingleOrDefault(candidate =>
                string.Equals(candidate.FeedKey, feedKey, StringComparison.Ordinal));

            if (feed is null)
            {
                provider.SourceFeeds.Add(new SourceFeed
                {
                    ProviderConnection = provider,
                    FeedKey = feedKey,
                    EntityKind = entityKind,
                    DatasetRef = datasetRef,
                    SyncMode = syncMode,
                    Config = config,
                    IsEnabled = enabled,
                    CreatedAt = now,
                });
                changes++;
                continue;
            }

            if (feed.EntityKind == entityKind
                && feed.DatasetRef == datasetRef
                && feed.SyncMode == syncMode
                && feed.IsEnabled == enabled
                && JsonElement.DeepEquals(feed.Config.RootElement, config.RootElement))
            {
                continue;
            }

            feed.EntityKind = entityKind;
            feed.DatasetRef = datasetRef;
            feed.SyncMode = syncMode;
            feed.Config = config;
            feed.IsEnabled = enabled;
            changes++;
        }

        return changes;
    }

    private static JsonDocument ToJsonObject(IConfigurationSection section)
    {
        var value = ToObject(section);
        return JsonDocument.Parse(JsonSerializer.Serialize(value ?? new SortedDictionary<string, object?>()));
    }

    private static object? ToObject(IConfigurationSection section)
    {
        var children = section.GetChildren().ToArray();
        if (children.Length == 0)
        {
            return ParseScalar(section.Value);
        }

        if (children.All(child => int.TryParse(child.Key, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return children
                .OrderBy(child => int.Parse(child.Key, CultureInfo.InvariantCulture))
                .Select(ToObject)
                .ToList();
        }

        var result = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            result[child.Key] = ToObject(child);
        }

        return result;
    }

    private static object? ParseScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (bool.TryParse(value, out var boolean))
        {
            return boolean;
        }

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            return integer;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
        {
            return number;
        }

        return value;
    }

    private static bool ParseBool(string? value, bool fallback) =>
        bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
