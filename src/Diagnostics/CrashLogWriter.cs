using System.Text;

namespace EpTUN;

internal static class CrashLogWriter
{
    private static readonly object Sync = new();

    public static string? TryWrite(string configPath, string category, Exception? exception, string? detail = null)
    {
        try
        {
            var directory = ResolveCrashLogDirectory(configPath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return null;
            }

            Directory.CreateDirectory(directory);

            lock (Sync)
            {
                var filePath = Path.Combine(directory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
                using var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                writer.WriteLine($"Timestamp: {DateTime.Now:O}");
                writer.WriteLine($"Category: {category}");
                writer.WriteLine($"ProcessId: {Environment.ProcessId}");
                writer.WriteLine($"Version: {GetVersion()}");
                writer.WriteLine($"OS: {Environment.OSVersion}");
                writer.WriteLine($"BaseDirectory: {AppContext.BaseDirectory}");
                writer.WriteLine($"ConfigPath: {configPath}");

                if (!string.IsNullOrWhiteSpace(detail))
                {
                    writer.WriteLine();
                    writer.WriteLine(detail);
                }

                if (exception is not null)
                {
                    writer.WriteLine();
                    writer.WriteLine(exception.ToString());
                }

                return filePath;
            }
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveCrashLogDirectory(string configPath)
    {
        if (!string.IsNullOrWhiteSpace(configPath))
        {
            try
            {
                var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
                if (!string.IsNullOrWhiteSpace(configDirectory))
                {
                    return Path.Combine(configDirectory, "logs");
                }
            }
            catch
            {
                // Fall through to next candidate.
            }
        }

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            return Path.Combine(AppContext.BaseDirectory, "logs");
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? string.Empty
            : Path.Combine(localAppData, "EpTUN", "logs");
    }

    private static string GetVersion()
    {
        return typeof(CrashLogWriter).Assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
