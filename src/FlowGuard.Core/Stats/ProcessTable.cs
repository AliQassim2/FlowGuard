using System.Collections.Concurrent;
using System.Diagnostics;
using FlowGuard.Core.Processes;

namespace FlowGuard.Core.Stats;

/// <summary>PID → <see cref="ProcessTrafficState"/> registry shared by the shaper and the sampler.</summary>
public sealed class ProcessTable
{
    private readonly ConcurrentDictionary<int, ProcessTrafficState> _states = new();
    private readonly Func<int, ProcessIdentity> _resolver;

    public ProcessTable(Func<int, ProcessIdentity>? resolver = null)
    {
        _resolver = resolver ?? ProcessResolver.Resolve;
    }

    public int Count => _states.Count;

    /// <summary>
    /// Hot path. Lock-free hit; on first sight of a PID the executable name is resolved once
    /// (one OpenProcess + QueryFullProcessImageName, a few microseconds).
    /// </summary>
    public ProcessTrafficState GetOrAdd(int pid)
    {
        if (_states.TryGetValue(pid, out ProcessTrafficState? state))
            return state;

        var created = new ProcessTrafficState(_resolver(pid))
        {
            LastActiveTimestamp = Stopwatch.GetTimestamp(),
        };
        return _states.GetOrAdd(pid, created);
    }

    /// <summary>
    /// Adds the counters of a tracked site. Sites get negative IDs so they never clash with a
    /// PID, and are never evicted for being idle.
    /// </summary>
    internal ProcessTrafficState AddSite(string siteKey)
    {
        int id = Interlocked.Decrement(ref _nextSiteId);
        var state = new ProcessTrafficState(new ProcessIdentity(id, siteKey, null, 0))
        {
            LastActiveTimestamp = Stopwatch.GetTimestamp(),
        };
        _states[id] = state;
        return state;
    }

    private int _nextSiteId;

    public bool TryGet(int pid, out ProcessTrafficState? state) => _states.TryGetValue(pid, out state);

    /// <summary>Allocating snapshot of all states — sampler/UI use only.</summary>
    public ProcessTrafficState[] ToArray() => _states.Values.ToArray();

    internal bool Remove(ProcessTrafficState state)
        => _states.TryRemove(new KeyValuePair<int, ProcessTrafficState>(state.Pid, state));
}
