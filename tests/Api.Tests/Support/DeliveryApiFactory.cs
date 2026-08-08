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

    public DemoInvoiceDeliveryProvider Provider { get; private set; } = null!;

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

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);

        // Resolved once the host exists. Reaching for it earlier would build the container during
        // configuration, which is how a factory ends up with two of everything.
        Provider = (DemoInvoiceDeliveryProvider)Services.GetRequiredService<IInvoiceDeliveryProvider>();
    }
}
