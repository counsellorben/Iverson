using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;

namespace Iverson.LoadTest.Auth;

public sealed record AuthentikIdentityConfig(
    string Username,
    string Password,
    string ClientId,
    string RedirectUri,
    string BaseUrl,
    string? HostHeader,
    string CacheTargetToken); // already-mapped: "compose" or "kind"

public sealed record MintedToken(string AccessToken, string? RefreshToken, int ExpiresInSeconds);

public sealed class AuthentikFlowExecutorClient : IDisposable
{
    private const string FlowSlug = "default-authentication-flow";
    private const int MaxFlowStages = 20;
    private const int MaxTotpAttempts = 4;

    private readonly AuthentikIdentityConfig identity;
    private readonly ILogger<AuthentikFlowExecutorClient> logger;

    // Kept as a field (rather than only inside the handler below) so SendAsync can read the
    // authentik_csrf cookie back out of it for the CSRF header — see R15. Built in the
    // constructor body (not a field initializer) because a field initializer cannot reference
    // another instance field (CS0236), and _http's handler needs this same container instance.
    private readonly System.Net.CookieContainer _cookies = new();

    private readonly HttpClient _http;

    public AuthentikFlowExecutorClient(
        AuthentikIdentityConfig identity,
        ILogger<AuthentikFlowExecutorClient> logger)
    {
        this.identity = identity;
        this.logger = logger;
        _http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = _cookies,
        });
    }

    // For tests: drives the client over a caller-supplied handler. HttpClient never follows
    // redirects itself (HttpClientHandler does), so the handler sees each 302 exactly as the
    // constructor above's AllowAutoRedirect = false handler returns it. The cookie container
    // stays empty, so SendAsync sends no CSRF header.
    public AuthentikFlowExecutorClient(
        AuthentikIdentityConfig identity,
        ILogger<AuthentikFlowExecutorClient> logger,
        HttpMessageHandler handler)
    {
        this.identity = identity;
        this.logger = logger;
        _http = new HttpClient(handler);
    }

    // RFC 6238 TOTP — HMAC-SHA1, 6 digits, 30s period. Mirrors mint_acting_user_token.py's `totp()`.
    private static string Totp(string secretBase32, DateTimeOffset? at = null)
    {
        var padded = secretBase32.ToUpperInvariant().PadRight(
            secretBase32.Length + (8 - secretBase32.Length % 8) % 8, '=');
        var key = Base32Decode(padded);
        var counter = (long)((at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30);
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        var offset = hash[^1] & 0x0F;
        var code = ((hash[offset] & 0x7F) << 24 | (hash[offset + 1] & 0xFF) << 16 |
                    (hash[offset + 2] & 0xFF) << 8 | (hash[offset + 3] & 0xFF)) % 1_000_000;
        return code.ToString("D6");
    }

    // RFC 4648 base32 decode (A-Z2-7 alphabet, 5 bits/char) — mirrors Python's base64.b32decode.
    private static byte[] Base32Decode(string s)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var trimmed = s.TrimEnd('=');
        var bits = new List<byte>();
        int buffer = 0, bitsInBuffer = 0;
        foreach (var ch in trimmed)
        {
            var val = alphabet.IndexOf(ch);
            if (val < 0) throw new FormatException($"Invalid base32 character '{ch}'");
            buffer = (buffer << 5) | val;
            bitsInBuffer += 5;
            if (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                bits.Add((byte)((buffer >> bitsInBuffer) & 0xFF));
            }
        }
        return bits.ToArray();
    }

    private static (string Verifier, string Challenge) GeneratePkce()
    {
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Convert.ToBase64String(verifierBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        using var sha256 = SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return (verifier, challenge);
    }

    private static string CacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "iverson");

    private string CachePath =>
        Path.Combine(CacheDir, $"acting-user-totp-secret-{identity.CacheTargetToken}-{identity.Username}.txt");

    private string? LoadCachedTotpSecret() =>
        Environment.GetEnvironmentVariable("IVERSON_TOTP_SECRET") is { Length: > 0 } env ? env :
        File.Exists(CachePath) ? File.ReadAllText(CachePath).Trim() is { Length: > 0 } s ? s : null : null;

    // The secret is a long-lived second factor, so neither the directory nor the file may ever
    // exist at a mode another local user can read. A create mode applies only to a file this call
    // creates, so a file left at a looser mode by an older build is restricted BEFORE the new
    // secret goes into it. Nothing changes the mode after the write: a test can then see the
    // create mode itself, which a chmod after the write would mask.
    private void SaveCachedTotpSecret(string secret)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(CacheDir);
            File.WriteAllText(CachePath, secret + "\n");
        }
        else
        {
            // As with the file below: a directory that already exists is restricted, and a new one is
            // created owner-only. A chmod after creating it would mask the create mode from the tests.
            if (Directory.Exists(CacheDir))
                File.SetUnixFileMode(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            else
                Directory.CreateDirectory(CacheDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            if (File.Exists(CachePath))
                File.SetUnixFileMode(CachePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var stream = new FileStream(CachePath, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
            });
            using var writer = new StreamWriter(stream);
            writer.Write(secret + "\n");
        }
        logger.LogInformation("Cached new TOTP secret for future runs at {Path}", CachePath);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, HttpContent? content = null)
    {
        var req = new HttpRequestMessage(method, url) { Content = content };
        req.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        if (identity.HostHeader is { Length: > 0 } h) req.Headers.Host = h;

        // Authentik's flow executor only exempts POSTs from CSRF while the session is
        // unauthenticated — the very first login of a process. Once that login succeeds, the
        // handler's CookieContainer carries an authenticated session cookie for the rest of the
        // process's life, and every later POST through this same client (e.g. a re-authentication
        // triggered by MintAsync after the cached token expires) is session-authenticated and
        // CSRF-checked, even though nothing here was ever sending the CSRF header — hence R15.
        // Authentik's contract: echo the `authentik_csrf` cookie back as `X-authentik-CSRF`; send
        // nothing when that cookie doesn't exist yet (the unauthenticated first login).
        if (method != HttpMethod.Get)
        {
            var csrfCookie = _cookies.GetCookies(new Uri(identity.BaseUrl))["authentik_csrf"];
            if (csrfCookie is not null) req.Headers.Add("X-authentik-CSRF", csrfCookie.Value);
        }

        return await _http.SendAsync(req);
    }

    private async Task DriveAuthenticationFlowAsync(string flowUrl)
    {
        string? cachedSecret = null;
        var totpState = new TotpAttemptState();

        for (var i = 0; i < MaxFlowStages; i++)
        {
            var getResp = await SendAsync(HttpMethod.Get, flowUrl);
            var challengeJson = await getResp.Content.ReadAsStringAsync();
            using var challenge = JsonDocument.Parse(challengeJson);
            var root = challenge.RootElement;
            var component = root.TryGetProperty("component", out var c) ? c.GetString() : null;
            logger.LogDebug("flow stage: {Component}", component);

            switch (component)
            {
                case "xak-flow-redirect":
                    return;

                case "ak-stage-identification":
                    await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { uid_field = identity.Username }));
                    continue;

                case "ak-stage-password":
                    await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { password = identity.Password }));
                    continue;

                case "ak-stage-authenticator-validate":
                {
                    var hasDeviceChallenges = root.TryGetProperty("device_challenges", out var dc) &&
                        dc.ValueKind == JsonValueKind.Array && dc.GetArrayLength() > 0;
                    if (hasDeviceChallenges)
                    {
                        cachedSecret ??= LoadCachedTotpSecret()
                            ?? throw new InvalidOperationException(
                                $"This user already has an enrolled TOTP device on the server, but no locally " +
                                $"cached secret exists at {CachePath}. Authentik never re-exposes an enrolled " +
                                "device's secret — restore the cached secret file, or reset the user's TOTP " +
                                "device in Authentik to force re-enrollment.");
                        await SubmitTotpCodeAsync(flowUrl, cachedSecret, totpState);
                        continue;
                    }

                    JsonElement totpStage = default;
                    if (root.TryGetProperty("configuration_stages", out var stages))
                    {
                        foreach (var s in stages.EnumerateArray())
                        {
                            if (s.TryGetProperty("meta_model_name", out var m) &&
                                (m.GetString() ?? "").EndsWith("authenticatortotpstage"))
                            {
                                totpStage = s;
                                break;
                            }
                        }
                    }
                    if (totpStage.ValueKind == JsonValueKind.Undefined)
                        throw new InvalidOperationException(
                            "No TOTP configuration stage is offered by the authenticator-validate stage.");
                    var pk = totpStage.GetProperty("pk");
                    await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { selected_stage = pk.ToString() }));
                    continue;
                }

                case "ak-stage-authenticator-totp":
                {
                    var configUrl = root.GetProperty("config_url").GetString()!;
                    var secret = ParseTotpSecretFromConfigUrl(configUrl);
                    SaveCachedTotpSecret(secret);
                    cachedSecret = secret;
                    await SubmitTotpCodeAsync(flowUrl, secret, totpState);
                    continue;
                }

                default:
                    throw new InvalidOperationException($"Unhandled flow-executor component '{component}': {challengeJson}");
            }
        }

        throw new InvalidOperationException($"Authentication flow did not complete after {MaxFlowStages} stages");
    }

    // Tracks TOTP replay-window state across DriveAuthenticationFlowAsync's loop. A plain class
    // (not `ref` locals) because async methods cannot have `ref`/`out`/`in` parameters in C#
    // (CS1988) — mirrors mint_acting_user_token.py's own TotpAttemptState class.
    private sealed class TotpAttemptState
    {
        public int? LastCounter;
        public int Attempts;
    }

    // Authentik marks a TOTP code "used" on any submission attempt within its 30s window, success
    // or failure — so submitting twice in the same window always fails the second time. Wait out
    // the rest of the window rather than misreport that as a real validation failure.
    private async Task SubmitTotpCodeAsync(string flowUrl, string secret, TotpAttemptState state)
    {
        if (state.Attempts >= MaxTotpAttempts)
            throw new InvalidOperationException(
                $"TOTP code was rejected {MaxTotpAttempts} times in a row; giving up. If this isn't just " +
                "window-reuse, the cached secret is likely stale.");

        var now = DateTimeOffset.UtcNow;
        var counter = (int)(now.ToUnixTimeSeconds() / 30);
        if (state.LastCounter == counter)
        {
            var waitMs = (int)((counter + 1) * 30 - now.ToUnixTimeSeconds()) * 1000 + 500;
            logger.LogInformation("Waiting {Ms}ms for a fresh TOTP time-step...", waitMs);
            await Task.Delay(waitMs);
        }
        var code = Totp(secret);
        state.LastCounter = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        state.Attempts++;
        await SendAsync(HttpMethod.Post, flowUrl, JsonBody(new { code }));
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static string ParseTotpSecretFromConfigUrl(string configUrl)
    {
        var uri = new Uri(configUrl);
        var query = HttpUtility.ParseQueryString(uri.Query);
        return query["secret"] ?? throw new InvalidOperationException("config_url has no 'secret' param");
    }

    public async Task<MintedToken> MintAsync(CancellationToken ct = default)
    {
        var flowUrl = $"{identity.BaseUrl}/api/v3/flows/executor/{FlowSlug}/";
        await DriveAuthenticationFlowAsync(flowUrl);

        var (verifier, challenge) = GeneratePkce();
        var state = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var query = $"client_id={Uri.EscapeDataString(identity.ClientId)}" +
                    $"&redirect_uri={Uri.EscapeDataString(identity.RedirectUri)}" +
                    "&response_type=code&scope=openid%20groups%20tenant_id%20offline_access" +
                    $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
        var authorizeResp = await SendAsync(HttpMethod.Get, $"{identity.BaseUrl}/application/o/authorize/?{query}");
        if (authorizeResp.StatusCode != System.Net.HttpStatusCode.Redirect &&
            (int)authorizeResp.StatusCode != 302)
            throw new InvalidOperationException($"Expected a 302 from /application/o/authorize/, got {authorizeResp.StatusCode}");
        var location = authorizeResp.Headers.Location
            ?? throw new InvalidOperationException("302 from /application/o/authorize/ had no Location header");
        var code = HttpUtility.ParseQueryString(location.Query)["code"]
            ?? throw new InvalidOperationException($"302 Location had no 'code': {location}");

        var tokenResp = await SendAsync(HttpMethod.Post, $"{identity.BaseUrl}/application/o/token/",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = identity.RedirectUri,
                ["client_id"] = identity.ClientId,
                ["code_verifier"] = verifier,
            }));
        return await ParseTokenResponseAsync(tokenResp);
    }

    /// <summary>
    /// Sets <see cref="AuthentikIdentityConfig.Password"/> as the user's password by driving the
    /// one-time recovery link CreateTenant returns (Tenant.admin_recovery_link) through Authentik's
    /// flow executor. Only the link's flow slug and flow_token are used: its host is the API
    /// server's internal view of Authentik, so requests go to identity.BaseUrl with the configured
    /// Host header, like MintAsync's. The link is a bearer credential, so no message includes it.
    /// </summary>
    public async Task SetPasswordFromRecoveryLinkAsync(string recoveryLink)
    {
        if (string.IsNullOrEmpty(recoveryLink))
            throw new InvalidOperationException(
                $"CreateTenant returned no admin recovery link, so '{identity.Username}' has no password " +
                "and the data plane cannot log in.");
        if (!Uri.TryCreate(recoveryLink, UriKind.Absolute, out var link))
            throw new InvalidOperationException("The admin recovery link is malformed: it is not an absolute URL.");
        var slug = link.AbsolutePath.TrimEnd('/').Split('/')[^1];
        var token = HttpUtility.ParseQueryString(link.Query)["flow_token"];
        if (string.IsNullOrEmpty(slug) || string.IsNullOrEmpty(token))
            throw new InvalidOperationException("The admin recovery link is malformed: it has no flow slug or no flow_token.");

        var flowUrl = $"{identity.BaseUrl}/api/v3/flows/executor/{slug}/?query=" +
                      Uri.EscapeDataString($"flow_token={token}");

        var prompt = await ReadComponentAsync(await SendAsync(HttpMethod.Get, flowUrl));
        if (prompt != "ak-stage-prompt")
            throw new InvalidOperationException($"The recovery flow did not offer the password prompt; it answered '{prompt}'.");

        var resp = await SendAsync(HttpMethod.Post, flowUrl,
            JsonBody(new { password = identity.Password, password_repeat = identity.Password }));

        // A 200 here is a rejection: Authentik re-renders the prompt with response_errors.
        if (resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            using var rejected = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = rejected.RootElement;
            var errors = root.TryGetProperty("response_errors", out var re) && re.ValueKind == JsonValueKind.Object
                ? re.EnumerateObject()
                    .SelectMany(field => field.Value.ValueKind == JsonValueKind.Array ? field.Value.EnumerateArray() : [])
                    .Select(e => e.TryGetProperty("string", out var text) ? text.GetString() : null)
                    .OfType<string>()
                    .ToList()
                : [];
            throw new InvalidOperationException(errors.Count > 0
                ? $"Authentik rejected the tenant admin's password: {string.Join(" ", errors)}"
                : $"The recovery flow answered the password with '{(root.TryGetProperty("component", out var c) ? c.GetString() : null)}' and no error; the password was not set.");
        }

        // Each completed stage (prompt, MFA check, user write) answers a body-less 302 back to the
        // same executor URL; this client doesn't follow redirects, so follow them here.
        for (var i = 0; i < MaxFlowStages && resp.StatusCode == System.Net.HttpStatusCode.Redirect; i++)
            resp = await SendAsync(HttpMethod.Get, flowUrl);
        if (resp.StatusCode == System.Net.HttpStatusCode.Redirect)
            throw new InvalidOperationException($"The recovery flow was still redirecting after {MaxFlowStages} requests.");

        var end = await ReadComponentAsync(resp);
        if (end != "xak-flow-redirect")
            throw new InvalidOperationException($"The recovery flow ended at '{end}', not 'xak-flow-redirect'; the password was not set.");
    }

    private static async Task<string?> ReadComponentAsync(HttpResponseMessage resp)
    {
        if (resp.StatusCode != System.Net.HttpStatusCode.OK)
            throw new InvalidOperationException($"The recovery flow answered HTTP {(int)resp.StatusCode}.");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("component", out var c) ? c.GetString() : null;
    }

    public async Task<MintedToken> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var resp = await SendAsync(HttpMethod.Post, $"{identity.BaseUrl}/application/o/token/",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = identity.ClientId,
            }));
        return await ParseTokenResponseAsync(resp);
    }

    private static async Task<MintedToken> ParseTokenResponseAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Token request failed ({(int)resp.StatusCode}): {body}");
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        return new MintedToken(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null,
            root.GetProperty("expires_in").GetInt32());
    }

    public void Dispose() => _http.Dispose();
}
