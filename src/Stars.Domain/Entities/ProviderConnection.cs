namespace Stars.Domain.Entities;

/// <summary>
/// Provider configuration. Only a secret reference is stored, never API keys in plaintext.
/// </summary>
public class ProviderConnection
{
    public Guid Id { get; set; }

    public required string Name { get; set; }

    public required string ConnectorKey { get; set; }

    public string? BaseUrl { get; set; }

    public string? AuthSecretRef { get; set; }

    public required JsonDocument Settings { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<SourceFeed> SourceFeeds { get; set; } = [];
}
