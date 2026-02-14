using System.Windows.Forms;

namespace EpTUN;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            MessageBox.Show(
                "This app currently supports Windows only.",
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        string configPath;
        try
        {
            configPath = ResolveConfigPath(args);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                ex.Message,
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(configPath));
    }

    private static string ResolveConfigPath(IReadOnlyList<string> args)
    {
        if (args.Count == 1)
        {
            return Path.GetFullPath(args[0]);
        }

        if (args.Count == 2 &&
            (args[0].Equals("--config", StringComparison.OrdinalIgnoreCase) ||
             args[0].Equals("-c", StringComparison.OrdinalIgnoreCase)))
        {
            return Path.GetFullPath(args[1]);
        }

        if (args.Count > 0)
        {
            throw new ArgumentException("Usage: EpTUN.exe [appsettings.json] or [--config appsettings.json]");
        }

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "appsettings.json")),
            Path.Combine(Environment.CurrentDirectory, "appsettings.json")
        };

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }
}

