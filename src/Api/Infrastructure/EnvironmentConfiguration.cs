namespace Api.Infrastructure;

/// <summary>
/// Bridges the flat variable names in <c>.env.example</c> to the hierarchical configuration keys
/// the app binds. Keeping both in step is what makes "clone, paste a key, run" hold: the same
/// names work with <c>dotnet run</c>, with Docker Compose and with a VPS panel.
/// </summary>
public static class EnvironmentConfiguration
{
    private static readonly (string Variable, string ConfigurationKey)[] Aliases =
    [
        ("AI_PROVIDER", "AI:Provider"),
        ("AZURE_OPENAI_ENDPOINT", "AI:AzureEndpoint"),
        ("AZURE_OPENAI_API_KEY", "AI:AzureApiKey"),
        ("AZURE_OPENAI_CHAT_DEPLOYMENT", "AI:AzureChatDeployment"),
        ("OPENAI_BASE_URL", "AI:OpenAiBaseUrl"),
        ("OPENAI_API_KEY", "AI:OpenAiApiKey"),
        ("CHAT_MODEL", "AI:ChatModel"),
        ("JWT_SIGNING_KEY", "Jwt:SigningKey"),
        ("ASSISTANT_API_BASE_URL", "Assistant:ApiBaseUrl"),
        ("INVOICING_CURRENCY", "Invoicing:Currency"),
        ("INVOICING_CURRENCY_SYMBOL", "Invoicing:CurrencySymbol"),
        ("INVOICING_LOCALE", "Invoicing:Locale"),
        ("INVOICING_TAX_LABEL", "Invoicing:TaxLabel"),
        ("INVOICING_DEFAULT_VAT_RATE", "Invoicing:DefaultVatRate"),
        ("INVOICING_REDUCED_VAT_RATE", "Invoicing:ReducedVatRate"),
        ("INVOICING_PAYMENT_TERM_DAYS", "Invoicing:DefaultPaymentTermDays"),
        ("INVOICING_ACCOUNTANT_MARK_PAID_LIMIT", "Invoicing:AccountantMarkPaidLimit"),
    ];

    /// <summary>
    /// Loads a <c>.env</c> file from the repository root when present. Docker Compose and CI pass
    /// real environment variables, so this only helps local <c>dotnet run</c>.
    /// </summary>
    public static IConfigurationBuilder AddDotEnvFile(this IConfigurationBuilder builder, string contentRootPath)
    {
        var path = FindUpwards(contentRootPath, ".env");
        if (path is null)
        {
            return builder;
        }

        Dictionary<string, string?> values = [];

        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var name = trimmed[..separator].Trim();
            var value = ReadValue(trimmed[(separator + 1)..]);

            // A real environment variable always wins over the file.
            if (Environment.GetEnvironmentVariable(name) is null)
            {
                values[name] = value;
            }
        }

        return builder.AddInMemoryCollection(values);
    }

    /// <summary>
    /// Reads one <c>.env</c> value: quoted values are taken verbatim, unquoted ones end at a
    /// trailing <c>#</c> comment. Without that, <c>AI_PROVIDER=azure-openai  # or openai-compatible</c>
    /// silently configures a provider named "azure-openai  # or openai-compatible".
    /// </summary>
    private static string ReadValue(string raw)
    {
        var value = raw.Trim();

        if (value.StartsWith('"') && value.Length > 1 && value.EndsWith('"'))
        {
            return value[1..^1];
        }

        // Only whitespace-preceded '#' starts a comment, so a '#' inside a password survives.
        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        return comment < 0 ? value : value[..comment].TrimEnd();
    }

    /// <summary>Maps the flat variable names onto the configuration keys the options bind to.</summary>
    public static IConfigurationBuilder AddFlatVariableAliases(this IConfigurationBuilder builder)
    {
        var configuration = builder.Build();
        Dictionary<string, string?> mapped = [];

        foreach (var (variable, key) in Aliases)
        {
            if (configuration[variable] is { Length: > 0 } value)
            {
                mapped[key] = value;
            }
        }

        if (ResolveConnectionString(configuration) is { } connectionString)
        {
            mapped["ConnectionStrings:Postgres"] = connectionString;
        }

        return mapped.Count == 0 ? builder : builder.AddInMemoryCollection(mapped);
    }

    /// <summary>
    /// Accepts either an Npgsql keyword string or the <c>postgres://user:pass@host:port/db</c> URL
    /// that hosting panels hand out.
    /// </summary>
    private static string? ResolveConnectionString(IConfiguration configuration)
    {
        var raw = configuration["POSTGRES_CONNECTION_STRING"] ?? configuration["DATABASE_URL"];

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return raw;
        }

        var uri = new Uri(raw);
        var credentials = uri.UserInfo.Split(':', 2);

        return $"Host={uri.Host};Port={(uri.Port > 0 ? uri.Port : 5432)};"
            + $"Database={uri.AbsolutePath.TrimStart('/')};"
            + $"Username={Uri.UnescapeDataString(credentials[0])};"
            + $"Password={Uri.UnescapeDataString(credentials.ElementAtOrDefault(1) ?? string.Empty)}";
    }

    private static string? FindUpwards(string startPath, string fileName)
    {
        for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
