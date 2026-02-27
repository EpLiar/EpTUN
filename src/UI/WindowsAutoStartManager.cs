using System.Diagnostics;
using System.Security.Principal;

namespace EpTUN;

internal static class WindowsAutoStartManager
{
    private const string TaskNamePrefix = "EpTUN Auto Start";

    public static bool IsEnabled()
    {
        var result = RunSchtasks("/Query", "/TN", BuildTaskName(), "/XML");
        return result.ExitCode == 0;
    }

    public static void SetEnabled(string configPath, bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Windows auto start is supported on Windows only.");
        }

        if (enabled)
        {
            var executablePath = ResolveExecutablePath();
            var fullConfigPath = Path.GetFullPath(configPath);
            var taskCommandLine = BuildTaskCommandLine(executablePath, fullConfigPath);
            var result = RunSchtasks(
                "/Create",
                "/SC",
                "ONLOGON",
                "/RL",
                "HIGHEST",
                "/F",
                "/TN",
                BuildTaskName(),
                "/TR",
                taskCommandLine);
            EnsureSuccess(result, "create");
            return;
        }

        var deleteResult = RunSchtasks("/Delete", "/TN", BuildTaskName(), "/F");
        if (deleteResult.ExitCode == 0)
        {
            return;
        }

        var queryResult = RunSchtasks("/Query", "/TN", BuildTaskName());
        if (queryResult.ExitCode != 0)
        {
            return;
        }

        EnsureSuccess(deleteResult, "delete");
    }

    private static string ResolveExecutablePath()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            executablePath = Application.ExecutablePath;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Failed to resolve the EpTUN executable path for auto start.");
        }

        return Path.GetFullPath(executablePath);
    }

    private static string BuildTaskName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? identity.Name ?? Environment.UserName;
        return $"{TaskNamePrefix} ({sid})";
    }

    private static string BuildTaskCommandLine(string executablePath, string configPath)
    {
        return $"\"{executablePath}\" --config \"{configPath}\"";
    }

    private static ProcessResult RunSchtasks(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start schtasks.exe.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        return new ProcessResult(process.ExitCode, standardOutput, standardError);
    }

    private static void EnsureSuccess(ProcessResult result, string action)
    {
        if (result.ExitCode == 0)
        {
            return;
        }

        var details = string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput
            : result.StandardError;
        details = string.IsNullOrWhiteSpace(details)
            ? $"schtasks exited with code {result.ExitCode}."
            : details.Trim();

        throw new InvalidOperationException($"Failed to {action} the Windows auto-start task. {details}");
    }

    private sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);
}
