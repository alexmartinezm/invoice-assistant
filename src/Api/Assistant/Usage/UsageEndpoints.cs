using System.Security.Claims;
using Api.Domain;
using Api.Features.Auth;
using Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Api.Assistant.Usage;

public sealed record UsageSummary(
    DateOnly? From,
    DateOnly? To,
    int Conversations,
    int ModelCalls,
    long PromptTokens,
    long CompletionTokens,
    decimal CostEur,
    decimal SpentTodayEur,
    decimal DailyBudgetEur,
    bool BudgetExhausted);

public sealed record ConversationUsageRow(
    Guid ConversationId,
    DateTimeOffset StartedAt,
    string UserEmail,
    string Models,
    int ModelCalls,
    long PromptTokens,
    long CompletionTokens,
    int ToolCalls,
    decimal CostEur);

public sealed record ModelCallRow(
    DateTimeOffset At,
    string Model,
    int PromptTokens,
    int CompletionTokens,
    int ToolCalls,
    int LatencyMs,
    decimal CostEur);

public sealed record ToolEventRow(DateTimeOffset At, string Tool, string Decision);

public sealed record ConversationUsageDetail(
    Guid ConversationId,
    DateTimeOffset StartedAt,
    string UserEmail,
    string SystemPromptHash,
    int ModelCalls,
    long PromptTokens,
    long CompletionTokens,
    int ToolCalls,
    decimal CostEur,
    IReadOnlyList<ModelCallRow> Calls,
    IReadOnlyList<ToolEventRow> ToolEvents);

/// <summary>
/// What the assistant costs, in euros, per conversation and per day — the numbers behind the Usage
/// page. Everyone sees their own conversations; an Admin sees everyone's. The budget figures are
/// global on purpose: the kill switch guards one shared wallet, so showing a per-user slice of it
/// would misstate how close the demo is to pausing.
/// </summary>
public static class UsageEndpoints
{
    /// <summary>
    /// How many conversations the list returns, newest first. There is no paging and no total, so
    /// past this many the page silently shows a window rather than the whole ledger — acceptable
    /// for a demo, and the first thing to fix if this ever ran somewhere busy.
    /// </summary>
    public const int MaxConversations = 100;

    public static IEndpointRouteBuilder MapUsageEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/api/usage")
            .WithTags("Usage")
            .RequireAuthorization();

        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/conversations", ConversationsAsync);
        group.MapGet("/conversations/{id:guid}", ConversationAsync);

        return routes;
    }

    private static async Task<IResult> SummaryAsync(
        DateOnly? from,
        DateOnly? to,
        ClaimsPrincipal principal,
        AppDbContext db,
        UsageBudget budget,
        CancellationToken cancellationToken)
    {
        var records = Visible(db, principal);
        records = FilterByDate(records, from, to);

        var totals = await records
            .GroupBy(_ => 1)
            .Select(g => new
            {
                // Null-conversation rows would otherwise count as one conversation between them.
                Conversations = g.Where(r => r.ConversationId != null)
                    .Select(r => r.ConversationId).Distinct().Count(),
                ModelCalls = g.Count(),
                PromptTokens = g.Sum(r => (long)r.PromptTokens),
                CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                CostEur = g.Sum(r => r.CostEur),
            })
            .SingleOrDefaultAsync(cancellationToken);

        var status = await budget.GetStatusAsync(cancellationToken);

        return Results.Ok(new UsageSummary(
            from,
            to,
            totals?.Conversations ?? 0,
            totals?.ModelCalls ?? 0,
            totals?.PromptTokens ?? 0,
            totals?.CompletionTokens ?? 0,
            totals?.CostEur ?? 0m,
            status.SpentTodayEur,
            status.DailyBudgetEur,
            status.Exhausted));
    }

    private static async Task<IResult> ConversationsAsync(
        DateOnly? from,
        DateOnly? to,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var records = FilterByDate(Visible(db, principal), from, to)
            .Where(r => r.ConversationId != null);

        // The totals come from the usage rows; when the conversation *started* and whose it is come
        // from the conversation itself, joined here rather than aggregated off the rows. The first
        // usage row is written after the first model call answers, so a minimum over the rows is a
        // second or two later than the conversation — and the detail endpoint, which reads the
        // conversation directly, would then disagree with this list about the same conversation.
        var rows = await records
            .GroupBy(r => r.ConversationId!.Value)
            .Select(g => new
            {
                ConversationId = g.Key,
                ModelCalls = g.Count(),
                PromptTokens = g.Sum(r => (long)r.PromptTokens),
                CompletionTokens = g.Sum(r => (long)r.CompletionTokens),
                ToolCalls = g.Sum(r => r.ToolCallCount),
                CostEur = g.Sum(r => r.CostEur),
            })
            .Join(
                db.Conversations.AsNoTracking(),
                totals => totals.ConversationId,
                conversation => conversation.Id,
                (totals, conversation) => new { totals, conversation.CreatedAt, conversation.UserId })
            .Join(
                db.Users.AsNoTracking(),
                row => row.UserId,
                user => user.Id,
                (row, user) => new { row.totals, row.CreatedAt, user.Email })
            .OrderByDescending(row => row.CreatedAt)
            .Take(MaxConversations)
            .ToListAsync(cancellationToken);

        var conversationIds = rows.Select(row => row.totals.ConversationId).ToList();

        // The model names are gathered separately and joined in memory: a string aggregate does not
        // translate, and there are at most MaxConversations rows here by construction.
        var models = await db.UsageRecords.AsNoTracking()
            .Where(r => r.ConversationId != null && conversationIds.Contains(r.ConversationId.Value))
            .Select(r => new { ConversationId = r.ConversationId!.Value, r.Model })
            .Distinct()
            .ToListAsync(cancellationToken);

        return Results.Ok(rows
            .Select(row => new ConversationUsageRow(
                row.totals.ConversationId,
                row.CreatedAt,
                row.Email,
                string.Join(", ", models
                    .Where(m => m.ConversationId == row.totals.ConversationId)
                    .Select(m => m.Model)
                    .Order()),
                row.totals.ModelCalls,
                row.totals.PromptTokens,
                row.totals.CompletionTokens,
                row.totals.ToolCalls,
                row.totals.CostEur))
            .ToList());
    }

    private static async Task<IResult> ConversationAsync(
        Guid id,
        ClaimsPrincipal principal,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var conversation = await db.Conversations.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == id, cancellationToken);

        // The same 404 whether the conversation does not exist or belongs to someone else: a
        // Viewer should not be able to probe which conversation ids are real.
        if (conversation is null
            || (principal.Role() != Role.Admin && conversation.UserId != principal.Id()))
        {
            return Results.NotFound();
        }

        var calls = await db.UsageRecords.AsNoTracking()
            .Where(r => r.ConversationId == id)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new ModelCallRow(
                r.CreatedAt, r.Model, r.PromptTokens, r.CompletionTokens,
                r.ToolCallCount, r.LatencyMs, r.CostEur))
            .ToListAsync(cancellationToken);

        var toolEvents = await db.AuditEvents.AsNoTracking()
            .Where(a => a.ConversationId == id && a.Action == "tool_call")
            .OrderBy(a => a.Timestamp)
            .Select(a => new { a.Timestamp, a.ToolName, a.Decision })
            .ToListAsync(cancellationToken);

        var owner = await db.Users.AsNoTracking()
            .Where(u => u.Id == conversation.UserId)
            .Select(u => u.Email)
            .SingleOrDefaultAsync(cancellationToken);

        return Results.Ok(new ConversationUsageDetail(
            conversation.Id,
            conversation.CreatedAt,
            owner ?? string.Empty,
            conversation.SystemPromptHash,
            calls.Count,
            calls.Sum(c => (long)c.PromptTokens),
            calls.Sum(c => (long)c.CompletionTokens),
            calls.Sum(c => c.ToolCalls),
            calls.Sum(c => c.CostEur),
            calls,
            toolEvents
                .Select(e => new ToolEventRow(
                    e.Timestamp,
                    e.ToolName ?? string.Empty,
                    e.Decision.ToString().ToLowerInvariant()))
                .ToList()));
    }

    private static IQueryable<UsageRecord> Visible(AppDbContext db, ClaimsPrincipal principal)
    {
        var records = db.UsageRecords.AsNoTracking();

        return principal.Role() == Role.Admin
            ? records
            : records.Where(r => r.UserId == principal.Id());
    }

    private static IQueryable<UsageRecord> FilterByDate(IQueryable<UsageRecord> records, DateOnly? from, DateOnly? to)
    {
        // Calendar dates, interpreted as whole UTC days, both ends inclusive.
        if (from is { } start)
        {
            var startAt = new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            records = records.Where(r => r.CreatedAt >= startAt);
        }

        if (to is { } end)
        {
            var endAt = new DateTimeOffset(end.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            records = records.Where(r => r.CreatedAt < endAt);
        }

        return records;
    }
}
