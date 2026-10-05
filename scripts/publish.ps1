param([switch]$SkipTests, [switch]$VersionedOutput)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -LiteralPath (Join-Path $repository 'src/SkyrimVersionPatcher.App/SkyrimVersionPatcher.App.csproj') -Raw)
$releaseVersion = [string]$project.Project.PropertyGroup.Version
if ($releaseVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid release version.' }
$artifactName = 'SkyrimVersionPatcher-single-exe-win-x64'
if ($VersionedOutput) {
    $artifactName += '-' + $releaseVersion
}
$compiler = Get-Command makensis -ErrorAction SilentlyContinue
if (!$compiler) {
    $compiler = Join-Path $repository '.tools/nsis/makensis.exe'
    if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) { throw 'NSIS compiler not found. Add makensis to PATH or place it in .tools/nsis/makensis.exe.' }
}
$output = Join-Path $repository ('artifacts/' + $artifactName)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
$staging = [IO.Path]::GetFullPath((Join-Path $artifactRoot ('.publish-' + [guid]::NewGuid().ToString('N'))))
$payload = Join-Path $staging 'payload'
$launcher = Join-Path $staging 'SkyrimVersionPatcher.exe'
Push-Location $repository
try {
    dotnet build SkyrimVersionPatcher.slnx -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (-not $SkipTests) {
        dotnet run --project tests/SkyrimVersionPatcher.Tests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
        dotnet run --project tests/SkyrimVersionPatcher.UiTests -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
    }
    dotnet publish src/SkyrimVersionPatcher.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:IncludeAllContentForSelfExtract=false -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath (Join-Path $repository 'README.md') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $repository 'LICENSE') -Destination $payload
    Copy-Item -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md') -Destination $payload
    $dataOutput = Join-Path $payload 'data'
    New-Item -ItemType Directory -Path $dataOutput -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'data/SOURCES.md') -Destination $dataOutput
    Copy-Item -LiteralPath (Join-Path $repository 'data/versions.json') -Destination $dataOutput
    Copy-Item -LiteralPath (Join-Path $repository 'data/executable-hashes.json') -Destination $dataOutput
    $manifest = Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($payload.Length + 1).Replace('\', '/') + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $payloadId = [BitConverter]::ToString($hasher.ComputeHash([Text.Encoding]::UTF8.GetBytes(($manifest -join "`n")))).Replace('-', '').ToLowerInvariant() }
    finally { $hasher.Dispose() }
    & $compiler /V2 /NOCONFIG "/DPAYLOAD_DIR=$payload" "/DPAYLOAD_ID=$payloadId" "/DOUTPUT_FILE=$launcher" "/DAPP_VERSION=$releaseVersion" "/DICON_FILE=$(Join-Path $repository 'src/SkyrimVersionPatcher.App/Assets/SkyrimVersionPatcher.ico')" (Join-Path $PSScriptRoot 'launcher.nsi')
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $launcher -PathType Leaf)) { throw 'Launcher compilation failed.' }
    # Publish only the launcher. Leave earlier portable data and artifacts untouched.
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    $destination = Join-Path $output 'SkyrimVersionPatcher.exe'
    if (Test-Path -LiteralPath $destination) { [IO.File]::Replace($launcher, $destination, (Join-Path $staging 'previous.exe')) }
    else { Move-Item -LiteralPath $launcher -Destination $destination }
    Get-FileHash -LiteralPath $destination -Algorithm SHA256
}
finally {
    Pop-Location
    if (!$staging.StartsWith($artifactRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Publish staging directory is outside artifacts.'
    }
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
