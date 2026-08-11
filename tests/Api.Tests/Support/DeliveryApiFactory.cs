using Api.Infrastructure;
using Api.Infrastructure.Delivery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Api.Tests.Support;

/// <summary>
/// The application with the outbox driven by hand rather than by its own timer.
/// </summary>
/// <remarks>
/// <para>
/// The background worker is removed and its passes are invoked explicitly, which is the difference
/// between a deterministic test and a test that waits a second and hopes. "Sleep, then assert the
/// email went out" fails on a loaded CI machine and passes on a fast one, which is the worst
/// possible property for the suite that is supposed to prove this feature works.
/// </para>
/// <para>
/// The provider is the real <see cref="DemoInvoiceDeliveryProvider"/>, kept so the tests can count
/// its receipts — "exactly one message was delivered" is the assertion the whole slice is for.
/// </para>
/// </remarks>
public sealed class DeliveryApiFactory : ApiFactory
{
    public ScriptedFaults Faults { get; } = new();

    /// <summary>
    /// The singleton the application itself uses. Resolved on first touch rather than in the
    /// constructor: reaching for it earlier would build the container during configuration, which
    /// is how a factory ends up with two of everything.
    /// </summary>
    public DemoInvoiceDeliveryProvider Provider =>
        (DemoInvoiceDeliveryProvider)Services.GetRequiredService<IInvoiceDeliveryProvider>();

    /// <summary>Dispatch and reconcile, as the worker would do them in one tick.</summary>
    public async Task RunOutboxAsync()
    {
        await RunDispatchAsync();
        await RunReconcileAsync();
    }

    public async Task RunDispatchAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<OutboxDispatcher>().DispatchBatchAsync(CancellationToken.None);
    }

    public async Task RunReconcileAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<DeliveryReconciler>().ReconcileAsync(CancellationToken.None);
    }

    /// <summary>The pass that finishes executions whose caller never came back.</summary>
    public async Task<int> RunExecutionReconcileAsync()
    {
        using var scope = Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ExecutionReconciler>().ReconcileAsync(CancellationToken.None);
    }

    /// <summary>
    /// Runs the block against a provider with different capabilities, and restores them after.
    /// </summary>
    /// <remarks>
    /// The capabilities decide what the outbox is allowed to do after an ambiguous answer, so a test
    /// that cannot vary them can only ever exercise the branch the demo provider happens to be on —
    /// which is how "resend after ambiguity" stayed unconditional for as long as it did.
    /// </remarks>
    public IDisposable ProviderThat(bool honoursStableKey, bool supportsReceiptLookup)
    {
        var original = Provider.Capabilities;
        Provider.Capabilities = new ProviderCapabilities(honoursStableKey, supportsReceiptLookup);

        return new Restore(() => Provider.Capabilities = original);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IFaultInjector>(Faults));

            // The worker's loop, not its work: RunOnceAsync is exercised through the dispatcher and
            // the reconciler directly, on the test's schedule.
            services.RemoveAll<IHostedService>();
        });
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
