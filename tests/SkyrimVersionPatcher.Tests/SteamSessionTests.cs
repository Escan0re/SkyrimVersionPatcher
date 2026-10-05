using System.Diagnostics;
using SkyrimVersionPatcher.Core.Downloading;

public static class SteamSessionTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("Steam process lookup rejects invalid identifiers without throwing", () =>
        {
            foreach (var processId in new[] { 0, -1 })
            {
                Assert.True(!WindowsProcessIdentity.TryGetExecutablePath(processId, out var path, out var error));
                Assert.Equal<string?>(null, path);
                Assert.Equal(87, error);
            }
        });
        suite.Add("Steam process lookup reads current executable with limited rights", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            Assert.True(WindowsProcessIdentity.TryGetExecutablePath(Environment.ProcessId, out var path, out var error), $"Windows error {error}.");
            Assert.Equal(0, error);
            Assert.True(string.Equals(Path.GetFullPath(Environment.ProcessPath!), path, StringComparison.OrdinalIgnoreCase), path);
        });
        suite.Add("Steam process lookup reports an exited process without throwing", () =>
        {
            if (!OperatingSystem.IsWindows()) return;
            using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
            {
                Arguments = "/c exit 0", UseShellExecute = false, CreateNoWindow = true
            })!;
            var processId = process.Id;
            process.WaitForExit();
            Assert.True(!WindowsProcessIdentity.TryGetExecutablePath(processId, out var path, out var error));
            Assert.Equal<string?>(null, path);
            Assert.True(error is 87 or 1168, $"Unexpected Windows error {error}.");
        });
    }
}
