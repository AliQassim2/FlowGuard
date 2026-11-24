using System.Diagnostics;
using FlowGuard.Core.Engine;
using FlowGuard.Core.Packets;
using FlowGuard.Core.Processes;
using FlowGuard.Core.Sites;
using FlowGuard.Core.Stats;

namespace FlowGuard.Core.Shaping;

/// <summary>Traffic of one tracked site: its own counters (a synthetic process entry) and shaping group.</summary>
public sealed class SiteRoute
{
    internal SiteRoute(SiteDefinition site, ProcessTrafficState state, ShapingGroup group)
    {
        Site = site;
        State = state;
        Group = group;
    }

    public SiteDefinition Site { get; }
    public ProcessTrafficState State { get; }
    public ShapingGroup Group { get; }
}

/// <summary>
/// The engine's <see cref="IPacketHandler"/>: attributes every packet to its process (or to a
/// tracked site, see <see cref="SiteClassifier"/>), counts it, and — when that executable or
/// site has an active cap — runs it through its group's token bucket / delay queue. A background
/// scheduler thread re-injects queued packets as tokens refill.
/// <para>
/// Never shaped: local network traffic (it doesn't use the internet line), DNS, and anything
/// without ports such as ICMP, so pings measure the line rather than FlowGuard.
/// </para>
/// </summary>
public sealed class TrafficShaper : IPacketHandler, IDisposable
{
    private readonly ProcessTable _table;
    private readonly ShapingOptions _options;
    private readonly object _configLock = new();

    // Copy-on-write: the hot path reads these without locks.
    private volatile Dictionary<string, ShapingGroup> _groupMap = new(StringComparer.OrdinalIgnoreCase);
    private volatile ShapingGroup[] _groupArray = [];
    private int _configVersion;

    private volatile Dictionary<string, SiteRoute> _routes = new(StringComparer.OrdinalIgnoreCase);
    private long _internetDown;
    private long _internetUp;

    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _scheduler;
    private volatile bool _disposed;
    private volatile IPacketInjector? _injector;

    public TrafficShaper(ProcessTable table, ShapingOptions? options = null)
    {
        _table = table ?? throw new ArgumentNullException(nameof(table));
        _options = options ?? new ShapingOptions();
        _scheduler = new Thread(SchedulerLoop)
        {
            IsBackground = true,
            Name = "FlowGuard.ShaperScheduler",
            Priority = ThreadPriority.AboveNormal,
        };
        _scheduler.Start();
    }

    /// <summary>Where released packets are sent. Set by the composition root (the engine).</summary>
    public IPacketInjector? Injector
    {
        get => _injector;
        set { _injector = value; _wake.Set(); }
    }

    public IReadOnlyList<ShapingGroup> Groups => _groupArray;

    /// <summary>Recognises which site each connection belongs to.</summary>
    public SiteClassifier Sites { get; } = new();

    /// <summary>Tracked sites, keyed "site:…".</summary>
    public IReadOnlyCollection<SiteRoute> SiteRoutes => _routes.Values;

    /// <summary>Bytes that crossed the internet line (non-local remote address), received / sent.</summary>
    public long InternetDownBytes => Interlocked.Read(ref _internetDown);
    public long InternetUpBytes => Interlocked.Read(ref _internetUp);

    /// <summary>True while any group has a finite cap or still holds queued packets.</summary>
    public bool RequiresDivert
    {
        get
        {
            foreach (ShapingGroup g in _groupArray)
                if (g.IsActive) return true;
            return false;
        }
    }

    public long QueuedBytes
    {
        get
        {
            long total = 0;
            foreach (ShapingGroup g in _groupArray)
                total += g.Down.QueuedBytes + g.Up.QueuedBytes;
            return total;
        }
    }

    // ---------------------------------------------------------------- configuration API

    /// <summary>Sets (or with <see cref="RateLimit.Unlimited"/>, clears) the hard cap for an executable.</summary>
    public void SetHardLimit(string exeName, RateLimit limit)
        => Configure(exeName, g => g.HardLimit = limit);

    public void ClearHardLimit(string exeName) => SetHardLimit(exeName, RateLimit.Unlimited);

    /// <summary>Dynamic cap layered on top of the hard limit (used by QoS in Phase 3).</summary>
    public void SetDynamicCap(string exeName, RateLimit cap)
        => Configure(exeName, g => g.DynamicCap = cap);

    public IReadOnlyDictionary<string, RateLimit> GetHardLimits()
        => _groupArray.Where(g => !g.HardLimit.IsUnlimited)
                      .ToDictionary(g => g.ExeName, g => g.HardLimit, StringComparer.OrdinalIgnoreCase);

    public ShapingGroup? FindGroup(string key)
        => _groupMap.TryGetValue(TargetKey.Normalize(key), out ShapingGroup? g) ? g : null;

    /// <summary>
    /// Sets which sites get their own counters and shaping group. Their traffic is then counted
    /// and limited separately from the app that carries it (typically a browser).
    /// </summary>
    public void SetTrackedSites(IEnumerable<SiteDefinition> sites)
    {
        List<SiteDefinition> list = sites.DistinctBy(s => s.Key, StringComparer.OrdinalIgnoreCase).ToList();
        lock (_configLock)
        {
            var routes = new Dictionary<string, SiteRoute>(StringComparer.OrdinalIgnoreCase);
            foreach (SiteDefinition site in list)
            {
                if (_routes.TryGetValue(site.Key, out SiteRoute? existing) && existing.Site == site)
                {
                    routes[site.Key] = existing;
                    continue;
                }
                Configure(site.Key, _ => { });
                ProcessTrafficState state = existing?.State ?? _table.AddSite(site.Key);
                routes[site.Key] = new SiteRoute(site, state, _groupMap[site.Key]);
            }
            foreach ((string key, SiteRoute old) in _routes)
                if (!routes.ContainsKey(key))
                    _table.Remove(old.State);
            _routes = routes;
            Sites.Configure(list);
        }
    }

    private void Configure(string target, Action<ShapingGroup> mutate)
    {
        string key = TargetKey.Normalize(target);
        long now = Stopwatch.GetTimestamp();

        lock (_configLock)
        {
            if (!_groupMap.TryGetValue(key, out ShapingGroup? group))
            {
                group = new ShapingGroup(key, _options, now);
                var map = new Dictionary<string, ShapingGroup>(_groupMap, StringComparer.OrdinalIgnoreCase) { [key] = group };
                _groupArray = [.. _groupArray, group];
                _groupMap = map;
                Interlocked.Increment(ref _configVersion); // existing PIDs re-bind to the new group
            }

            mutate(group);
            group.Apply(now);
        }
        _wake.Set(); // a raised/removed cap may release queued packets immediately
    }

    // ---------------------------------------------------------------- hot path

    public PacketVerdict OnPacket(in PacketContext context)
    {
        ProcessTrafficState state = _table.GetOrAdd(context.Pid);
        PacketHeader header = context.Header;
        bool internet = header.RemoteAddress(context.Direction).IsInternet;
        if (internet)
        {
            if (context.Direction == PacketDirection.Inbound)
                Interlocked.Add(ref _internetDown, context.Length);
            else
                Interlocked.Add(ref _internetUp, context.Length);
        }
        bool shapeable = internet && !IsControl(in header);

        // Tracked sites: count (and shape) the connection under the site instead of the app.
        if (Sites.IsEnabled && Sites.Classify(context) is { } site &&
            _routes.TryGetValue(site.Key, out SiteRoute? route))
            return Admit(in context, route.State, route.Group, shapeable);

        // Re-bind to the exe's group whenever the configuration changed (cheap int compare).
        int version = Volatile.Read(ref _configVersion);
        if (state.ConfigVersion != version)
        {
            state.Group = state.Pid == ProcessResolver.UnattributedPid
                ? null
                : _groupMap.GetValueOrDefault(state.Name);
            state.ConfigVersion = version;
        }

        return Admit(in context, state, state.Group, shapeable);
    }

    /// <summary>DNS and port-less packets (ICMP) are tiny and latency-critical: never delay them.</summary>
    private static bool IsControl(in PacketHeader h)
        => !h.HasPorts || (h.Protocol == TransportProtocol.Udp && (h.SourcePort == 53 || h.DestinationPort == 53));

    private PacketVerdict Admit(in PacketContext context, ProcessTrafficState state, ShapingGroup? group, bool shapeable)
    {
        if (group is null || !shapeable || !context.CanModify)
        {
            state.AddDelivered(context.Direction, context.Length);
            return PacketVerdict.Pass;
        }

        PacketVerdict verdict = group.Lane(context.Direction)
            .Admit(context.Packet, context.Meta, state, Stopwatch.GetTimestamp(), out bool wasIdle);

        switch (verdict)
        {
            case PacketVerdict.Pass:
                state.AddDelivered(context.Direction, context.Length);
                break;
            case PacketVerdict.Drop:
                state.AddDropped(context.Length);
                break;
            case PacketVerdict.Queued when wasIdle:
                _wake.Set();
                break;
        }
        return verdict;
    }

    // ---------------------------------------------------------------- scheduler

    private void SchedulerLoop()
    {
        long frequency = Stopwatch.Frequency;
        while (!_disposed)
        {
            long now = Stopwatch.GetTimestamp();
            long minWait = long.MaxValue;
            IPacketInjector? injector = _injector;

            foreach (ShapingGroup g in _groupArray)
            {
                long w = g.Down.Release(now, injector);
                if (w >= 0 && w < minWait) minWait = w;
                w = g.Up.Release(now, injector);
                if (w >= 0 && w < minWait) minWait = w;
            }

            int timeoutMs = minWait == long.MaxValue
                ? Timeout.Infinite
                : (int)Math.Clamp((minWait * 1000 + frequency - 1) / frequency, 1, 100);
            _wake.WaitOne(timeoutMs);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _wake.Set();
        _scheduler.Join(TimeSpan.FromSeconds(2));
        foreach (ShapingGroup g in _groupArray)
        {
            g.Down.Clear();
            g.Up.Clear();
        }
        _wake.Dispose();
    }
}
