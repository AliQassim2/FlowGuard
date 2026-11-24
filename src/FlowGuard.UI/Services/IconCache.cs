using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace FlowGuard.UI.Services;

/// <summary>Executable icons, extracted once per path and frozen (UI thread only).</summary>
internal static class IconCache
{
    private static readonly Dictionary<string, ImageSource?> s_cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? Get(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (s_cache.TryGetValue(path, out ImageSource? cached))
            return cached;

        ImageSource? image = null;
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is not null)
            {
                BitmapSource bitmap = Imaging.CreateBitmapSourceFromHIcon(
                    icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                image = bitmap;
            }
        }
        catch
        {
            // Inaccessible or vanished executable: fall back to the generic glyph.
        }

        s_cache[path] = image;
        return image;
    }
}
