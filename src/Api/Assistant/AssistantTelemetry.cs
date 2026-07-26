using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Api.Assistant;

public static class AssistantTelemetry
{
    public const string SourceName = "InvoiceAssistant.Assistant";

    public const string MeterName = "InvoiceAssistant.Assistant";

    public static readonly ActivitySource Source = new(SourceName);

    public static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> ModelCalls = Meter.CreateCounter<long>(
        "assistant.model_calls", description: "Calls made to the model");

    public static readonly Counter<long> Tokens = Meter.CreateCounter<long>(
        "assistant.tokens", description: "Tokens consumed, tagged by direction");

    public static readonly Counter<double> CostEur = Meter.CreateCounter<double>(
        "assistant.cost", unit: "EUR", description: "Model spend as priced by configuration");

    public static readonly Counter<long> BudgetRejections = Meter.CreateCounter<long>(
        "assistant.budget_rejections", description: "Calls or turns refused because the daily budget is spent");

    public static readonly Counter<long> UnpricedModelCalls = Meter.CreateCounter<long>(
        "assistant.unpriced_model_calls", description: "Calls recorded at zero cost because the model has no configured price");
}
