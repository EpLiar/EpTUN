using System.Text;

namespace EpTUN;

internal sealed class FileLogSink : IDisposable
{
    private readonly object _sync = new();
    private readonly StreamWriter _writer;
    private bool _disposed;

    private FileLogSink(string filePath, StreamWriter writer)
    {
        FilePath = filePath;
        _writer = writer;
    }

    public string FilePath { get; }

    public static FileLogSink Create(string configPath)
    {
        var candidateDirectories = BuildCandidateDirectories(configPath);
        foreach (var directory in candidateDirectories)
        {
            try
            {
                Directory.CreateDirectory(directory);
                var filePath = Path.Combine(directory, $"eptun-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
                {
                    AutoFlush = true
                };

                return new FileLogSink(filePath, writer);
            }
            catch
            {
                // Try next candidate directory.
            }
        }

        throw new InvalidOperationException("Failed to create local log file.");
    }

    public void WriteLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
                // Keep UI logging alive even if file logging fails.
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _writer.Dispose();
        }
    }

    private static IEnumerable<string> BuildCandidateDirectories(string configPath)
    {
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(configPath))
        {
            try
            {
                var configDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath));
                if (!string.IsNullOrWhiteSpace(configDirectory))
                {
                    candidates.Add(Path.Combine(configDirectory, "logs"));
                }
            }
            catch
            {
                // Ignore invalid config path.
            }
        }

        if (!string.IsNullOrWhiteSpace(AppContext.BaseDirectory))
        {
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "logs"));
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            candidates.Add(Path.Combine(localAppData, "EpTUN", "logs"));
        }

        return candidates
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
