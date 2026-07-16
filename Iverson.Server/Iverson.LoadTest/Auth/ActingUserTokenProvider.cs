namespace Iverson.LoadTest.Auth;

public sealed class ActingUserTokenProvider(AuthentikFlowExecutorClient flowClient) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private string? _accessToken;
    private string? _refreshToken;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;

    public async Task<string> GetTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
            return _accessToken;

        await _lock.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAt)
                return _accessToken;

            MintedToken minted;
            if (_refreshToken is not null)
            {
                try
                {
                    minted = await flowClient.RefreshAsync(_refreshToken, ct);
                }
                catch (Exception)
                {
                    // Refresh token rejected/expired — fall back to the full TOTP flow.
                    minted = await flowClient.MintAsync(ct);
                }
            }
            else
            {
                minted = await flowClient.MintAsync(ct);
            }

            _accessToken = minted.AccessToken;
            _refreshToken = minted.RefreshToken ?? _refreshToken;
            // Refresh 60s early so no call ever races a token that expires mid-flight.
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(minted.ExpiresInSeconds - 60);
            return _accessToken;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<string> GetSubAsync(CancellationToken ct = default)
    {
        var token = await GetTokenAsync(ct);
        var payload = token.Split('.')[1];
        var padded = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=')
            .Replace('-', '+').Replace('_', '/');
        var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(padded));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("sub").GetString()!;
    }

    public void Dispose() => flowClient.Dispose();
}

public sealed record ActingUserIdentities(ActingUserTokenProvider Regular, ActingUserTokenProvider Bypass)
{
    // Used identically at every per-request/per-call site in WritePathRunner and ReadPathScenario —
    // centralized here rather than repeating the ternary at all 5 call sites.
    public ActingUserTokenProvider PickRandom() => Random.Shared.Next(2) == 0 ? Regular : Bypass;
}
