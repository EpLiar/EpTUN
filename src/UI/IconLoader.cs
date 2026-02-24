using System.Drawing;
using System.Windows.Forms;

namespace EpTUN;

internal static class IconLoader
{
    public static Icon LoadFromExecutable()
    {
        try
        {
            var executablePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executablePath))
            {
                executablePath = Application.ExecutablePath;
            }

            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                using var icon = Icon.ExtractAssociatedIcon(executablePath);
                if (icon is not null)
                {
                    return (Icon)icon.Clone();
                }
            }
        }
        catch
        {
            // Fallback to default icon when extraction is unavailable.
        }

        return (Icon)SystemIcons.Application.Clone();
    }
}
