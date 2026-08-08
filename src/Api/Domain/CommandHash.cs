using System.Security.Cryptography;
using System.Text;

namespace Api.Domain;

/// <summary>
/// The fingerprint of an authorized command: the tool, the exact frozen arguments, and the revision
/// of the thing it was authorized against.
/// </summary>
/// <remarks>
/// <para>
/// It hashes the stored <c>ArgsJson</c> <em>bytes</em> rather than a canonicalized form, and that is
/// deliberate: there is exactly one producer of those bytes — the server's configured
/// <c>System.Text.Json</c> serializer — so canonicalization would add a dependency to solve a
/// problem this codebase does not have. The moment a second producer appears, adopt a documented
/// canonical scheme and migrate the stored hashes explicitly. Do not quietly change the algorithm:
/// every hash already in the database would stop matching, and a mismatch here is read as "somebody
/// tampered with the approval".
/// </para>
/// <para>
/// The revision is part of the hash, not a separate check, because "mark 2026-0041 as paid" is not
/// one command — it is a different command against an invoice somebody edited in the meantime.
/// </para>
/// </remarks>
public static class CommandHash
{
    /// <summary>What stands in for the revision when the command has no existing target to bind to.</summary>
    public const string NoResource = "none";

    public static string Of(string toolName, string argsJson, long? expectedResourceRevision)
    {
        var payload = new StringBuilder()
            .Append(toolName).Append('\n')
            .Append(argsJson).Append('\n')
            .Append(expectedResourceRevision?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? NoResource)
            .ToString();

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}
