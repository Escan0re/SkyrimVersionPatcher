using System.Diagnostics;

namespace SkyrimVersionPatcher.Tests;

public static class PublishingTests
{
    public static void Register(TestSuite suite)
    {
        suite.Add("publish: one launcher contains a clean payload and failures preserve the previous release", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "svp-publish-" + Guid.NewGuid().ToString("N"));
            var scripts = Path.Combine(root, "scripts");
            Directory.CreateDirectory(scripts);
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts"));
            foreach (var name in new[] { "publish.ps1", "launcher.nsi" }) File.Copy(Path.Combine(source, name), Path.Combine(scripts, name));
            foreach (var name in new[] { "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "data/SOURCES.md", "data/versions.json", "data/executable-hashes.json", "data/transitions/index.json" })
            {
                var path = Path.Combine(root, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "fixture " + name);
            }
            var project = Path.Combine(root, "src", "SkyrimVersionPatcher.App", "SkyrimVersionPatcher.App.csproj");
            Directory.CreateDirectory(Path.GetDirectoryName(project)!);
            File.WriteAllText(project, "<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>");
            var output = Path.Combine(root, "artifacts", "SkyrimVersionPatcher-single-exe-win-x64");
            var legacyOutput = Path.Combine(root, "artifacts", "SkyrimVersionPatcher-win-x64");
            Directory.CreateDirectory(Path.Combine(legacyOutput, "Storage"));
            File.WriteAllText(Path.Combine(legacyOutput, "SkyrimVersionPatcher.exe"), "old exe");
            File.WriteAllText(Path.Combine(legacyOutput, "settings.json"), "personal settings");
            File.WriteAllText(Path.Combine(legacyOutput, "Storage", "cached-depot.bin"), "personal depot");
            File.WriteAllText(Path.Combine(legacyOutput, "obsolete.dll"), "old runtime");
            File.WriteAllText(legacyOutput + ".zip", "old zip");
            var driver = Path.Combine(root, "driver.ps1");
            File.WriteAllText(driver, """
                param([string]$PublishScript, [string]$FailStep, [switch]$ChangedPayload, [switch]$VersionedOutput)
                $ErrorActionPreference = 'Stop'
                function dotnet {
                    if ($args[0] -eq 'build' -and $FailStep -eq 'build') { $global:LASTEXITCODE = 1; return }
                    if ($args[0] -eq 'publish') {
                        $outputIndex = [Array]::IndexOf($args, '-o')
                        $publishOutput = $args[$outputIndex + 1]
                        New-Item -ItemType Directory -Path (Join-Path $publishOutput 'data/transitions') -Force | Out-Null
                        [IO.File]::WriteAllText((Join-Path $publishOutput 'SkyrimVersionPatcher.exe'), 'inner exe')
                        [IO.File]::WriteAllText((Join-Path $publishOutput 'wpfgfx_cor3.dll'), 'native runtime' + $ChangedPayload)
                        Copy-Item -LiteralPath 'data/transitions/index.json' -Destination (Join-Path $publishOutput 'data/transitions')
                        if ($FailStep -eq 'publish') { $global:LASTEXITCODE = 1; return }
                    }
                    $global:LASTEXITCODE = 0
                }
                function makensis {
                    $payload = ($args | Where-Object { $_ -like '/DPAYLOAD_DIR=*' }).Substring(14)
                    $identifier = ($args | Where-Object { $_ -like '/DPAYLOAD_ID=*' }).Substring(13)
                    $launcher = ($args | Where-Object { $_ -like '/DOUTPUT_FILE=*' }).Substring(14)
                    if ($identifier -notmatch '^[a-f0-9]{64}$') { throw 'Invalid payload identifier.' }
                    $names = Get-ChildItem -LiteralPath $payload -File -Recurse | ForEach-Object { $_.FullName.Substring($payload.Length + 1).Replace('\', '/') }
                    [IO.File]::WriteAllLines((Join-Path $PSScriptRoot 'payload-files.txt'), [string[]]$names)
                    [IO.File]::WriteAllText($launcher, $identifier)
                    $global:LASTEXITCODE = [int]($FailStep -eq 'makensis')
                }
                & $PublishScript -SkipTests -VersionedOutput:$VersionedOutput
                """);
            try
            {
                var script = Path.Combine(scripts, "publish.ps1");
                await RunAsync(driver, script);
                var executable = Path.Combine(output, "SkyrimVersionPatcher.exe");
                Assert.Equal("SkyrimVersionPatcher.exe", Path.GetFileName(Directory.EnumerateFileSystemEntries(output).Single()));
                Assert.True(!File.Exists(output + ".zip"));
                var names = File.ReadAllLines(Path.Combine(root, "payload-files.txt"));
                foreach (var name in new[] { "SkyrimVersionPatcher.exe", "wpfgfx_cor3.dll", "README.md", "LICENSE", "THIRD-PARTY-NOTICES.md", "data/SOURCES.md", "data/versions.json", "data/executable-hashes.json", "data/transitions/index.json" })
                    Assert.True(names.Contains(name), name);
                var original = File.ReadAllText(executable);
                await RunAsync(driver, script);
                Assert.Equal(original, File.ReadAllText(executable));
                await RunAsync(driver, script, changedPayload: true);
                Assert.True(original != File.ReadAllText(executable), "Changing payload content must change the extraction cache directory.");

                await RunAsync(driver, script);
                names = File.ReadAllLines(Path.Combine(root, "payload-files.txt"));
                Assert.True(!names.Any(name => name.Contains("settings.json") || name.StartsWith("Storage/") || name.Contains("obsolete.dll")));
                Assert.Equal("personal settings", File.ReadAllText(Path.Combine(legacyOutput, "settings.json")));
                Assert.Equal("personal depot", File.ReadAllText(Path.Combine(legacyOutput, "Storage", "cached-depot.bin")));
                Assert.Equal("old runtime", File.ReadAllText(Path.Combine(legacyOutput, "obsolete.dll")));
                Assert.Equal("old exe", File.ReadAllText(Path.Combine(legacyOutput, "SkyrimVersionPatcher.exe")));
                Assert.Equal("old zip", File.ReadAllText(legacyOutput + ".zip"));
                Assert.Equal("SkyrimVersionPatcher.exe", Path.GetFileName(Directory.EnumerateFileSystemEntries(output).Single()));
                foreach (var failStep in new[] { "build", "publish", "makensis" })
                {
                    var previous = File.ReadAllBytes(executable);
                    await RunAsync(driver, script, failStep, changedPayload: true);
                    Assert.True(previous.SequenceEqual(File.ReadAllBytes(executable)), failStep);
                    Assert.True(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "artifacts"), ".publish-*").Any());
                }
                await RunAsync(driver, script, versionedOutput: true);
                Assert.Equal("SkyrimVersionPatcher.exe", Path.GetFileName(Directory.EnumerateFileSystemEntries(output + "-1.2.3").Single()));
                Assert.True(!Directory.EnumerateFileSystemEntries(Path.Combine(root, "artifacts"), ".publish-*").Any());
            }
            finally { Directory.Delete(root, recursive: true); }
        });
    }

    private static async Task RunAsync(string driver, string script, string? failStep = null, bool changedPayload = false, bool versionedOutput = false)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.Environment.Remove("PSModulePath"); // Let Windows PowerShell find its own modules when launched from pwsh.
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", driver, "-PublishScript", script })
            start.ArgumentList.Add(arg);
        if (failStep is not null)
        {
            start.ArgumentList.Add("-FailStep");
            start.ArgumentList.Add(failStep);
        }
        if (changedPayload) start.ArgumentList.Add("-ChangedPayload");
        if (versionedOutput) start.ArgumentList.Add("-VersionedOutput");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var diagnostic = await output + await error;
        Assert.True(failStep is not null ? process.ExitCode != 0 : process.ExitCode == 0, diagnostic);
    }
}
