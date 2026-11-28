using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Maui.Authentication;
using Microsoft.Maui.Storage;

namespace PersonalFlow.Mobile;

public sealed record MobileServerEndpoints(string IdentityAuthority, string LifeApi, string LearningApi);

public sealed class MobileIdentitySession(HttpClient httpClient)
{
    const string ClientId = "personalflow-native";
    const string Redirect = "app.personalflow:/callback";
    const string RefreshStorageKey = "personalflow.identity.refresh";
    const string IdentitySetting = "personalflow.server.identity";
    const string LifeSetting = "personalflow.server.life";
    const string LearningSetting = "personalflow.server.learning";
    const string DeviceSetting = "personalflow.identity.device";
    static readonly Uri RedirectUri = new(Redirect);
    static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    readonly SemaphoreSlim tokenGate = new(1, 1);
    IConfigurationManager<OpenIdConnectConfiguration>? configurationManager;
    string? managerAuthority;
    string? accessToken;
    DateTimeOffset accessTokenExpiresAt;

    public string IdentityAuthority => Preferences.Default.Get(IdentitySetting, string.Empty);
    public string LifeApi => Preferences.Default.Get(LifeSetting, string.Empty);
    public string LearningApi => Preferences.Default.Get(LearningSetting, string.Empty);
    public bool IsConfigured =>
        TryHttpsBase(IdentityAuthority, out _) && TryHttpsBase(LifeApi, out _) && TryHttpsBase(LearningApi, out _);

    public async Task<bool> HasSessionAsync()
    {
        var saved = await ReadRefreshCredentialAsync();
        return saved is not null && string.Equals(saved.Authority, IdentityAuthority, StringComparison.Ordinal);
    }

    public Task SaveEndpointsAsync(string identityAuthority, string lifeApi, string learningApi)
    {
        if (!TryHttpsBase(identityAuthority, out var identity) ||
            !TryHttpsBase(lifeApi, out var life) || !TryHttpsBase(learningApi, out var learning))
            throw new ArgumentException("Укажите три абсолютных HTTPS адреса.");

        var authority = identity.AbsoluteUri;
        if (!string.Equals(authority, IdentityAuthority, StringComparison.Ordinal))
        {
            SecureStorage.Default.Remove(RefreshStorageKey);
            accessToken = null;
            accessTokenExpiresAt = default;
            configurationManager = null;
            managerAuthority = null;
        }

        Preferences.Default.Set(IdentitySetting, authority);
        Preferences.Default.Set(LifeSetting, life.AbsoluteUri);
        Preferences.Default.Set(LearningSetting, learning.AbsoluteUri);
        return Task.CompletedTask;
    }

    public async Task SignInAsync(CancellationToken cancellationToken = default)
    {
        var identityAuthority = RequireConfiguredIdentity();
        var configuration = await GetConfigurationAsync(identityAuthority, cancellationToken);
        var verifier = RandomBase64Url(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = RandomBase64Url(32);
        var nonce = RandomBase64Url(32);
        var deviceId = Preferences.Default.Get(DeviceSetting, string.Empty);
        if (!Guid.TryParse(deviceId, out _))
        {
            deviceId = Guid.NewGuid().ToString("D");
            Preferences.Default.Set(DeviceSetting, deviceId);
        }

        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["redirect_uri"] = Redirect,
            ["response_type"] = "code",
            ["scope"] = "openid offline_access personalflow",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
            ["nonce"] = nonce,
            ["device_id"] = deviceId,
            ["ui_locales"] = "ru-RU"
        };
        var authorizationUri = AddQuery(configuration.AuthorizationEndpoint, parameters);
        var result = await WebAuthenticator.Default.AuthenticateAsync(
            new WebAuthenticatorOptions { Url = authorizationUri, CallbackUrl = RedirectUri }, cancellationToken);

        var returnedState = Get(result, "state");
        if (!FixedEquals(state, returnedState))
            throw new InvalidOperationException("Ответ входа не прошёл проверку состояния.");
        var error = Get(result, "error");
        if (!string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException("Сервер отклонил вход: " + error);
        var code = Get(result, "code");
        if (string.IsNullOrWhiteSpace(code))
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
        await SaveRefreshCredentialAsync(new(identityAuthority, token.RefreshToken));
        SetAccessToken(token);
    }

    public async Task<bool> SignOutAsync(CancellationToken cancellationToken = default)
    {
        var saved = await ReadRefreshCredentialAsync();
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
            SecureStorage.Default.Remove(RefreshStorageKey);
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

    internal async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenGate.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(accessToken) && accessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30))
                return accessToken;

            var credential = await ReadRefreshCredentialAsync();
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
                await ValidateIdTokenAsync(token.IdToken, configuration, expectedNonce: null, cancellationToken);
            if (!string.IsNullOrWhiteSpace(token.RefreshToken))
                await SaveRefreshCredentialAsync(new(credential.Authority, token.RefreshToken));
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
                metadataAddress,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(httpClient) { RequireHttps = true });
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
        if (!result.IsValid)
            throw new InvalidDataException("Identity ID token не прошёл криптографическую проверку.", result.Exception);
        if (expectedNonce is not null &&
            (!result.Claims.TryGetValue("nonce", out var returnedNonce) ||
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
                SecureStorage.Default.Remove(RefreshStorageKey);
            throw new InvalidOperationException("Identity не выдал токен. Повторно войдите в аккаунт.");
        }
        if (string.IsNullOrWhiteSpace(token.AccessToken) || token.ExpiresIn is < 1 or > 86_400)
            throw new InvalidDataException("Identity вернул некорректный ответ токена.");
        return token;
    }

    void SetAccessToken(TokenResponse token)
    {
        accessToken = token.AccessToken;
        accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(token.ExpiresIn);
    }

    async Task<RefreshCredential?> ReadRefreshCredentialAsync()
    {
        var serialized = await SecureStorage.Default.GetAsync(RefreshStorageKey);
        return string.IsNullOrWhiteSpace(serialized) ? null : JsonSerializer.Deserialize<RefreshCredential>(serialized, JsonOptions);
    }

    static Task SaveRefreshCredentialAsync(RefreshCredential credential) =>
        SecureStorage.Default.SetAsync(RefreshStorageKey, JsonSerializer.Serialize(credential, JsonOptions));

    string RequireConfiguredIdentity()
    {
        if (!TryHttpsBase(IdentityAuthority, out var identity))
            throw new InvalidOperationException("Сначала задайте HTTPS адрес Identity в настройках.");
        return identity.AbsoluteUri;
    }

    static Uri AddQuery(string endpoint, IReadOnlyDictionary<string, string> values)
    {
        var query = string.Join("&", values.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        var builder = new UriBuilder(endpoint) { Query = query };
        return builder.Uri;
    }

    static bool TryHttpsBase(string value, out Uri address)
    {
        address = null!;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) || !string.IsNullOrEmpty(parsed.Query) || !string.IsNullOrEmpty(parsed.Fragment))
            return false;
        address = new Uri(parsed.AbsoluteUri.EndsWith('/') ? parsed.AbsoluteUri : parsed.AbsoluteUri + "/");
        return true;
    }

    static bool TrustedEndpoint(string issuer, string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) && endpointUri.Scheme == Uri.UriSchemeHttps &&
        Uri.TryCreate(issuer, UriKind.Absolute, out var issuerUri) &&
        string.Equals(endpointUri.IdnHost, issuerUri.IdnHost, StringComparison.OrdinalIgnoreCase) && endpointUri.Port == issuerUri.Port;

    static bool SameIssuer(string expected, string actual) =>
        Uri.TryCreate(actual, UriKind.Absolute, out var actualUri) &&
        string.Equals(new Uri(expected).AbsoluteUri.TrimEnd('/'), actualUri.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal);

    static string Get(WebAuthenticatorResult result, string key) => result.Properties.TryGetValue(key, out var value) ? value : string.Empty;
    static string RandomBase64Url(int byteCount) => Base64Url(RandomNumberGenerator.GetBytes(byteCount));
    static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    sealed record RefreshCredential(string Authority, string Token);

    sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; init; } = string.Empty;
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; init; }
        [JsonPropertyName("id_token")] public string? IdToken { get; init; }
        [JsonPropertyName("expires_in")] public int ExpiresIn { get; init; }
        [JsonPropertyName("error")] public string? Error { get; init; }
    }

    sealed class BearerTokenHandler(MobileIdentitySession session) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var token = await session.GetAccessTokenAsync(cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
