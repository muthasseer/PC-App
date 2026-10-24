using System.Net.Http.Headers;
using System.Net.Http.Json;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Weighbridge.Contracts;

namespace Weighbridge.Cloud.Tests;

/// <summary>The real cloud server in memory, on a throw-away SQLite file or PostgreSQL database (WB_TEST_PG).</summary>
public sealed class CloudFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "Bridge#Cloud2026";
    public const string UserPassword = "Users#Pass2026";
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("awms-cloud-");
    private readonly string? _pgName;
    private readonly string? _pgConnection;

    private CloudFactory(string provider, string? pgName, string? pgConnection)
    {
        Provider = provider;
        _pgName = pgName;
        _pgConnection = pgConnection;
    }

    public string Provider { get; }
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);
    public string DataDirectory => _dir.FullName;

    public static string? PostgresAdmin => Environment.GetEnvironmentVariable("WB_TEST_PG");

    public static IEnumerable<object[]> Providers()
    {
        yield return ["sqlite"];
        if (!string.IsNullOrWhiteSpace(PostgresAdmin)) yield return ["postgres"];
    }

    public static async Task<CloudFactory> CreateAsync(string provider = "sqlite")
    {
        if (provider != "postgres") return new CloudFactory(provider, null, null);
        var name = "awms_cloud_test_" + Guid.NewGuid().ToString("N")[..12];
        await using (var admin = new NpgsqlConnection(PostgresAdmin))
        {
            await admin.OpenAsync();
            await admin.ExecuteAsync($"CREATE DATABASE {name}");
        }
        return new CloudFactory(provider, name, new NpgsqlConnectionStringBuilder(PostgresAdmin) { Database = name }.ToString());
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Cloud:DataDirectory", _dir.FullName);
        builder.UseSetting("Cloud:DatabaseProvider", Provider == "postgres" ? "PostgreSql" : "Sqlite");
        if (_pgConnection is not null) builder.UseSetting("Cloud:ConnectionString", _pgConnection);
        builder.UseSetting("Cloud:BootstrapAdminUsername", "admin");
        builder.UseSetting("Cloud:BootstrapAdminPassword", AdminPassword);
        builder.UseSetting("Cloud:LoginRateLimitPerMinute", "1000");
        builder.UseSetting("Cloud:TimeZone", "Asia/Colombo");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(Time));
    }

    public HttpClient Anonymous() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    public HttpClient WithToken(string token)
    {
        var c = Anonymous();
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return c;
    }

    public HttpClient WithSiteKey(string key)
    {
        var c = Anonymous();
        c.DefaultRequestHeaders.Add(Api.SiteKeyHeader, key);
        return c;
    }

    public async Task<HttpResponseMessage> PostLoginAsync(string username, string password, string? totp = null) =>
        await Anonymous().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(username, password, totp), Api.Json);

    public async Task<LoginResponse> LoginAsync(string username, string password, string? totp = null)
    {
        var response = await PostLoginAsync(username, password, totp);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<LoginResponse>(Api.Json))!;
    }

    /// <summary>Signs in as the bootstrap administrator and completes the forced password change.</summary>
    public async Task<HttpClient> AdminAsync()
    {
        var current = await PostLoginAsync("admin", AdminCurrentPassword);
        if (current.IsSuccessStatusCode) return WithToken((await current.Content.ReadFromJsonAsync<LoginResponse>(Api.Json))!.AccessToken);
        var first = await LoginAsync("admin", AdminPassword);
        var changed = await WithToken(first.AccessToken).PostAsJsonAsync("/api/v1/me/password", new ChangePasswordRequest(AdminPassword, AdminPassword + "!"), Api.Json);
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        return WithToken((await changed.Content.ReadFromJsonAsync<LoginResponse>(Api.Json))!.AccessToken);
    }

    public static string AdminCurrentPassword => AdminPassword + "!";

    public async Task<string> CreateSiteAsync(HttpClient admin, string code, string name)
    {
        var response = await admin.PostAsJsonAsync("/api/v1/admin/sites", new CreateSiteRequest(code, name), Api.Json);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CreateSiteResponse>(Api.Json))!.SiteKey;
    }

    /// <summary>Creates a user and completes the first-login password change; returns a client signed in as that user.</summary>
    public async Task<HttpClient> UserAsync(HttpClient admin, string username, string role, params string[] sites)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest(username, username.ToUpperInvariant(), UserPassword, role, sites), Api.Json);
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var login = await LoginAsync(username, UserPassword);
        Assert.True(login.User.MustChangePassword);
        var changed = await WithToken(login.AccessToken).PostAsJsonAsync("/api/v1/me/password", new ChangePasswordRequest(UserPassword, UserPassword + "x"), Api.Json);
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync());
        return WithToken((await changed.Content.ReadFromJsonAsync<LoginResponse>(Api.Json))!.AccessToken);
    }

    /// <summary>The message of an <see cref="ErrorDto"/> response body.</summary>
    public static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(); // buffered: the body can still be read afterwards
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() ?? text : text;
        }
        catch (System.Text.Json.JsonException)
        {
            return text;
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (_pgName is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(PostgresAdmin);
            await admin.OpenAsync();
            await admin.ExecuteAsync($"DROP DATABASE IF EXISTS {_pgName} WITH (FORCE)");
        }
        try { _dir.Delete(true); }
        catch (IOException) { }
    }
}
