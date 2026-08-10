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
    private readonly Dictionary<FaultCheckpoint, Func<Task>> _actions = [];

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

    /// <summary>
    /// Runs an action, once, the next time this checkpoint is reached — the deterministic way to
    /// interleave a concurrent writer at an exact point rather than with a sleep. The action runs on
    /// a plain thread-pool thread (via <see cref="Task.Run(Func{Task})"/>) so its own awaits never
    /// resume on the reached thread's synchronization context, which is what would otherwise deadlock
    /// when the reaching code is blocked waiting on it. It must use its own <c>DbContext</c> scope,
    /// never the one whose pass is reaching this checkpoint.
    /// </summary>
    public void RunOnce(FaultCheckpoint checkpoint, Func<Task> action)
    {
        lock (_gate)
        {
            _actions[checkpoint] = action;
        }
    }

    public void Disarm()
    {
        lock (_gate)
        {
            _armed.Clear();
            _reached.Clear();
            _actions.Clear();
        }
    }

    public void Reach(FaultCheckpoint checkpoint)
    {
        Func<Task>? action;
        bool armed;

        lock (_gate)
        {
            _reached.Add(checkpoint);
            _actions.Remove(checkpoint, out action);
            armed = _armed.Remove(checkpoint);
        }

        // Outside the lock: the action does its own I/O and could itself reach checkpoints. Blocking
        // here is deliberate — the reaching pass must not proceed until the interleaved writer has
        // committed, which is the whole point of pinning the race to this line.
        if (action is not null)
        {
            Task.Run(action).GetAwaiter().GetResult();
        }

        if (armed)
        {
            throw new InjectedFaultException(checkpoint);
        }
    }
}
