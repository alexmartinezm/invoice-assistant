using System.ClientModel;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Api.Assistant;
using Api.Assistant.Tools;
using Api.Infrastructure;
using Azure.AI.OpenAI;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenAI;

namespace InvoiceAssistant.Evals;

/// <summary>
/// Boots the real application against a throwaway PostgreSQL database — the same arrangement as
/// the integration tests — but with a real, recorded model instead of a scripted one. Everything
/// the cases assert on (gate decisions, audit events, DB writes) is produced by the code that
/// ships, driven by a model that was actually free to misbehave.
/// </summary>
public class EvalHost : WebApplicationFactory<Program>
{
    /// <summary>The repository's real <c>policies.json</c>: the evals gate on what ships.</summary>
    public static readonly PolicyDocument Policy = PolicyDocument.Load(
        Path.Combine(EvalEnvironment.Current.RepositoryRoot, "policies.json"));

    private readonly string _databaseName = $"invoice_assistant_evals_{Guid.NewGuid():N}";
    private readonly string _adminConnectionString;
    private readonly string _testConnectionString;

    public EvalHost()
        : this(providerClient: null)
    {
    }

    /// <summary>
    /// <paramref name="providerClient"/> lets the harness's own tests run it against a scripted
    /// model: the machinery gets exercised on every plain <c>dotnet test</c>, credentials or not.
    /// </summary>
    public EvalHost(IChatClient? providerClient)
    {
        _adminConnectionString = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION_STRING")
            ?? "Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=postgres";

        _testConnectionString = new NpgsqlConnectionStringBuilder(_adminConnectionString)
        {
            Database = _databaseName,
        }.ConnectionString;

        ExecuteOnAdminDatabase($"CREATE DATABASE \"{_databaseName}\"");

        Recorder = new RecordingChatClient(providerClient ?? BuildProviderClient(EvalEnvironment.Current));
    }

    public RecordingChatClient Recorder { get; }

    /// <summary>The app's warning-and-worse logs, so a dead turn can say why it died.</summary>
    public ServerLogCapture ServerLogs { get; } = new();

    /// <summary>An <see cref="HttpClient"/> already carrying the given demo user's bearer token.</summary>
    public async Task<HttpClient> ClientForAsync(string email)
    {
        var client = CreateClient();

        // A turn against a real model takes as long as the model takes.
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenForAsync(email));
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = _testConnectionString,
                ["Jwt:SigningKey"] = "evals-signing-key-long-enough-for-hmac-sha256-use",
                // The provider client is replaced below; these only keep startup quiet.
                ["AI:Provider"] = "azure-openai",
                ["AI:AzureEndpoint"] = string.Empty,
                // The per-user rate limit protects a public demo's wallet, not the suite; at the
                // suite's pace it would only add flakes.
                ["Assistant:TurnsPerMinute"] = "1000",
                // The run's brake is the harness's own token budget; the app's euro kill switch
                // pausing the suite halfway through would only make a red build harder to read.
                ["Usage:DailyBudgetEur"] = "1000",
                ["OTEL_CONSOLE_EXPORTER"] = "false",
            }));

        builder.ConfigureLogging(logging => logging.AddProvider(ServerLogs));

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IChatClient>();

            // The app's own pipeline, so the per-turn iteration cap under eval is the one
            // policies.json ships and every metered call lands in usage_records like production.
            services.AddAssistantChatPipeline(_ => Recorder, Policy, detailedToolErrors: false);

            services.Replace(ServiceDescriptor.Singleton(
                new AssistantAvailability(IsConfigured: true, Reason: string.Empty)));

            // The tools' HTTP hop stays real (ADR 002): out over HttpClient, back in through the
            // test server's pipeline. Only the socket is swapped.
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

    private static IChatClient BuildProviderClient(EvalEnvironment environment) =>
        environment.Provider is "azure-openai"
            ? new AzureOpenAIClient(
                    new Uri(environment.AzureEndpoint!),
                    new ApiKeyCredential(environment.AzureApiKey!))
                .GetChatClient(environment.Model)
                .AsIChatClient()
            : new OpenAIClient(
                    new ApiKeyCredential(environment.OpenAiApiKey!),
                    new OpenAIClientOptions
                    {
                        Endpoint = environment.OpenAiBaseUrl is { } baseUrl
                            ? new Uri(baseUrl, UriKind.Absolute)
                            : null,
                    })
                .GetChatClient(environment.Model)
                .AsIChatClient();

    private async Task<string> TokenForAsync(string email)
    {
        using var client = CreateClient();
        var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { email, password = DatabaseSeeder.DemoPassword });
        response.EnsureSuccessStatusCode();

        var login = await response.Content.ReadFromJsonAsync<LoginPayload>();
        return login!.AccessToken;
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
