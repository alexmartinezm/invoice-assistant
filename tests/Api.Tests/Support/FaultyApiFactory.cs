using Api.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Api.Tests.Support;

/// <summary>
/// The application with the fault-injection seam armed.
/// </summary>
/// <remarks>
/// A separate factory rather than a flag on <see cref="ApiFactory"/>, so no other test can trip a
/// checkpoint by accident and so the shipped registration — <see cref="NoFaults"/> — is what every
/// other suite runs against.
/// </remarks>
public sealed class FaultyApiFactory : ApiFactory
{
    public ScriptedFaults Faults { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IFaultInjector>(Faults)));
    }
}

/// <summary>
/// Throws at named checkpoints, once each, on demand.
/// </summary>
/// <remarks>
/// Once each because the interesting scenario is always "it broke, and then the client retried" —
/// a checkpoint that kept throwing would test an outage rather than a recovery. Deterministic
/// rather than timed: a race proved with a sleep is a race proved on a quiet machine.
/// </remarks>
public sealed class ScriptedFaults : IFaultInjector
{
    private readonly Lock _gate = new();
    private readonly HashSet<FaultCheckpoint> _armed = [];
    private readonly List<FaultCheckpoint> _reached = [];

    /// <summary>Every checkpoint the code has walked through, in order. Useful when a test is lying.</summary>
    public IReadOnlyList<FaultCheckpoint> Reached
    {
        get
        {
            lock (_gate)
            {
                return [.. _reached];
            }
        }
    }

    public void ThrowOnce(FaultCheckpoint checkpoint)
    {
        lock (_gate)
        {
            _armed.Add(checkpoint);
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _armed.Clear();
            _reached.Clear();
        }
    }

    public void Reach(FaultCheckpoint checkpoint)
    {
        lock (_gate)
        {
            _reached.Add(checkpoint);

            if (!_armed.Remove(checkpoint))
            {
                return;
            }
        }

        throw new InjectedFaultException(checkpoint);
    }
}
