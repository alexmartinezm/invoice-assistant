namespace Api.Infrastructure;

/// <summary>
/// The places where this system can lose track of what happened.
/// </summary>
/// <remarks>
/// Each one is a boundary a transaction cannot span, named so a test can stop the process exactly
/// there. They are the reason the durability claims in ADR 009 are claims about tested behaviour
/// rather than about intent — "the write commits and the response is lost" is not something you can
/// demonstrate by waiting and hoping.
/// </remarks>
public enum FaultCheckpoint
{
    /// <summary>The approval is durable and no request has gone out yet.</summary>
    AfterApprovalClaimCommitted,

    /// <summary>The handler has changed local state; nothing is committed.</summary>
    BeforeBusinessTransactionCommit,

    /// <summary>The effect and its receipt are committed; the caller has not been told.</summary>
    AfterBusinessTransactionCommitBeforeResponse,

    /// <summary>An outbox row is leased and the provider has not been called.</summary>
    AfterOutboxClaimBeforeProviderCall,

    /// <summary>The provider has accepted and stored a receipt; the answer is about to be lost.</summary>
    AfterProviderAcceptedBeforeReceiptReturned,

    /// <summary>The provider's answer is in hand; the execution has not been updated.</summary>
    AfterProviderReceiptBeforeExecutionFinalized,
}

/// <summary>
/// A seam that does nothing in production and throws on demand in a test.
/// </summary>
/// <remarks>
/// Deliberately not reachable from configuration in a deployed environment, not a chat tool, and
/// not an endpoint. The registered implementation is <see cref="NoFaults"/>; a test replaces the
/// singleton. Proving a race with a sleep proves nothing except that the machine was busy.
/// </remarks>
public interface IFaultInjector
{
    void Reach(FaultCheckpoint checkpoint);
}

/// <summary>The production implementation, and the only one this assembly registers.</summary>
public sealed class NoFaults : IFaultInjector
{
    public void Reach(FaultCheckpoint checkpoint)
    {
    }
}

/// <summary>Thrown at a checkpoint to stand in for the process dying there.</summary>
public sealed class InjectedFaultException(FaultCheckpoint checkpoint)
    : Exception($"Injected fault at {checkpoint}.")
{
    public FaultCheckpoint Checkpoint { get; } = checkpoint;
}
