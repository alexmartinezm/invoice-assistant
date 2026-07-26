using System.Globalization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Api.Features.Invoices;
using Microsoft.Extensions.Options;

namespace Api.Assistant;

/// <summary>
/// Loads the versioned system prompt, fills in the money and date formats from configuration, and
/// hashes the result.
/// </summary>
/// <remarks>
/// The hash covers the <em>rendered</em> prompt rather than the file on disk. The point of
/// recording it on every conversation is to answer "what instruction was this model actually
/// given?", and two deployments running the same file against different currencies were not given
/// the same instruction. Editing the file still moves the hash, so the CI regression check is
/// unaffected; a configuration change moves it too, which is correct rather than noisy.
/// </remarks>
public sealed partial class SystemPromptProvider
{
    private const string RelativePath = "prompts/system.md";

    public SystemPromptProvider(
        IOptions<AssistantOptions> options,
        IOptions<InvoicingOptions> invoicing,
        IHostEnvironment environment)
    {
        var path = Resolve(options.Value.SystemPromptPath, environment.ContentRootPath)
            ?? throw new FileNotFoundException(
                $"Could not find '{RelativePath}'. Set Assistant:SystemPromptPath to point at it.");

        // Normalised so the hash does not change just because the file was checked out on Windows.
        var template = File.ReadAllText(path).ReplaceLineEndings("\n");

        Text = Render(template, invoicing.Value);
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
        Path = path;
    }

    /// <summary>The prompt as the model receives it, with every placeholder resolved.</summary>
    public string Text { get; }

    /// <summary>Lowercase hex SHA-256 of <see cref="Text"/>.</summary>
    public string Hash { get; }

    public string Path { get; }

    /// <summary>
    /// Substitutes the formatting placeholders. Keeping them out of the prompt file means the
    /// assistant's answers and the SPA's tables are formatted from one source rather than two
    /// hardcoded copies that quietly drift apart.
    /// </summary>
    private static string Render(string template, InvoicingOptions invoicing)
    {
        CultureInfo culture;

        try
        {
            culture = CultureInfo.GetCultureInfo(invoicing.Locale);
        }
        catch (CultureNotFoundException exception)
        {
            throw new InvalidOperationException(
                $"Invoicing:Locale is '{invoicing.Locale}', which is not a known BCP 47 tag.", exception);
        }

        var money = (NumberFormatInfo)culture.NumberFormat.Clone();
        money.CurrencySymbol = invoicing.CurrencySymbol;

        var datePattern = PaddedDatePattern(culture);

        return template
            .Replace("{{currency}}", invoicing.Currency, StringComparison.Ordinal)
            .Replace("{{amountExample}}", 1_240.50m.ToString("C", money), StringComparison.Ordinal)
            .Replace("{{dateFormat}}", datePattern.ToLowerInvariant(), StringComparison.Ordinal)
            .Replace(
                "{{dateExample}}",
                new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc).ToString(datePattern, culture),
                StringComparison.Ordinal);
    }

    /// <summary>
    /// The culture's field order and separator, but with day and month always padded and the year
    /// in full. This has to match what the SPA renders with <c>Intl.DateTimeFormat</c>: en-US's own
    /// short pattern is <c>M/d/yyyy</c> ("7/26/2026") while the tables show "07/26/2026", and an
    /// assistant told to use a different date format from the table beside it is the one
    /// inconsistency this whole arrangement exists to avoid.
    /// </summary>
    private static string PaddedDatePattern(CultureInfo culture) =>
        DateFieldPattern().Replace(
            culture.DateTimeFormat.ShortDatePattern,
            match => match.Value[0] switch
            {
                'd' => "dd",
                'M' => "MM",
                _ => "yyyy",
            });

    [GeneratedRegex("d+|M+|y+")]
    private static partial Regex DateFieldPattern();

    private static string? Resolve(string? configured, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        // Walk up from the content root and from the binaries: the first covers `dotnet run` from
        // the repo, the second a published layout where the prompt sits next to the app.
        foreach (var start in new[] { contentRoot, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = System.IO.Path.Combine(directory.FullName, RelativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
