using System.Net.Http.Headers;
using System.Net.Http.Json;
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
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private const string DemoPassword = DatabaseSeeder.DemoPassword;

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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _testConnectionString,
                ["Jwt:SigningKey"] = "integration-tests-signing-key-long-enough-for-hmac-sha256",
                // No provider configured: the scripted client below stands in for one.
                ["AI:Provider"] = "azure-openai",
                ["AI:AzureEndpoint"] = string.Empty,
                ["OTEL_CONSOLE_EXPORTER"] = "false",
            }));

        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton(Clock));

            services.RemoveAll<IChatClient>();
            services.AddChatClient(Model).UseFunctionInvocation();
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
