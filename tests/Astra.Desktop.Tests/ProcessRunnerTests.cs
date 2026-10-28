using Astra.Desktop.Core;

namespace Astra.Desktop.Tests;

public class ProcessRunnerTests
{
    [Fact]
    public void Windows_command_line_keeps_the_quoted_paths_as_written()
    {
        var line = ProcessRunner.WindowsCommandLine(
            @"C:\Users\A B\AppData\Local\Programs\Astra\resources\server\astra-server.exe",
            ["serve", "--started-by", "desktop", "--port", "17321"],
            @"C:\Users\A B\.astra\logs\2026-10-29.log");
        // No \" escapes: cmd.exe reads quotes literally, and /s strips only the outer pair.
        Assert.Equal(
            "/d /s /c \"\"C:\\Users\\A B\\AppData\\Local\\Programs\\Astra\\resources\\server\\astra-server.exe\" serve --started-by desktop --port 17321 >> \"C:\\Users\\A B\\.astra\\logs\\2026-10-29.log\" 2>&1\"",
            line);
    }

    [Fact]
    public async Task Detached_start_runs_the_binary_and_appends_its_output_to_the_log()
    {
        var dir = Directory.CreateTempSubdirectory("astra proc ");
        try
        {
            var log = Path.Combine(dir.FullName, "server log.txt");
            File.WriteAllText(log, "before\n");
            var (binary, args) = OperatingSystem.IsWindows()
                ? (Path.Combine(Environment.SystemDirectory, "cmd.exe"), new[] { "/c", "echo", "hello" })
                : ("/bin/echo", new[] { "hello" });
            using var p = ProcessRunner.StartDetached(binary, args, log);
            await p.WaitForExitAsync();
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("hello", File.ReadAllText(log));
            Assert.StartsWith("before", File.ReadAllText(log));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
