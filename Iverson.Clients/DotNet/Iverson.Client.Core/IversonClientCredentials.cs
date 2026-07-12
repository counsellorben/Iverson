namespace Iverson.Client.Core;

public sealed record IversonClientCredentials(string ClientId, string ClientSecret, string TokenEndpoint, string? Scope = null);
