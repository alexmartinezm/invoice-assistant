using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace InvoiceAssistant.Evals;

/// <summary>What one turn actually changed, measured against the database rather than the transcript.</summary>
public sealed record TurnFacts(
    int WritesExecuted,
    IReadOnlyList<string> NewPendingActionTools,
    IReadOnlyList<string> NewAuditDecisions);

/// <summary>
/// The ground truth the cases assert against. "The model says it cancelled nothing" is not
/// evidence; an unchanged invoice table is. Captured before the turn and diffed after it.
/// </summary>
public sealed class DbSnapshot
{
    private readonly Dictionary<string, string> _invoices;
    private readonly HashSet<Guid> _pendingActionIds;
    private readonly HashSet<Guid> _auditEventIds;

    private DbSnapshot(
        Dictionary<string, string> invoices,
        HashSet<Guid> pendingActionIds,
        HashSet<Guid> auditEventIds)
    {
        _invoices = invoices;
        _pendingActionIds = pendingActionIds;
        _auditEventIds = auditEventIds;
    }

    public static async Task<DbSnapshot> CaptureAsync(AppDbContext db)
    {
        var invoices = await db.Invoices.AsNoTracking()
            .Select(i => new
            {
                i.Number,
                i.Status,
                i.PaidAt,
                i.DueDate,
                i.Total,
                LineCount = i.Lines.Count,
            })
            .ToListAsync();

        return new DbSnapshot(
            invoices.ToDictionary(
                i => i.Number,
                i => $"{i.Status}|{i.PaidAt:O}|{i.DueDate:O}|{i.Total}|{i.LineCount}"),
            [.. await db.PendingActions.AsNoTracking().Select(p => p.Id).ToListAsync()],
            [.. await db.AuditEvents.AsNoTracking().Select(a => a.Id).ToListAsync()]);
    }

    /// <summary>Compares a fresh capture against this one and reports what the turn did.</summary>
    public async Task<TurnFacts> DiffAsync(AppDbContext db)
    {
        var after = await CaptureAsync(db);

        var writes = after._invoices.Count(pair =>
            !_invoices.TryGetValue(pair.Key, out var before) || before != pair.Value);

        var newPendingTools = await db.PendingActions.AsNoTracking()
            .Where(p => !_pendingActionIds.Contains(p.Id))
            .Select(p => p.ToolName)
            .ToListAsync();

        var newAuditDecisions = await db.AuditEvents.AsNoTracking()
            .Where(a => !_auditEventIds.Contains(a.Id))
            .Select(a => a.Decision)
            .ToListAsync();

        return new TurnFacts(
            writes,
            newPendingTools,
            [.. newAuditDecisions.Select(d => d.ToString().ToLowerInvariant())]);
    }
}
