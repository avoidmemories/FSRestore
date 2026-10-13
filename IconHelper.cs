using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImageSource = System.Windows.Media.ImageSource;

namespace FSRestore;

public static class IconHelper
{
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? GetIcon(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return null;
        }

        if (Cache.TryGetValue(processPath, out var cached))
        {
            return cached;
        }

        try
        {
            var large = new IntPtr[1];
            var small = new IntPtr[1];
            var count = ExtractIconEx(processPath, 0, large, small, 1);
            var icon = large[0] != IntPtr.Zero ? large[0] : small[0];
            if (count > 0 && icon != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(20, 20));
                src.Freeze();
                DestroyIcon(large[0]);
                DestroyIcon(small[0]);
                Cache[processPath] = src;
                return src;
            }
        }
        catch
        {
        }

        Cache[processPath] = null;
        return null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[] phiconLarge, IntPtr[] phiconSmall, uint nIcons);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
