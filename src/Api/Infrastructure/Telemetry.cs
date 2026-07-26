using Api.Assistant;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Api.Infrastructure;

public static class Telemetry
{
    public const string ServiceName = "invoice-assistant";

    /// <summary>
    /// Traces and metrics for the whole app. The chat UI shows the trace id of each turn, so the
    /// spans below — the turn, each tool call, each outgoing HTTP request — are what someone
    /// actually looks at when asking "what did the assistant do?".
    /// </summary>
    public static IServiceCollection AddAppTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        var useConsoleExporter = configuration.GetValue("OTEL_CONSOLE_EXPORTER", environment.IsDevelopment());

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(AssistantTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (useConsoleExporter)
                {
                    tracing.AddConsoleExporter();
                }

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(AssistantTelemetry.MeterName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter();
                }
            });

        return services;
    }
}
