using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Api.Assistant;

/// <summary>
/// Loads the versioned system prompt and its hash. The hash is stored on every conversation so a
/// regression can be traced back to the exact prompt that produced it.
/// </summary>
public sealed class SystemPromptProvider
{
    private const string RelativePath = "prompts/system.md";

    public SystemPromptProvider(IOptions<AssistantOptions> options, IHostEnvironment environment)
    {
        var path = Resolve(options.Value.SystemPromptPath, environment.ContentRootPath)
            ?? throw new FileNotFoundException(
                $"Could not find '{RelativePath}'. Set Assistant:SystemPromptPath to point at it.");

        // Normalised so the hash does not change just because the file was checked out on Windows.
        Text = File.ReadAllText(path).ReplaceLineEndings("\n");
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Text)));
        Path = path;
    }

    public string Text { get; }

    /// <summary>Lowercase hex SHA-256 of the prompt text.</summary>
    public string Hash { get; }

    public string Path { get; }

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
