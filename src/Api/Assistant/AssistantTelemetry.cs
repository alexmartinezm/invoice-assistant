using System.Diagnostics;

namespace Api.Assistant;

public static class AssistantTelemetry
{
    public const string SourceName = "InvoiceAssistant.Assistant";

    public static readonly ActivitySource Source = new(SourceName);
}
