namespace FlowGuard.Core.Qos;

public enum MemberClass : byte
{
    /// <summary>Game mode game: never slowed, reservation always kept free while it runs.</summary>
    Game,

    /// <summary>Movie mode platform: never slowed, reservation kept waiting while video plays.</summary>
    Video,

    /// <summary>No priority number: left alone, except to protect a game or a video.</summary>
    Unmanaged,

    /// <summary>Has a priority number 1..128.</summary>
    Ranked,
}

/// <summary>One app or site in one direction.</summary>
public readonly record struct AllocationMember(
    string Key,
    MemberClass Class,
    byte Rank,
    double Rate,
    bool Active,
    double Reserve = 0);

public sealed record AllocatorSettings(
    double LineUtilization,
    double Headroom,
    double MinBytesPerSecond,
    double GameSafetyMargin);

/// <summary>
/// Strict priority over a line of known capacity, recomputed every sample:
/// <list type="number">
/// <item>Games, then video, take what they use plus their reservation, and are never capped.</item>
/// <item>Apps without a number come next and are only capped to protect a game or video.</item>
/// <item>Numbered apps follow, 1 first. While a more important group is busy, each group shares
///   what is left of the line (minus headroom for the busier ones to grow into); apps with
///   the same number share their part fairly.</item>
/// </list>
/// Works on rates the shaper actually let through, so a starved important app grows a little
/// every sample thanks to the headroom until it has what it needs.
/// </summary>
public static class BandwidthAllocator
{
    /// <returns>Cap per key in bytes/s for capped members only (members not listed run free).</returns>
    public static Dictionary<string, long> Allocate(double capacity, IReadOnlyList<AllocationMember> members,
        AllocatorSettings settings, bool gameRunning)
    {
        var caps = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (capacity <= 0 || members.Count == 0)
            return caps;

        double budget = gameRunning
            ? capacity * (1 - settings.GameSafetyMargin)
            : capacity * settings.LineUtilization;
        double headroom = capacity * settings.Headroom;

        double taken = 0;
        bool trigger = false;       // something more important needs protecting
        bool rankedAbove = false;   // ...and it's a numbered app that may still be speeding up

        foreach (IGrouping<int, AllocationMember> level in members.GroupBy(Level).OrderBy(g => g.Key))
        {
            MemberClass cls = level.First().Class;
            if (cls is MemberClass.Game or MemberClass.Video)
            {
                foreach (AllocationMember m in level)
                {
                    taken += Math.Max(m.Rate, m.Reserve);
                    trigger |= m.Reserve > 0 || m.Active;
                }
                continue;
            }

            if (trigger)
                Share(level.ToList(), budget - taken - (rankedAbove ? headroom : 0), settings.MinBytesPerSecond, caps);

            foreach (AllocationMember m in level)
                taken += m.Rate;
            if (cls == MemberClass.Ranked && level.Any(m => m.Active))
                trigger = rankedAbove = true;
        }
        return caps;
    }

    private static int Level(AllocationMember m) => m.Class switch
    {
        MemberClass.Game => 0,
        MemberClass.Video => 1,
        MemberClass.Unmanaged => 2,
        _ => 2 + m.Rank,
    };

    /// <summary>
    /// Max-min fair split of <paramref name="budget"/>: each member gets what it is using plus
    /// room to grow, small users first; whatever is left goes to the busy ones.
    /// </summary>
    private static void Share(List<AllocationMember> level, double budget, double min, Dictionary<string, long> caps)
    {
        budget = Math.Max(budget, 0);
        var demand = level.Select(m => Math.Max(min, m.Rate * 1.25 + 16 * 1024)).ToArray();
        var alloc = new double[level.Count];
        double remaining = budget;
        int left = level.Count;
        foreach (int i in Enumerable.Range(0, level.Count).OrderBy(i => demand[i]))
        {
            double a = Math.Min(demand[i], remaining / left);
            alloc[i] = a;
            remaining -= a;
            left--;
        }

        if (remaining > 0)
        {
            int[] busy = Enumerable.Range(0, level.Count).Where(i => level[i].Active).ToArray();
            if (busy.Length == 0)
                busy = Enumerable.Range(0, level.Count).ToArray();
            foreach (int i in busy)
                alloc[i] += remaining / busy.Length;
        }

        for (int i = 0; i < level.Count; i++)
            caps[level[i].Key] = (long)Math.Max(Math.Max(1024, min), alloc[i]);
    }
}
