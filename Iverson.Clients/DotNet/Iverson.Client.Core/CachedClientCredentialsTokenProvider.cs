using IdentityModel.Client;

namespace Iverson.Client.Core;

internal sealed class CachedClientCredentialsTokenProvider(IversonClientCredentials credentials) : IDisposable
{
    private readonly HttpClient _httpClient = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync()
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _token;

        await _lock.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _token;

            var response = await _httpClient.RequestClientCredentialsTokenAsync(new ClientCredentialsTokenRequest
            {
                Address = credentials.TokenEndpoint,
                ClientId = credentials.ClientId,
                ClientSecret = credentials.ClientSecret,
                Scope = credentials.Scope,
            });

            if (response.IsError)
                throw new InvalidOperationException($"Failed to acquire Iverson client token: {response.Error}");

            _token = response.AccessToken;
            // Refresh 60s early so no call ever races a token that expires mid-flight.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(response.ExpiresIn - 60);
            return _token!;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose() => _httpClient.Dispose();
}
