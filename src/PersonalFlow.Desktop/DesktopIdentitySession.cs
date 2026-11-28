using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace PersonalFlow.Desktop;

internal sealed record DesktopServerEndpoints(string IdentityAuthority, string LifeApi, string LearningApi);
internal sealed record DesktopEndpointSettings(string IdentityAuthority, string LifeApi, string LearningApi, string DeviceId);

internal sealed class DesktopIdentitySession
{
    const string ClientId = "personalflow-native";
    const string Redirect = "http://127.0.0.1:54782/callback";
    const string RefreshCredentialKey = "desktop-refresh";
    const string DeviceSetting = "personalflow.identity.device";
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    static readonly Uri RedirectUri = new(Redirect);
    readonly HttpClient httpClient;
    readonly IDesktopCredentialStore credentials;
    readonly string settingsPath;
    readonly SemaphoreSlim tokenGate = new(1, 1);
    IConfigurationManager<OpenIdConnectConfiguration>? configurationManager;
    string? managerAuthority;
    string? accessToken;
    DateTimeOffset accessTokenExpiresAt;

    public DesktopIdentitySession(HttpClient httpClient, IDesktopCredentialStore credentials, string appDataDirectory)
    {
        this.httpClient = httpClient;
        this.credentials = credentials;
        settingsPath = Path.Combine(appDataDirectory, "desktop-settings.json");
    }

    DesktopEndpointSettings Settings => LoadSettings();
    public string IdentityAuthority => Settings.IdentityAuthority;
    public string LifeApi => Settings.LifeApi;
    public string LearningApi => Settings.LearningApi;
    public bool IsConfigured => TryHttpsBase(IdentityAuthority, out _) && TryHttpsBase(LifeApi, out _) && TryHttpsBase(LearningApi, out _);

    public Task<bool> HasSessionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var saved = ReadRefreshCredential();
        return Task.FromResult(saved is not null && string.Equals(saved.Authority, IdentityAuthority, StringComparison.Ordinal));
    }

    public void SaveEndpoints(string identityAuthority, string lifeApi, string learningApi)
    {
        if (!TryHttpsBase(identityAuthority, out var identity) ||
            !TryHttpsBase(lifeApi, out var life) || !TryHttpsBase(learningApi, out var learning))
            throw new ArgumentException("Укажите три абсолютных HTTPS адреса.");

        if (!string.Equals(identity.AbsoluteUri, IdentityAuthority, StringComparison.Ordinal))
        {
            credentials.Delete(RefreshCredentialKey);
            accessToken = null;
            accessTokenExpiresAt = default;
            configurationManager = null;
            managerAuthority = null;
        }

        SaveSettings(Settings with
        {
            IdentityAuthority = identity.AbsoluteUri,
            LifeApi = life.AbsoluteUri,
            LearningApi = learning.AbsoluteUri
        });
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var authority = RequireConfiguredIdentity();
        var configuration = await GetConfigurationAsync(authority, cancellationToken);
        var verifier = RandomBase64Url(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomBase64Url(32);
        var nonce = RandomBase64Url(32);
        var current = Settings;
        if (!Guid.TryParse(current.DeviceId, out _))
        {
            current = current with { DeviceId = Guid.NewGuid().ToString("D") };
            SaveSettings(current);
        }

        var authorizationUri = AddQuery(configuration.AuthorizationEndpoint, new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = Redirect,
            ["response_type"] = "code",
            ["scope"] = "openid offline_access personalflow",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
            ["device_id"] = current.DeviceId,
            ["ui_locales"] = "ru-RU"
        });

        var callback = await ReceiveAuthorizationCallbackAsync(authorizationUri, cancellationToken);
        if (!callback.TryGetValue("state", out var returnedState) || !FixedEquals(state, returnedState))
            throw new InvalidOperationException("Ответ входа не прошёл проверку состояния.");
        if (callback.TryGetValue("error", out var error) && !string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("Сервер отклонил вход: " + error);
        if (!callback.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Сервер не вернул код авторизации.");

        var token = await RequestTokenAsync(configuration, new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = ClientId,
            ["code"] = code,
            ["redirect_uri"] = Redirect,
            ["code_verifier"] = verifier
        }, cancellationToken);
        if (string.IsNullOrWhiteSpace(token.IdToken) || string.IsNullOrWhiteSpace(token.RefreshToken))
            throw new InvalidDataException("Identity не вернул обязательные OpenID Connect токены.");

        await ValidateIdTokenAsync(token.IdToken, configuration, nonce, cancellationToken);
        WriteRefreshCredential(new(authority, token.RefreshToken));
        SetAccessToken(token);
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var saved = ReadRefreshCredential();
        var revoked = saved is null;
        try
        {
            if (saved is not null && string.Equals(saved.Authority, IdentityAuthority, StringComparison.Ordinal))
            {
                var configuration = await GetConfigurationAsync(RequireConfiguredIdentity(), cancellationToken);
                using var response = await httpClient.PostAsync(configuration.RevocationEndpoint,
                    new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["token"] = saved.Token,
                        ["token_type_hint"] = "refresh_token",
                        ["client_id"] = ClientId
                    }), cancellationToken);
                revoked = response.IsSuccessStatusCode;
            }
        }
        finally
        {
            credentials.Delete(RefreshCredentialKey);
            accessToken = null;
            accessTokenExpiresAt = default;
        }
        return revoked;
    }

    public HttpClient CreateApiClient(string baseAddress)
    {
        if (!TryHttpsBase(baseAddress, out var address))
            throw new InvalidOperationException("API адрес должен быть абсолютным HTTPS URL.");
        var handler = new BearerTokenHandler(this) { InnerHandler = new HttpClientHandler() };
        return new HttpClient(handler, disposeHandler: true) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) };
    }

    async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken) && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
                return accessToken;

            var credential = ReadRefreshCredential();
            if (credential is null || !string.Equals(credential.Authority, IdentityAuthority, StringComparison.Ordinal))
                throw new InvalidOperationException("Войдите в PersonalFlow, чтобы синхронизировать данные.");

            var configuration = await GetConfigurationAsync(RequireConfiguredIdentity(), cancellationToken);
            var token = await RequestTokenAsync(configuration, new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = ClientId,
                ["refresh_token"] = credential.Token
            }, cancellationToken);
            if (!string.IsNullOrWhiteSpace(token.IdToken))
                await ValidateIdTokenAsync(token.IdToken, configuration, null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
                WriteRefreshCredential(new(credential.Authority, token.RefreshToken));
            SetAccessToken(token);
            return accessToken!;
        }
        finally { tokenGate.Release(); }
    }

    async Task<OpenIdConnectConfiguration> GetConfigurationAsync(string authority, CancellationToken cancellationToken)
    {
        if (!string.Equals(managerAuthority, authority, StringComparison.Ordinal) || configurationManager is null)
        {
            var metadataAddress = new Uri(new Uri(authority), ".well-known/openid-configuration").AbsoluteUri;
            configurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadataAddress, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever(httpClient) { RequireHttps = true });
            managerAuthority = authority;
        }

        var configuration = await configurationManager.GetConfigurationAsync(cancellationToken);
        if (!SameIssuer(authority, configuration.Issuer) || configuration.SigningKeys.Count == 0 ||
            !TrustedEndpoint(authority, configuration.AuthorizationEndpoint) ||
            !TrustedEndpoint(authority, configuration.TokenEndpoint) ||
            !TrustedEndpoint(authority, configuration.RevocationEndpoint))
            throw new InvalidDataException("Identity discovery metadata is incomplete or points to an unexpected HTTPS origin.");
        return configuration;
    }

    static async Task ValidateIdTokenAsync(string token, OpenIdConnectConfiguration configuration, string? expectedNonce, CancellationToken cancellationToken)
    {
        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            ValidateIssuer = true,
            ValidAudience = ClientId,
            ValidateAudience = true,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(60),
            ValidAlgorithms = ["RS256", "RS384", "RS512", "ES256", "ES384", "ES512"]
        });
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsValid) throw new InvalidDataException("Identity ID token не прошёл криптографическую проверку.", result.Exception);
        if (expectedNonce is not null && (!result.Claims.TryGetValue("nonce", out var returnedNonce) ||
             !FixedEquals(expectedNonce, returnedNonce?.ToString() ?? string.Empty)))
            throw new InvalidDataException("Identity ID token не прошёл проверку nonce.");
    }

    async Task<TokenResponse> RequestTokenAsync(OpenIdConnectConfiguration configuration, Dictionary<string, string> fields, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(fields), cancellationToken);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken);
        if (!response.IsSuccessStatusCode || token is null || !string.IsNullOrWhiteSpace(token.Error))
        {
            if (token?.Error == "invalid_grant" && fields.TryGetValue("grant_type", out var grant) && grant == "refresh_token")
                credentials.Delete(RefreshCredentialKey);
            throw new InvalidOperationException("Identity не выдал токен. Повторно войдите в аккаунт.");
        }
        if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn is < 1 or > 86_400)
            throw new InvalidDataException("Identity вернул некорректный ответ токена.");
        return token;
    }

    async Task<Dictionary<string, string>> ReceiveAuthorizationCallbackAsync(Uri authorizationUri, CancellationToken cancellationToken)
    {
        using var listener = new TcpListener(IPAddress.Loopback, RedirectUri.Port);
        try { listener.Start(1); }
        catch (SocketException ex) { throw new InvalidOperationException("Локальный порт для безопасного входа уже занят. Закройте другое приложение и повторите вход.", ex); }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var browserProcess = Process.Start(new ProcessStartInfo(authorizationUri.AbsoluteUri) { UseShellExecute = true });
            if (browserProcess is null) throw new InvalidOperationException("Не удалось открыть системный браузер.");
            browserProcess.Dispose();
            while (true)
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token);
                var request = await ReadLoopbackRequestAsync(client, timeout.Token);
                if (request is null) continue;
                var (path, query) = request.Value;
                if (path != "/callback")
                {
                    await SendLoopbackResponseAsync(client, 404, "Не найдено", timeout.Token);
                    continue;
                }
                await SendLoopbackResponseAsync(client, 200, "Вход завершён. Можно вернуться в PersonalFlow и закрыть эту вкладку.", timeout.Token);
                return query;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Вход не завершился за пять минут. Повторите попытку.");
        }
    }

    static async Task<(string Path, Dictionary<string, string> Query)?> ReadLoopbackRequestAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        using var data = new MemoryStream();
        var buffer = new byte[2048];
        while (data.Length <= 16_384)
        {
            var count = await stream.ReadAsync(buffer, cancellationToken);
            if (count == 0) return null;
            data.Write(buffer, 0, count);
            var bytes = data.GetBuffer();
            var length = checked((int)data.Length);
            for (var index = Math.Max(0, length - count - 3); index <= length - 4; index++)
                if (bytes[index] == '\r' && bytes[index + 1] == '\n' && bytes[index + 2] == '\r' && bytes[index + 3] == '\n')
                {
                    var firstLine = Encoding.ASCII.GetString(bytes, 0, length).Split("\r\n", 2, StringSplitOptions.None)[0];
                    var parts = firstLine.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length != 3 || parts[0] != "GET" || !Uri.TryCreate(new Uri("http://127.0.0.1"), parts[1], out var uri)) return null;
                    return (uri.AbsolutePath, ParseQuery(uri.Query));
                }
        }
        throw new InvalidDataException("Loopback callback request is too large.");
    }

    static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            var key = Uri.UnescapeDataString((separator < 0 ? part : part[..separator]).Replace('+', ' '));
            var value = Uri.UnescapeDataString((separator < 0 ? string.Empty : part[(separator + 1)..]).Replace('+', ' '));
            if (!result.TryAdd(key, value)) throw new InvalidDataException("Loopback callback contains duplicate parameters.");
        }
        return result;
    }

    static async Task SendLoopbackResponseAsync(TcpClient client, int status, string message, CancellationToken cancellationToken)
    {
        const string template = "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>PersonalFlow</title><body><main><h1>PersonalFlow</h1><p>{0}</p></main></body></html>";
        var body = Encoding.UTF8.GetBytes(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, WebUtility.HtmlEncode(message)));
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {(status == 200 ? "OK" : "Not Found")}\r\nContent-Type: text/html; charset=utf-8\r\nCache-Control: no-store\r\nConnection: close\r\nContent-Length: {body.Length}\r\n\r\n");
        var stream = client.GetStream();
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    static Uri AddQuery(string endpoint, IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new UriBuilder(endpoint) { Query = query }.Uri;
    }

    static bool TryHttpsBase(string value, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment)) return false;
        address = new Uri(parsed.AbsoluteUri.EndsWith('/') ? parsed.AbsoluteUri : parsed.AbsoluteUri + "/");
        return true;
    }

    static bool TrustedEndpoint(string issuer, string endpoint) => Uri.TryCreate(endpoint, UriKind.Absolute, out var value) &&
        value.Scheme == Uri.UriSchemeHttps && Uri.TryCreate(issuer, UriKind.Absolute, out var authority) &&
        string.Equals(value.IdnHost, authority.IdnHost, StringComparison.OrdinalIgnoreCase) && value.Port == authority.Port;

    static bool SameIssuer(string expected, string actual) => Uri.TryCreate(actual, UriKind.Absolute, out var uri) &&
        string.Equals(new Uri(expected).AbsoluteUri.TrimEnd('/'), uri.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    static string RequireEndpoint(string endpoint, string field) => TryHttpsBase(endpoint, out var address)
        ? address.AbsoluteUri : throw new InvalidOperationException($"Настройте HTTPS адрес {field}.");

    string RequireConfiguredIdentity() => RequireEndpoint(IdentityAuthority, "Identity");

    DesktopEndpointSettings LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
                return JsonSerializer.Deserialize<DesktopEndpointSettings>(File.ReadAllText(settingsPath), JsonOptions)
                    ?? EmptySettings;
        }
        catch (JsonException) { }
        return EmptySettings;
    }

    void SaveSettings(DesktopEndpointSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var temporary = settingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temporary, settingsPath, overwrite: true);
    }

    DesktopRefreshCredential? ReadRefreshCredential()
    {
        var value = credentials.Read(RefreshCredentialKey);
        return string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.Deserialize<DesktopRefreshCredential>(value, JsonOptions);
    }

    void WriteRefreshCredential(DesktopRefreshCredential credential) =>
        credentials.Write(RefreshCredentialKey, JsonSerializer.Serialize(credential, JsonOptions));

    void SetAccessToken(TokenResponse token)
    {
        accessToken = token.AccessToken;
        accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
    }

    static readonly DesktopEndpointSettings EmptySettings = new(string.Empty, string.Empty, string.Empty, string.Empty);

    static string RandomBase64Url(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));
    static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    sealed record DesktopRefreshCredential(string Authority, string Token);

    sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("id_token")] public string? IdToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
    }

    sealed class BearerTokenHandler(DesktopIdentitySession session) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await session.GetAccessTokenAsync(cancellationToken));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
