namespace Api.Features.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string SigningKey { get; set; } = string.Empty;

    public string Issuer { get; set; } = "invoice-assistant";

    public string Audience { get; set; } = "invoice-assistant";

    /// <summary>
    /// 60 minutes: long enough that a token does not expire halfway through a live demo or a
    /// screen recording, which is the only reason a refresh flow would be needed here.
    /// </summary>
    public int LifetimeMinutes { get; set; } = 60;
}

/// <summary>Authorization policy names, so endpoints never spell roles out inline.</summary>
public static class Policies
{
    /// <summary>Accountant or above: reads, drafts, sending, due dates and payments.</summary>
    public const string Accountant = "accountant";

    /// <summary>Admin only: cancelling an invoice is the one irreversible business action.</summary>
    public const string Admin = "admin";
}
