using Api.Domain;

namespace Api.Tests;

/// <summary>
/// The execution state machine, with no host and no database.
/// </summary>
/// <remarks>
/// The rule worth protecting here is the one about <see cref="ActionExecutionStatus.Unknown"/>: it
/// is reachable, it is not terminal, and nothing may collapse it into <c>Failed</c>. "Nothing
/// happened" is the most dangerous thing this system could say about a write it cannot account for.
/// </remarks>
public class ActionExecutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_execution_starts_pending_and_keys_every_attempt_with_its_own_id()
    {
        var execution = Execution();

        Assert.Equal(ActionExecutionStatus.Pending, execution.Status);
        Assert.Equal(execution.Id.ToString(), execution.IdempotencyKey);
        Assert.Equal(0, execution.AttemptCount);
        Assert.False(execution.IsSettled);
    }

    [Fact]
    public void Starting_counts_the_attempt_and_keeps_the_first_start_time()
    {
        var execution = Execution();

        execution.Start(Now);
        execution.Succeed(200, """{"status":"ok"}""", Now.AddSeconds(1));

        Assert.Equal(ActionExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(1, execution.AttemptCount);
        Assert.Equal(Now, execution.StartedAt);
        Assert.Equal(Now.AddSeconds(1), execution.CompletedAt);
        Assert.True(execution.IsSettled);
    }

    /// <summary>
    /// A lost response schedules a look, not a resend. Retrying a write nobody can prove did not
    /// happen is the failure this entity exists to prevent.
    /// </summary>
    [Fact]
    public void A_lost_response_becomes_unknown_and_schedules_reconciliation()
    {
        var execution = Execution();
        execution.Start(Now);

        execution.MarkUnknown("transport_lost", "The connection closed after the request went out.", Now.AddSeconds(30));

        Assert.Equal(ActionExecutionStatus.Unknown, execution.Status);
        Assert.Equal(Now.AddSeconds(30), execution.NextAttemptAt);
        Assert.Null(execution.CompletedAt);
        Assert.False(execution.IsSettled);
    }

    [Fact]
    public void An_unknown_execution_can_be_reconciled_to_succeeded_without_a_second_attempt()
    {
        var execution = Execution();
        execution.Start(Now);
        execution.MarkUnknown("transport_lost", detail: null, Now.AddSeconds(30));

        execution.Succeed(200, """{"status":"ok"}""", Now.AddMinutes(1));

        Assert.Equal(ActionExecutionStatus.Succeeded, execution.Status);
        Assert.Equal(1, execution.AttemptCount);
        Assert.Null(execution.NextAttemptAt);
    }

    [Fact]
    public void A_settled_execution_cannot_move_again()
    {
        var execution = Execution();
        execution.Start(Now);
        execution.Fail(409, "invalid_transition", "Only a sent invoice can be marked as paid.", Now);

        Assert.Throws<InvalidOperationException>(() => execution.Start(Now));
        Assert.Throws<InvalidOperationException>(() => execution.Succeed(200, null, Now));
    }

    /// <summary>
    /// A queued delivery is not a finished write. The execution stays <c>Executing</c> so nothing
    /// downstream can read "the invoice is Sent" as "the customer received it".
    /// </summary>
    [Fact]
    public void A_queued_delivery_keeps_the_execution_running()
    {
        var execution = Execution();
        var deliveryId = Guid.CreateVersion7();
        execution.Start(Now);

        execution.AwaitDelivery(deliveryId, 202, """{"status":"Sent"}""", Now);

        Assert.Equal(ActionExecutionStatus.Executing, execution.Status);
        Assert.Equal(deliveryId, execution.DeliveryId);
        Assert.False(execution.IsSettled);
    }

    [Fact]
    public void A_failure_detail_is_bounded_rather_than_stored_whole()
    {
        var execution = Execution();
        execution.Start(Now);

        execution.Fail(500, "api_error", new string('x', ActionExecution.MaxErrorDetail + 200), Now);

        Assert.Equal(ActionExecution.MaxErrorDetail, execution.ErrorDetail!.Length);
    }

    /// <summary>
    /// The hash binds the tool, the exact stored bytes and the revision. Changing any of the three
    /// is a different command, which is the whole point of asking a person about the first one.
    /// </summary>
    [Fact]
    public void The_command_hash_covers_the_tool_the_arguments_and_the_revision()
    {
        var baseline = CommandHash.Of("mark_invoice_paid", """{"number":"2026-0041"}""", 3);

        Assert.Equal(64, baseline.Length);
        Assert.Equal(baseline, CommandHash.Of("mark_invoice_paid", """{"number":"2026-0041"}""", 3));
        Assert.NotEqual(baseline, CommandHash.Of("cancel_invoice", """{"number":"2026-0041"}""", 3));
        Assert.NotEqual(baseline, CommandHash.Of("mark_invoice_paid", """{"number":"2026-0042"}""", 3));
        Assert.NotEqual(baseline, CommandHash.Of("mark_invoice_paid", """{"number":"2026-0041"}""", 4));
        Assert.NotEqual(baseline, CommandHash.Of("mark_invoice_paid", """{"number":"2026-0041"}""", null));
    }

    private static ActionExecution Execution() => new()
    {
        UserId = Guid.CreateVersion7(),
        ToolName = "mark_invoice_paid",
        Decision = ExecutionDecision.Confirmed,
        CommandHash = CommandHash.Of("mark_invoice_paid", """{"number":"2026-0041"}""", 1),
        CreatedAt = Now,
    };
}
