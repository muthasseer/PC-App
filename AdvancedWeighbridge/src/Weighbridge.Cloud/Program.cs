using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Weighbridge.Cloud.Endpoints;
using Weighbridge.Cloud.Services;
using Weighbridge.Contracts;
using Weighbridge.Data;

var builder = WebApplication.CreateBuilder(args);

var cloud = builder.Configuration.GetSection(CloudOptions.Section).Get<CloudOptions>() ?? new CloudOptions();
var problems = cloud.Validate();
if (problems.Count > 0) throw new InvalidOperationException("The AWMS Cloud configuration is invalid:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", problems));
var dataDirectory = cloud.ResolveDataDirectory(builder.Environment.ContentRootPath);
var keys = CloudKeys.Resolve(cloud, dataDirectory);

builder.Services.AddSingleton(cloud);
builder.Services.AddSingleton(keys);
builder.Services.AddSingleton(new SecretBox(keys.DataKey));
builder.Services.AddSingleton(new Database(cloud.DatabaseOptions(builder.Environment.ContentRootPath)));
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CloudAuthService>();
builder.Services.AddSingleton<SyncIngestService>();
builder.Services.AddSingleton<CloudQueryService>();
builder.Services.AddSingleton<CloudAdminService>();
builder.Services.AddSingleton<SiteMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SiteMonitor>());

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.WebHost.ConfigureKestrel(k =>
{
    k.AddServerHeader = false;
    k.Limits.MaxRequestBodySize = 16 * 1024 * 1024;
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx => ctx.HttpContext.RequestServices.GetRequiredService<CloudAuthService>().EnrichAsync(ctx),
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new ErrorDto(ctx.AuthenticateFailure is null ? "Sign in required." : "Your session has expired. Sign in again."), Api.Json);
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                var restriction = ctx.HttpContext.User.FindFirst(CloudClaims.MustChangePassword)?.Value;
                await ctx.Response.WriteAsJsonAsync(new ErrorDto(restriction switch
                {
                    CloudAuthService.RestrictionPassword => "Change your password before continuing.",
                    CloudAuthService.RestrictionMfa => "Set up two-factor authentication before continuing (required for administrators).",
                    _ => "You do not have the permission for this action.",
                }), Api.Json);
            },
        };
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, SiteKeyAuthenticationHandler>(SiteKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<CloudAuthService>((o, auth) => o.TokenValidationParameters = auth.ValidationParameters);
builder.Services.AddAuthorization(Policies.Register);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.OnRejected = async (ctx, ct) => await ctx.HttpContext.Response.WriteAsJsonAsync(new ErrorDto("Too many requests. Wait a moment and try again."), Api.Json, ct);
    o.AddPolicy(RateLimits.Login, http => RateLimitPartition.GetFixedWindowLimiter(http.Ip() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = cloud.LoginRateLimitPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(RateLimits.Public, http => RateLimitPartition.GetFixedWindowLimiter(http.Ip() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    o.AddPolicy(RateLimits.Sync, http => RateLimitPartition.GetFixedWindowLimiter(http.Request.Headers[Api.SiteKeyHeader].ToString() is { Length: > 0 } key ? SiteKeys.Hash(key) : http.Ip() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 1200, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

if (cloud.TrustForwardedHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

var startupLog = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AWMS.Cloud");
var database = app.Services.GetRequiredService<Database>();
var migration = await MigrationRunner.MigrateAsync(database, startupLog, assembly: typeof(CloudOptions).Assembly,
    folder: database.Options.Provider == DatabaseProvider.Sqlite ? "CloudSqlite" : "CloudPostgreSql");
startupLog.LogInformation("AWMS Cloud database {Provider} at schema version {Version}; keys from {Source}", database.Options.Provider, migration.ToVersion, keys.Source);
await app.Services.GetRequiredService<CloudAdminService>().BootstrapAsync(startupLog);

if (cloud.TrustForwardedHeaders) app.UseForwardedHeaders();
app.UseMiddleware<ApiErrorMiddleware>();
app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h.XContentTypeOptions = "nosniff";
    h.XFrameOptions = "DENY";
    h["Referrer-Policy"] = "no-referrer";
    h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
    h["Cross-Origin-Opener-Policy"] = "same-origin";
    h.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; connect-src 'self'; frame-src 'self' blob:; " +
                              "object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";
    if (ctx.Request.IsHttps) h.StrictTransportSecurity = "max-age=31536000";
    if (ctx.Request.Path.StartsWithSegments("/api")) h.CacheControl = "no-store";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapSyncEndpoints();
app.MapAppEndpoints();
app.MapAdminEndpoints();

app.Run();

/// <summary>Entry point (public for the integration tests).</summary>
public partial class Program;
