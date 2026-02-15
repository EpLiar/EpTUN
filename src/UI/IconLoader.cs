using System.Drawing;
using System.Runtime.InteropServices;

namespace EpTUN;

internal static class IconLoader
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon LoadFromPngCandidates()
    {
        foreach (var path in GetCandidatePaths())
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using var bitmap = new Bitmap(path);
                var iconHandle = bitmap.GetHicon();

                try
                {
                    using var handleIcon = Icon.FromHandle(iconHandle);
                    return (Icon)handleIcon.Clone();
                }
                finally
                {
                    _ = DestroyIcon(iconHandle);
                }
            }
            catch
            {
                // ignore broken icon files and keep searching
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private static IEnumerable<string> GetCandidatePaths()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "favicon.png");
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "favicon.png"));
        yield return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "favicon.png"));
        yield return Path.Combine(Environment.CurrentDirectory, "favicon.png");
    }
}

