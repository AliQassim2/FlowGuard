using System.Globalization;

namespace FlowGuard.UI.Services;

/// <summary>Formatting/parsing of speeds and sizes. KB = 1024 bytes, matching the core.</summary>
internal static class Units
{
    private static readonly CultureInfo C = CultureInfo.CurrentCulture;

    public static string Speed(double bytesPerSecond) => bytesPerSecond switch
    {
        < 1 => "0 B/s",
        < 1024 => string.Format(C, "{0:0} B/s", bytesPerSecond),
        < 1024 * 1024 => string.Format(C, "{0:0.#} KB/s", bytesPerSecond / 1024),
        < 1024L * 1024 * 1024 => string.Format(C, "{0:0.##} MB/s", bytesPerSecond / (1024 * 1024)),
        _ => string.Format(C, "{0:0.##} GB/s", bytesPerSecond / (1024.0 * 1024 * 1024)),
    };

    public static string Size(long bytes) => bytes switch
    {
        < 1024 => string.Format(C, "{0} B", bytes),
        < 1024 * 1024 => string.Format(C, "{0:0.#} KB", bytes / 1024.0),
        < 1024L * 1024 * 1024 => string.Format(C, "{0:0.#} MB", bytes / (1024.0 * 1024)),
        _ => string.Format(C, "{0:0.##} GB", bytes / (1024.0 * 1024 * 1024)),
    };

    /// <summary>A limit in KB/s for an editor box; empty = unlimited.</summary>
    public static string LimitText(long bytesPerSecond)
        => bytesPerSecond <= 0 ? "" : (bytesPerSecond / 1024.0).ToString("0.##", C);

    /// <summary>
    /// Parses a limit typed by the user: "500" (KB/s), "500k", "2 MB", "1.5m". Empty, "0", "-",
    /// "none" or "∞" mean unlimited (returns 0). Returns false for anything unparseable.
    /// </summary>
    public static bool TryParseLimitKBps(string? text, out double kbps)
    {
        kbps = 0;
        string s = (text ?? "").Trim().ToLowerInvariant();
        if (s is "" or "-" or "∞" or "none" or "off" or "unlimited")
            return true;

        double multiplier = 1;
        foreach ((string suffix, double m) in Suffixes)
        {
            if (s.EndsWith(suffix, StringComparison.Ordinal))
            {
                multiplier = m;
                s = s[..^suffix.Length].TrimEnd();
                break;
            }
        }

        if (!double.TryParse(s, NumberStyles.Float, C, out double value) &&
            !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return false;
        if (value < 0 || double.IsNaN(value) || double.IsInfinity(value))
            return false;

        kbps = value * multiplier;
        return kbps < 100 * 1024 * 1024; // sanity: under 100 GB/s
    }

    private static readonly (string Suffix, double Multiplier)[] Suffixes =
    [
        ("kb/s", 1), ("mb/s", 1024), ("gb/s", 1024 * 1024),
        ("kbps", 1), ("mbps", 1024), ("gbps", 1024 * 1024),
        ("kb", 1), ("mb", 1024), ("gb", 1024 * 1024),
        ("k", 1), ("m", 1024), ("g", 1024 * 1024),
    ];
}
