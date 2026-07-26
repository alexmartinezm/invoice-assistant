namespace Api.Infrastructure;

/// <summary>
/// Finds a file that lives at the repository root — <c>prompts/system.md</c>, <c>policies.json</c> —
/// from an app whose working directory is somewhere under it.
/// </summary>
public static class RepositoryFile
{
    /// <summary>
    /// Returns the configured path when one is set and exists, otherwise the first match found by
    /// walking up from the content root and from the binaries. The first covers <c>dotnet run</c>
    /// from inside the repository, the second a published layout where the file sits next to the app.
    /// </summary>
    public static string? Find(string? configured, string relativePath, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        foreach (var start in new[] { contentRoot, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine(directory.FullName, relativePath);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}
