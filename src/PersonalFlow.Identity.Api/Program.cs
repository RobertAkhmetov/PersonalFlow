using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Encodings.Web;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using PersonalFlow.Hosting;
using static OpenIddict.Abstractions.OpenIddictConstants;

var builder = WebApplication.CreateBuilder(args);
HostingDefaults.Configure(builder);
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = "__Host-PersonalFlow.Antiforgery";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
});

var identityAuthority = builder.Configuration["Identity:Authority"] ?? throw new InvalidOperationException("Configure Identity:Authority.");
if (!Uri.TryCreate(identityAuthority, UriKind.Absolute, out var identityUri) || (builder.Environment.IsProduction() && identityUri.Scheme != Uri.UriSchemeHttps))
    throw new InvalidOperationException("Identity:Authority must be an absolute HTTPS URL in production.");
var tokenCertificate = LoadTokenCertificate(builder.Configuration, builder.Environment);
var dataProtectionPath = builder.Configuration["DataProtection:Path"] ??
    (builder.Environment.IsDevelopment() ? Path.Combine(Path.GetTempPath(), "personalflow-identity-keys") : throw new InvalidOperationException("Configure DataProtection:Path to a persistent directory."));
Directory.CreateDirectory(dataProtectionPath);
var dataProtection = builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath)).SetApplicationName("PersonalFlow.Identity");
if (tokenCertificate is not null) dataProtection.ProtectKeysWithCertificate(tokenCertificate);
var connectionString = DatabaseSecrets.GetConnectionString(builder.Configuration);
builder.Services.AddDbContext<IdentityDb>(options => { options.UseNpgsql(connectionString); options.UseOpenIddict(); });
builder.Services.AddIdentity<IdentityUser, IdentityRole>(options =>
{
    options.Password.RequiredLength = 14;
    options.Password.RequireDigit = true;
    options.Password.RequireLowercase = true;
    options.Password.RequireUppercase = true;
    options.Password.RequireNonAlphanumeric = true;
    options.Lockout.MaxFailedAccessAttempts = 5;
    options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    options.User.RequireUniqueEmail = false;
}).AddEntityFrameworkStores<IdentityDb>().AddDefaultTokenProviders();
builder.Services.AddAuthorization();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = "__Host-PersonalFlow.Identity";
    options.Cookie.Path = "/";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.ExpireTimeSpan = TimeSpan.FromHours(12);
    options.SlidingExpiration = true;
    options.LoginPath = "/login";
});

builder.Services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<IdentityDb>()).AddServer(options =>
{
    options.SetIssuer(new Uri(identityUri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"));
    options.SetAuthorizationEndpointUris("connect/authorize")
        .SetTokenEndpointUris("connect/token")
        .SetRevocationEndpointUris("connect/revoke");
    options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow().RequireProofKeyForCodeExchange();
    options.RegisterScopes(Scopes.OpenId, Scopes.OfflineAccess, "personalflow");
    options.DisableAccessTokenEncryption();
    options.SetAccessTokenLifetime(TimeSpan.FromMinutes(5));
    options.SetRefreshTokenLifetime(TimeSpan.FromDays(30));
    if (tokenCertificate is null)
        options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
    else
        options.AddSigningCertificate(tokenCertificate).AddEncryptionCertificate(tokenCertificate);
    options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("oauth", context => RateLimitPartition.GetFixedWindowLimiter(context.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
HostingDefaults.ConfigurePipeline(app);
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers.Pragma = "no-cache";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IdentityDb>();
    if (await VersionedSqlMigrations.HandleCommandAsync(db, args, 70312418))
    {
        if (args.Contains("--migrate", StringComparer.Ordinal))
            await EnsureNativeClientAsync(scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>());
        return;
    }
}

app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapGet("/health/ready", async (IdentityDb db, IOpenIddictApplicationManager applications, CancellationToken ct) =>
{
    var schemaReady = await VersionedSqlMigrations.IsCurrentAsync(db, ct);
    var clientReady = await applications.FindByClientIdAsync("personalflow-native", ct) is not null;
    return schemaReady && clientReady ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});
if (app.Environment.IsDevelopment()) app.MapOpenApi();

if (args.Contains("--provision-owner", StringComparer.Ordinal))
{
    using var scope = app.Services.CreateScope();
    var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    if (await users.Users.AnyAsync()) throw new InvalidOperationException("An owner already exists. Owner provisioning is one-time only.");
    var name = builder.Configuration["Owner:Name"] ?? throw new InvalidOperationException("Configure Owner:Name.");
    var password = DatabaseSecrets.Get(builder.Configuration, "Owner:Password", "Owner:PasswordFile");
    var id = builder.Configuration["Owner:Id"] ?? Guid.NewGuid().ToString("D");
    var owner = new IdentityUser { Id = id, UserName = name, EmailConfirmed = true };
    var result = await users.CreateAsync(owner, password);
    if (!result.Succeeded) throw new InvalidOperationException("Owner creation failed: " + string.Join(",", result.Errors.Select(error => error.Code)));
    await users.ResetAuthenticatorKeyAsync(owner);
    Console.WriteLine($"OWNER_ID={owner.Id}");
    return;
}

app.MapGet("/login", (HttpContext context, IAntiforgery antiforgery, string? returnUrl) =>
{
    var tokens = antiforgery.GetAndStoreTokens(context);
    var safeReturnUrl = IsLocalReturnUrl(returnUrl) ? returnUrl! : "/setup";
    return Results.Content(LoginHtml(tokens.RequestToken!, safeReturnUrl), "text/html; charset=utf-8");
});
app.MapPost("/login", async (HttpContext context, IAntiforgery antiforgery, SignInManager<IdentityUser> signIn, UserManager<IdentityUser> users) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var form = await context.Request.ReadFormAsync();
    var name = form["name"].ToString(); var password = form["password"].ToString();
    if (name.Length is 0 or > 256 || password.Length > 1024) return Results.Problem(statusCode: 401, title: "Не удалось войти");
    var user = await users.FindByNameAsync(name);
    if (user is null) return Results.Problem(statusCode: 401, title: "Не удалось войти");
    var passwordResult = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
    if (!passwordResult.Succeeded && !passwordResult.RequiresTwoFactor) return Results.Problem(statusCode: 401, title: "Не удалось войти");
    if (user.TwoFactorEnabled)
    {
        var recoveryCode = form["recovery"].ToString();
        var valid = string.IsNullOrWhiteSpace(recoveryCode)
            ? await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, form["code"].ToString().Replace(" ", "", StringComparison.Ordinal))
            : (await users.RedeemTwoFactorRecoveryCodeAsync(user, recoveryCode)).Succeeded;
        if (!valid) { await users.AccessFailedAsync(user); return Results.Problem(statusCode: 401, title: "Не удалось войти"); }
    }
    var claims = new[] { new Claim("mfa", user.TwoFactorEnabled ? "true" : "false") };
    await signIn.SignInWithClaimsAsync(user, isPersistent: false, claims);
    if (!user.TwoFactorEnabled) return Results.LocalRedirect("/setup");
    var destination = form["returnUrl"].ToString();
    return Results.LocalRedirect(IsLocalReturnUrl(destination) ? destination : "/setup");
}).RequireRateLimiting("login");

app.MapGet("/setup", async (HttpContext context, IAntiforgery antiforgery, UserManager<IdentityUser> users) =>
{
    var user = await users.GetUserAsync(context.User);
    if (user is null) return Results.Redirect("/login");
    if (user.TwoFactorEnabled) return Results.Content("<html lang='ru'><body><h1>Вход настроен</h1><p>Вернитесь в PersonalFlow и повторите вход.</p></body></html>", "text/html; charset=utf-8");
    var tokens = antiforgery.GetAndStoreTokens(context);
    var key = HtmlEncoder.Default.Encode((await users.GetAuthenticatorKeyAsync(user))!);
    return Results.Content($"<html lang='ru'><meta name='viewport' content='width=device-width'/><body><h1>Настройка второго фактора</h1><p>Добавьте этот ключ в приложение-аутентификатор (TOTP, 6 цифр, 30 секунд):</p><code>{key}</code><form method='post'><input type='hidden' name='__RequestVerificationToken' value='{HtmlEncoder.Default.Encode(tokens.RequestToken!)}'/><label>Код <input name='code' inputmode='numeric' autocomplete='one-time-code' maxlength='6'/></label><button>Подтвердить</button></form></body></html>", "text/html; charset=utf-8");
}).RequireAuthorization();
app.MapPost("/setup", async (HttpContext context, IAntiforgery antiforgery, UserManager<IdentityUser> users, SignInManager<IdentityUser> signIn) =>
{
    await antiforgery.ValidateRequestAsync(context);
    var user = await users.GetUserAsync(context.User); var form = await context.Request.ReadFormAsync();
    var code = form["code"].ToString().Replace(" ", "", StringComparison.Ordinal);
    if (user is null || user.TwoFactorEnabled || code.Length != 6 || !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, code)) return Results.BadRequest();
    await users.SetTwoFactorEnabledAsync(user, true);
    var recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
    await signIn.SignOutAsync();
    return Results.Content("<html lang='ru'><body><h1>Сохраните коды восстановления</h1><pre>" + HtmlEncoder.Default.Encode(string.Join("\n", recoveryCodes!)) + "</pre><p>Войдите повторно из приложения.</p></body></html>", "text/html; charset=utf-8");
}).RequireAuthorization().RequireRateLimiting("login");
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<IdentityUser> signIn) =>
{
    await antiforgery.ValidateRequestAsync(context); await signIn.SignOutAsync(); return Results.Redirect("/login");
}).RequireAuthorization();

app.MapGet("/connect/authorize", async (HttpContext context, UserManager<IdentityUser> users) =>
{
    var request = context.GetOpenIddictServerRequest();
    if (request is null) return Results.BadRequest();
    var cookie = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);
    var user = cookie.Succeeded ? await users.GetUserAsync(cookie.Principal!) : null;
    if (user is null || cookie.Principal?.FindFirst("mfa")?.Value != "true" || !user.TwoFactorEnabled)
        return Results.Challenge(new AuthenticationProperties { RedirectUri = context.Request.Path + context.Request.QueryString }, [IdentityConstants.ApplicationScheme]);

    var identity = new ClaimsIdentity(TokenValidationParameters.DefaultAuthenticationType, Claims.Name, Claims.Role);
    identity.SetClaim(Claims.Subject, user.Id);
    identity.SetClaim("security_stamp", await users.GetSecurityStampAsync(user));
    var deviceId = request.GetParameter("device_id")?.ToString();
    if (deviceId is null || !Guid.TryParse(deviceId, out _)) deviceId = Guid.NewGuid().ToString("D");
    identity.SetClaim("device_id", deviceId);
    var principal = new ClaimsPrincipal(identity);
    principal.SetScopes(request.GetScopes()); principal.SetResources("personalflow");
    identity.SetDestinations(claim => claim.Type is Claims.Subject or "device_id" ? [Destinations.AccessToken, Destinations.IdentityToken] : Array.Empty<string>());
    return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}).RequireRateLimiting("oauth");
app.MapPost("/connect/token", async (HttpContext context, UserManager<IdentityUser> users) =>
{
    var request = context.GetOpenIddictServerRequest();
    if (request is null || (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())) return Results.BadRequest();
    var authentication = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    var principal = authentication.Principal;
    var subject = principal?.GetClaim(Claims.Subject);
    if (principal is null || string.IsNullOrWhiteSpace(subject)) return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    var user = await users.FindByIdAsync(subject);
    if (user is null || !user.TwoFactorEnabled || await users.IsLockedOutAsync(user) || await users.GetSecurityStampAsync(user) != principal.GetClaim("security_stamp"))
        return Results.Forbid(authenticationSchemes: [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
    return Results.SignIn(principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
}).RequireRateLimiting("oauth");

app.Run();

static async Task EnsureNativeClientAsync(IOpenIddictApplicationManager applications)
{
    const string desktopRedirect = "http://127.0.0.1:54782/callback";
    var existing = await applications.FindByClientIdAsync("personalflow-native");
    if (existing is not null)
    {
        var existingDescriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(existingDescriptor, existing);
        var redirect = new Uri(desktopRedirect);
        if (!existingDescriptor.RedirectUris.Contains(redirect))
        {
            existingDescriptor.RedirectUris.Add(redirect);
            await applications.UpdateAsync(existing, existingDescriptor);
        }
        return;
    }
    var descriptor = new OpenIddictApplicationDescriptor
    {
        ClientId = "personalflow-native", ClientType = ClientTypes.Public, ConsentType = ConsentTypes.Implicit, DisplayName = "PersonalFlow"
    };
    descriptor.RedirectUris.Add(new Uri("app.personalflow:/callback"));
    descriptor.RedirectUris.Add(new Uri(desktopRedirect));
    descriptor.Permissions.UnionWith([
        Permissions.Endpoints.Authorization, Permissions.Endpoints.Token, Permissions.Endpoints.Revocation,
        Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code,
        Permissions.Prefixes.Scope + Scopes.OpenId, Permissions.Prefixes.Scope + Scopes.OfflineAccess, Permissions.Prefixes.Scope + "personalflow"]);
    descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
    await applications.CreateAsync(descriptor);
}

static bool IsLocalReturnUrl(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith('/', StringComparison.Ordinal) && !value.StartsWith("//", StringComparison.Ordinal) && !value.StartsWith("/\\", StringComparison.Ordinal);

static string LoginHtml(string token, string returnUrl) => $"<html lang='ru'><meta name='viewport' content='width=device-width'/><body><h1>PersonalFlow</h1><form method='post'><input type='hidden' name='__RequestVerificationToken' value='{HtmlEncoder.Default.Encode(token)}'/><input type='hidden' name='returnUrl' value='{HtmlEncoder.Default.Encode(returnUrl)}'/><p><label>Имя <input name='name' autocomplete='username' required maxlength='256'/></label></p><p><label>Пароль <input name='password' type='password' autocomplete='current-password' required maxlength='1024'/></label></p><p><label>Код аутентификатора <input name='code' inputmode='numeric' autocomplete='one-time-code' maxlength='6'/></label></p><p><label>Или код восстановления <input name='recovery' autocomplete='off' maxlength='32'/></label></p><button>Войти</button></form></body></html>";

static X509Certificate2? LoadTokenCertificate(IConfiguration configuration, IHostEnvironment environment)
{
    if (environment.IsDevelopment()) return null;
    var path = configuration["Signing:Certificate"] ?? throw new InvalidOperationException("Configure Signing:Certificate.");
    var password = DatabaseSecrets.Get(configuration, "Signing:Password", "Signing:PasswordFile");
    var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
    if (!certificate.HasPrivateKey || certificate.NotAfter.ToUniversalTime() <= DateTime.UtcNow.AddDays(30))
        throw new InvalidOperationException("The token signing certificate is invalid or expires within 30 days.");
    return certificate;
}

public sealed class IdentityDb(DbContextOptions<IdentityDb> options) : IdentityDbContext<IdentityUser>(options);

