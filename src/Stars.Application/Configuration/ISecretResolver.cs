namespace Stars.Application.Configuration;

/// <summary>
/// Resolves a provider secret reference. References are environment variable names;
/// provider_connections only stores the reference, never a key in plaintext.
/// </summary>
public interface ISecretResolver
{
    string? Resolve(string? secretReference);
}

public sealed class EnvironmentSecretResolver : ISecretResolver
{
    public string? Resolve(string? secretReference)
    {
        if (string.IsNullOrWhiteSpace(secretReference))
        {
            return null;
        }

        return Environment.GetEnvironmentVariable(secretReference)
            ?? Environment.GetEnvironmentVariable($"STARS_SECRET_{secretReference}");
    }
}
