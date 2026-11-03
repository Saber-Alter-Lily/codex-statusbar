# CI-only packaging: publish locally, test the resulting EXE with synthetic data, then ZIP.
# Does not install, register startup, invoke live probes, create a GitHub Release or call models.
[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'Portable package validation requires Windows' }
$repo = Split-Path -Parent $PSScriptRoot
$sourceCommit = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceCommit -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve source commit' }
$prHead = $env:PORTABLE_PR_HEAD
if ($prHead -notmatch '^[0-9a-f]{40}$') { throw 'Expected explicit PR head commit provenance' }
$sdkVersion = (& dotnet --version).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve SDK version' }
$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$work = Join-Path $env:RUNNER_TEMP ('statusbar-package-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'publish'
$bundle = Join-Path $work 'bundle'
$codexHome = Join-Path $work 'codex-home'
$temp = Join-Path $work 'temp'
$localData = Join-Path $work 'local-app-data'
$fixtures = Join-Path $work 'fixtures'
$sessions = Join-Path $codexHome 'sessions'
$emptyDotnet = Join-Path $work 'empty-dotnet'
$oldEnvironment = @{}
foreach ($name in @('CODEX_HOME','TEMP','TMP','LOCALAPPDATA','DOTNET_BUNDLE_EXTRACT_BASE_DIR','DOTNET_ROOT','DOTNET_ROOT_X64')) {
    $oldEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    New-Item -ItemType Directory -Path $work, $publish, $bundle, $codexHome, $temp, $localData, $fixtures, $sessions, $emptyDotnet -Force | Out-Null
    dotnet publish (Join-Path $repo 'src/CodexStatusbar/CodexStatusbar.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:ContinuousIntegrationBuild=true -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Portable publish failed: $LASTEXITCODE" }
    $publishedFiles = @(Get-ChildItem -LiteralPath $publish -File -Recurse)
    if ($publishedFiles.Count -ne 1 -or $publishedFiles[0].Name -ne 'CodexStatusbar.exe') {
        throw 'Unexpected publish layout: refusing to omit dependencies or package unreviewed files'
    }
    $exe = Join-Path $bundle 'CodexStatusbar.exe'
    Copy-Item -LiteralPath $publishedFiles[0].FullName -Destination $exe
    # Validate the PE architecture rather than relying on a filename suffix.
    $stream = [IO.File]::OpenRead($exe)
    $reader = [IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt16() -ne 0x5A4D) { throw 'EXE is not an MZ image' }
        $stream.Position = 0x3C
        $peOffset = $reader.ReadUInt32()
        $stream.Position = $peOffset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664) { throw 'EXE is not Windows x64 PE' }
    }
    finally { $reader.Dispose(); $stream.Dispose() }

    Copy-Item (Join-Path $repo 'LICENSE'), (Join-Path $repo 'LICENSE-codex-token-overlay'), (Join-Path $repo 'THIRD-PARTY-NOTICES.md') -Destination $bundle
    Copy-Item -LiteralPath (Join-Path $repo 'docs/PORTABLE_TEST.zh-CN.md') -Destination (Join-Path $bundle 'README.txt')
    # Self-contained redistribution includes the SDK distribution's .NET legal notices.
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'LICENSE.txt') -Destination (Join-Path $bundle 'DOTNET-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $dotnetRoot 'ThirdPartyNotices.txt') -Destination (Join-Path $bundle 'DOTNET-THIRD-PARTY-NOTICES.txt')

    Copy-Item (Join-Path $repo 'fixtures/fixture-modern.jsonl'), (Join-Path $repo 'fixtures/fixture-legacy.jsonl') -Destination $fixtures
    $env:CODEX_HOME = $codexHome
    $env:TEMP = $temp
    $env:TMP = $temp
    $env:LOCALAPPDATA = $localData
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $work 'bundle-extract'
    $env:DOTNET_ROOT = $emptyDotnet
    $env:DOTNET_ROOT_X64 = $emptyDotnet
    $stdout = Join-Path $work 'self-test.stdout.txt'
    $stderr = Join-Path $work 'self-test.stderr.txt'
    $process = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"' + $fixtures + '"'), '--sessions', ('"' + $sessions + '"')) -WorkingDirectory $bundle -NoNewWindow -PassThru -RedirectStandardOutput $stdout -RedirectStandardError $stderr
    if (-not $process.WaitForExit(120000)) {
        $process.Kill($true)
        throw 'Published EXE self-test timed out'
    }
    $process.WaitForExit()
    $process.Refresh()
    $outText = Get-Content -LiteralPath $stdout -Raw
    $errorText = Get-Content -LiteralPath $stderr -Raw
    Write-Host $outText
    if ($errorText) { Write-Host $errorText }
    if ($process.ExitCode -ne 0 -or $outText -notmatch '(?m)^RESULT: PASS\r?$') {
        throw "Published EXE self-test failed (exit $($process.ExitCode)); no artifact will be uploaded"
    }
    $checkLine = [regex]::Match($outText, '(?m)^checks: (\d+)\s+failures: 0\r?$')
    if (-not $checkLine.Success) { throw 'Published EXE check count missing' }
    $buildInfo = [ordered]@{
        packageKind = 'portable-test-not-a-release'; runtimeIdentifier = 'win-x64'
        selfContained = $true; singleFile = $true; sourceCommit = $sourceCommit; pullRequestHead = $prHead
        dotnetSdk = $sdkVersion; builtUtc = [DateTime]::UtcNow.ToString('o')
        workflowRun = "$env:GITHUB_SERVER_URL/$env:GITHUB_REPOSITORY/actions/runs/$env:GITHUB_RUN_ID"
        publishedExecutableSyntheticChecks = [int]$checkLine.Groups[1].Value
        publishedExecutableSyntheticFailures = 0
        liveCodexAndGuiValidated = $false; signed = $false
    }
    $buildInfo | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $bundle 'BUILD-INFO.json') -Encoding utf8
    $payload = @(Get-ChildItem -LiteralPath $bundle -File | Sort-Object Name)
    $hashes = @{}
    $lines = foreach ($file in $payload) {
        $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $hashes[$file.Name] = $hash
        "$hash  $($file.Name)"
    }
    $lines | Set-Content -LiteralPath (Join-Path $bundle 'SHA256SUMS.txt') -Encoding ascii
    $expected = @('CodexStatusbar.exe','LICENSE','LICENSE-codex-token-overlay','THIRD-PARTY-NOTICES.md','DOTNET-LICENSE.txt','DOTNET-THIRD-PARTY-NOTICES.txt','README.txt','BUILD-INFO.json','SHA256SUMS.txt') | Sort-Object
    $actual = @(Get-ChildItem -LiteralPath $bundle -File | Select-Object -ExpandProperty Name | Sort-Object)
    if (@(Compare-Object $expected $actual).Count -ne 0) { throw 'Package allowlist mismatch' }
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $zipName = "CodexStatusbar-win-x64-test-$($prHead.Substring(0, 12)).zip"
    $zipPath = Join-Path $OutputDirectory $zipName
    if (Test-Path -LiteralPath $zipPath) { throw 'Refusing to overwrite an existing package' }
    Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $verify = Join-Path $work 'verify'
    Expand-Archive -LiteralPath $zipPath -DestinationPath $verify
    $unzipped = @(Get-ChildItem -LiteralPath $verify -File -Recurse | Select-Object -ExpandProperty Name | Sort-Object)
    if (@(Compare-Object $expected $unzipped).Count -ne 0) { throw 'ZIP contents mismatch' }
    foreach ($name in $hashes.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $verify $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hashes[$name]) {
            throw "ZIP round-trip hash mismatch: $name"
        }
    }
    if ((Get-FileHash (Join-Path $verify 'SHA256SUMS.txt')).Hash -ne (Get-FileHash (Join-Path $bundle 'SHA256SUMS.txt')).Hash) { throw 'Manifest round-trip mismatch' }
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    "$zipHash  $zipName" | Set-Content -LiteralPath "$zipPath.sha256" -Encoding ascii
    Write-Host "PORTABLE TEST PACKAGE VERIFIED: $zipName"
    Write-Host "SHA256: $zipHash"
    if ($env:GITHUB_STEP_SUMMARY) {
        @("## Portable test package", "- File: $zipName", "- SHA256: $zipHash", "- Published EXE synthetic checks: $($checkLine.Groups[1].Value), failures: 0", '- No live Codex/UI compatibility claim; no release or installation.') | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}
finally {
    foreach ($name in $oldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $oldEnvironment[$name], 'Process') }
    if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue }
}
