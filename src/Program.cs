using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace EpTUN;

internal static class Program
{
    private const string ActivationRequestMessage = "SHOW_WINDOW";
    private const int SwShow = 5;
    private const int SwRestore = 9;
    private const uint AsfwAny = 0xFFFFFFFF;
    private static int _fatalExceptionHandled;

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

        var instanceToken = BuildInstanceToken();
        var singleInstanceMutexName = $@"Local\EpTUN.{instanceToken}.SingleInstance";
        var activationPipeName = $"EpTUN.{instanceToken}.ActivationPipe";

        using var singleInstanceMutex = new Mutex(initiallyOwned: true, singleInstanceMutexName, out var isPrimaryInstance);
        if (!isPrimaryInstance)
        {
            TryAllowAnyProcessSetForegroundWindow();
            _ = TrySignalPrimaryInstance(activationPipeName);
            TryActivateExistingWindow();
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        RegisterGlobalExceptionHandlers(configPath);

        using var activationListenerCts = new CancellationTokenSource();
        var mainForm = new MainForm(configPath);
        _ = RunActivationListenerAsync(
            activationPipeName,
            () => mainForm.RequestShowWindowFromExternalInstance(),
            activationListenerCts.Token);

        Application.Run(mainForm);

        activationListenerCts.Cancel();
    }

    private static void RegisterGlobalExceptionHandlers(string configPath)
    {
        Application.ThreadException += (_, e) =>
        {
            var crashLogPath = CrashLogWriter.TryWrite(configPath, "Application.ThreadException", e.Exception);
            ShowFatalErrorAndExit(configPath, crashLogPath);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var crashLogPath = CrashLogWriter.TryWrite(
                configPath,
                "AppDomain.UnhandledException",
                e.ExceptionObject as Exception,
                $"IsTerminating: {e.IsTerminating}");
            ShowFatalErrorAndExit(configPath, crashLogPath);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _ = CrashLogWriter.TryWrite(configPath, "TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private static void ShowFatalErrorAndExit(string configPath, string? crashLogPath)
    {
        if (Interlocked.Exchange(ref _fatalExceptionHandled, 1) != 0)
        {
            return;
        }

        var i18n = new Localizer(UiLanguageResolver.ResolveFromConfigPath(configPath));
        var message = string.IsNullOrWhiteSpace(crashLogPath)
            ? i18n.Text(
                "EpTUN encountered a fatal error and must exit.",
                "EpTUN 遇到致命错误并即将退出。")
            : i18n.Text(
                $"EpTUN encountered a fatal error and must exit.\nCrash log: {crashLogPath}",
                $"EpTUN 遇到致命错误并即将退出。\n崩溃日志：{crashLogPath}");

        try
        {
            MessageBox.Show(
                message,
                "EpTUN",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
        catch
        {
            // Ignore UI failures while shutting down after a fatal exception.
        }

        Environment.Exit(1);
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

    private static async Task RunActivationListenerAsync(
        string activationPipeName,
        Action onActivationRequested,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    activationPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(cancellationToken);

                using var reader = new StreamReader(server);
                var message = await reader.ReadLineAsync(cancellationToken);
                if (string.Equals(message, ActivationRequestMessage, StringComparison.Ordinal))
                {
                    onActivationRequested();
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // Ignore transient pipe failures and keep listening.
            }
        }
    }

    private static bool TrySignalPrimaryInstance(string activationPipeName)
    {
        const int connectTimeoutMs = 150;
        const int retryDelayMs = 80;
        const int maxAttempts = 5;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", activationPipeName, PipeDirection.Out);
                client.Connect(connectTimeoutMs);
                using var writer = new StreamWriter(client) { AutoFlush = true };
                writer.WriteLine(ActivationRequestMessage);
                return true;
            }
            catch (TimeoutException)
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (IOException)
            {
                Thread.Sleep(retryDelayMs);
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    private static string BuildInstanceToken()
    {
        var userNameBytes = Encoding.UTF8.GetBytes(Environment.UserName);
        var hash = SHA256.HashData(userNameBytes);
        return Convert.ToHexString(hash.AsSpan(0, 8));
    }

    private static void TryAllowAnyProcessSetForegroundWindow()
    {
        try
        {
            _ = AllowSetForegroundWindow(AsfwAny);
        }
        catch
        {
            // Ignore foreground permission failures.
        }
    }

    private static void TryActivateExistingWindow()
    {
        try
        {
            var windowHandle = FindWindow(lpClassName: null, lpWindowName: "EpTUN");
            if (windowHandle == IntPtr.Zero)
            {
                return;
            }

            if (IsIconic(windowHandle))
            {
                _ = ShowWindowAsync(windowHandle, SwRestore);
            }
            else
            {
                _ = ShowWindowAsync(windowHandle, SwShow);
            }

            _ = BringWindowToTop(windowHandle);
            _ = SetForegroundWindow(windowHandle);
        }
        catch
        {
            // Ignore activation fallback failures.
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);

    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(IntPtr windowHandle, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);
}

