using System.Diagnostics;

namespace TapoCtrl.Services;

public sealed record PythonDependencyStatus(bool PythonAvailable, bool PackagesAvailable, string PythonPath, string Detail)
{
    public bool Ready => PythonAvailable && PackagesAvailable;
}

public static class PythonDependencyService
{
    public static async Task<PythonDependencyStatus> CheckAsync(string? configuredPython, CancellationToken cancellationToken = default)
    {
        var python = string.IsNullOrWhiteSpace(configuredPython) ? "python" : configuredPython.Trim();
        try
        {
            var result = await RunAsync(python, "-c \"import sys, kasa, tapo; print(sys.executable)\"", cancellationToken);
            if (result.ExitCode == 0)
                return new(true, true, python, result.StdOut.Trim());

            var pythonOnly = await RunAsync(python, "--version", cancellationToken);
            if (pythonOnly.ExitCode != 0)
                return new(false, false, python, string.IsNullOrWhiteSpace(pythonOnly.StdErr) ? pythonOnly.StdOut : pythonOnly.StdErr);

            return new(true, false, python, string.IsNullOrWhiteSpace(result.StdErr) ? result.StdOut : result.StdErr);
        }
        catch (Exception ex)
        {
            return new(false, false, python, ex.Message);
        }
    }

    public static async Task<int> InstallAsync(string? configuredPython, CancellationToken cancellationToken = default)
    {
        var python = string.IsNullOrWhiteSpace(configuredPython) ? "python" : configuredPython.Trim();
        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = "-m pip install --user --upgrade python-kasa tapo",
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Pythonのインストーラーを開始できませんでした。");
        await process.WaitForExitAsync(cancellationToken);
        return process.ExitCode;
    }

    private static async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException($"{fileName} を起動できませんでした。");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
