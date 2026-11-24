using FlowGuard.Core.Processes;

namespace FlowGuard.Core.Sites;

/// <summary>
/// A website or streaming platform: a set of domain suffixes. Its traffic is recognised per
/// connection (DNS answers, TLS / QUIC server names), whichever app opened the connection.
/// </summary>
public sealed record SiteDefinition(string Key, string DisplayName, IReadOnlyList<string> Domains, bool IsVideo)
{
    /// <summary>True when <paramref name="domain"/> is one of the site's domains or a subdomain of one.</summary>
    public bool Matches(string domain)
    {
        foreach (string d in Domains)
        {
            if (domain.Length == d.Length
                    ? domain.Equals(d, StringComparison.OrdinalIgnoreCase)
                    : domain.Length > d.Length && domain[domain.Length - d.Length - 1] == '.' &&
                      domain.EndsWith(d, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}

/// <summary>
/// Keys used for rules, shaping groups and settings: executables ("steam.exe") and sites
/// ("site:youtube"). ':' can't appear in a file name, so the two never collide.
/// </summary>
public static class TargetKey
{
    public const string SitePrefix = "site:";

    public static bool IsSite(string key) => key.StartsWith(SitePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>"Steam" → "steam.exe", "SITE:YouTube" → "site:youtube", "www.Example.com/" → "site:example.com".</summary>
    public static string Normalize(string key)
    {
        string k = key.Trim();
        if (IsSite(k))
            return SitePrefix + NormalizeDomain(k[SitePrefix.Length..]);
        return ProcessResolver.NormalizeExeName(k);
    }

    public static string ForSite(string siteKeyOrDomain) => SitePrefix + NormalizeDomain(siteKeyOrDomain);

    /// <summary>Lower-cases and strips scheme, path, port and a leading "www.".</summary>
    public static string NormalizeDomain(string text)
    {
        string d = text.Trim().ToLowerInvariant();
        int scheme = d.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) d = d[(scheme + 3)..];
        int slash = d.IndexOfAny(['/', '?', '#']);
        if (slash >= 0) d = d[..slash];
        int colon = d.LastIndexOf(':');
        if (colon > 0 && d.IndexOf(':') == colon) d = d[..colon];   // host:port (not IPv6)
        d = d.Trim('.');
        if (d.StartsWith("www.", StringComparison.Ordinal)) d = d[4..];
        return d;
    }

    /// <summary>A plausible domain: letters, digits, '-' and '.', with at least one dot.</summary>
    public static bool IsValidDomain(string domain)
        => domain.Length is > 2 and < 254 && domain.Contains('.') && !domain.StartsWith('.') &&
           domain.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.');
}

/// <summary>Built-in platforms plus helpers for custom (single-domain) sites.</summary>
public static class SiteCatalog
{
    public static IReadOnlyList<SiteDefinition> BuiltIn { get; } =
    [
        new("site:youtube", "YouTube", ["youtube.com", "googlevideo.com", "ytimg.com", "youtu.be", "youtube-nocookie.com", "youtubei.googleapis.com"], true),
        new("site:netflix", "Netflix", ["netflix.com", "netflix.net", "nflxvideo.net", "nflximg.net", "nflxext.com", "nflxso.net"], true),
        new("site:twitch", "Twitch", ["twitch.tv", "ttvnw.net", "jtvnw.net", "twitchcdn.net", "twitchsvc.net"], true),
        new("site:kick", "Kick", ["kick.com"], true),
        new("site:shahid", "Shahid", ["shahid.net", "shahid.mbc.net", "mbc.net"], true),
        new("site:primevideo", "Prime Video", ["primevideo.com", "amazonvideo.com", "aiv-cdn.net", "aiv-delivery.net", "pv-cdn.net"], true),
        new("site:disneyplus", "Disney+", ["disneyplus.com", "disney-plus.net", "dssott.com", "bamgrid.com"], true),
        new("site:tiktok", "TikTok", ["tiktok.com", "tiktokcdn.com", "tiktokv.com", "tiktokcdn-us.com", "byteoversea.com", "ibytedtos.com"], true),
    ];

    /// <summary>The built-in definition for <paramref name="key"/>, or a single-domain custom site.</summary>
    public static SiteDefinition? Resolve(string key)
    {
        string k = TargetKey.Normalize(key);
        if (!TargetKey.IsSite(k))
            return null;
        foreach (SiteDefinition s in BuiltIn)
            if (s.Key.Equals(k, StringComparison.OrdinalIgnoreCase))
                return s;
        string domain = k[TargetKey.SitePrefix.Length..];
        return TargetKey.IsValidDomain(domain) ? new SiteDefinition(k, domain, [domain], IsVideo: false) : null;
    }

    /// <summary>Friendly name for any key: "YouTube", "example.com" or the exe name.</summary>
    public static string DisplayName(string key)
        => TargetKey.IsSite(key) ? Resolve(key)?.DisplayName ?? key[TargetKey.SitePrefix.Length..] : key;
}
