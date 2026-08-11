using System.Net.Http.Headers;
using System.Net.Http.Json;
using Api.Assistant;
using Api.Assistant.Tools;
using Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Api.Tests.Support;

/// <summary>
/// Boots the real application against a throwaway PostgreSQL database, with the clock pinned and
/// the model scripted. Everything else — auth, EF, endpoints, the tools' HTTP hop — is the code
/// that ships.
/// </summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    private const string DemoPassword = DatabaseSeeder.DemoPassword;

    /// <summary>The repository's real <c>policies.json</c> — the tests gate on what ships.</summary>
    public static readonly PolicyDocument Policy = PolicyDocument.Load(
        RepositoryFile.Find(configured: null, "policies.json", AppContext.BaseDirectory)
        ?? throw new FileNotFoundException("Could not find policies.json from the test binaries."));

    private readonly string _databaseName = $"invoice_assistant_test_{Guid.NewGuid():N}";
    private readonly string _adminConnectionString;
    private readonly string _testConnectionString;

    public ApiFactory()
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

        _testConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
        }.ConnectionString;

        ExecuteOnAdminDatabase($"CREATE DATABASE \"{_databaseName}\"");
    }

    public ScriptedChatClient Model { get; } = new();

    /// <summary>
    /// This test's own database, for the handful of assertions that need a second connection —
    /// holding a row lock the application then has to time out on, for one. Going through
    /// <see cref="AppDbContext"/> for that would be holding the lock with the same connection the
    /// request needs.
    /// </summary>
    public string ConnectionString => _testConnectionString;

    public IClock Clock { get; } = new FrozenClock();

    public LogCapture Logs { get; } = new();

    /// <summary>An <see cref="HttpClient"/> already carrying the given demo user's bearer token.</summary>
    public async Task<HttpClient> ClientForAsync(string email)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenForAsync(email));
        return client;
    }

    public async Task<string> TokenForAsync(string email)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = DemoPassword });
        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginPayload>();
        return login!.AccessToken;
    }

    /// <summary>
    /// Settings a derived factory wants on top of the defaults — used to boot the app under a
    /// different market so the configurability is proved rather than assumed.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, string?> ExtraConfiguration =>
        new Dictionary<string, string?>();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = _testConnectionString,
            ["Jwt:SigningKey"] = "integration-tests-signing-key-long-enough-for-hmac-sha256",
            // No provider configured: the scripted client below stands in for one.
            ["AI:Provider"] = "azure-openai",
            ["AI:AzureEndpoint"] = string.Empty,
            ["OTEL_CONSOLE_EXPORTER"] = "false",
            // A price for the scripted model, in round numbers, so usage assertions can say
            // exactly what a call costs: 120 in + 45 out = 0.0021€ per scripted round trip.
            ["Usage:Prices:0:Model"] = ScriptedChatClient.ScriptedModelId,
            ["Usage:Prices:0:InputPer1MEur"] = "10",
            ["Usage:Prices:0:OutputPer1MEur"] = "20",
        };

        foreach (var (key, value) in ExtraConfiguration)
        {
            settings[key] = value;
        }

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings));

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(Clock));

            services.RemoveAll<IChatClient>();

            // The app's own pipeline around the scripted model. Without it the iteration cap and
            // the usage collector would be missing, and both the per-turn limit tests and the
            // usage tests would pass for the wrong reason.
            services.AddAssistantChatPipeline(_ => Model, Policy, detailedToolErrors: true);

            services.Replace(ServiceDescriptor.Singleton(
                new Api.Assistant.AssistantAvailability(IsConfigured: true, Reason: string.Empty)));

            // The tools' HTTP hop is the point of ADR 002, so it stays real: the request goes out
            // over HttpClient and comes back in through the test server's pipeline, headers and all.
            // Only the socket is swapped — the identity-forwarding handler is the one from the app.
            services.AddHttpClient<SelfApiClient>(client => client.BaseAddress = new Uri("http://localhost"))
                .ConfigurePrimaryHttpMessageHandler(() => Server.CreateHandler());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing)
        {
            NpgsqlConnection.ClearAllPools();
            ExecuteOnAdminDatabase($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
        }
    }

    private void ExecuteOnAdminDatabase(string sql)
    {
        using var connection = new NpgsqlConnection(_adminConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }

    private sealed record LoginPayload(string AccessToken);
}
