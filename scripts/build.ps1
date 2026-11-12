[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$Version = '1.0.0',
    [switch]$SkipSmoke
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$artifactRoot = Join-Path $projectRoot 'artifacts'
$publishRoot = Join-Path $artifactRoot 'publish/win-x64'
$releaseRoot = Join-Path $artifactRoot 'release'
$packageRoot = Join-Path $artifactRoot 'package/LagLens'
$appProject = Join-Path $projectRoot 'src/LagLens.Windows/LagLens.Windows.csproj'
$testProject = Join-Path $projectRoot 'tests/LagLens.Tests/LagLens.Tests.csproj'

function Invoke-DotNet {
    param([string[]]$DotNetArgs)
    & dotnet @DotNetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

function Invoke-Smoke {
    param([string]$Executable, [string]$Mode, [string]$OutputDirectory)
    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    $process = Start-Process -FilePath $Executable -ArgumentList @($Mode, '--output', ('"' + $OutputDirectory + '"')) -PassThru
    if (-not $process.WaitForExit(120000)) {
        $process.Kill()
        throw "$Mode did not finish within two minutes."
    }
    if ($process.ExitCode -ne 0) { throw "$Mode failed with exit code $($process.ExitCode). Inspect $OutputDirectory" }
    if (-not (Get-ChildItem -Path $OutputDirectory -File -Recurse)) { throw "$Mode produced no validation evidence." }
}

function Assert-RuntimeNotices {
    # Assets identify the exact runtime packages selected by this SDK; do not pick an arbitrary cache version.
    $assetsPath = Join-Path $projectRoot 'src/LagLens.Windows/obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $dependencies = @($assets.project.frameworks.PSObject.Properties | ForEach-Object { $_.Value.downloadDependencies })
    $noticeSpecs = @(
        @{ Package = 'Microsoft.NETCore.App.Runtime.win-x64'; Source = 'LICENSE.TXT'; Destination = 'DOTNET-LICENSE.txt' },
        @{ Package = 'Microsoft.NETCore.App.Runtime.win-x64'; Source = 'THIRD-PARTY-NOTICES.TXT'; Destination = 'DOTNET-THIRD-PARTY-NOTICES.txt' },
        @{ Package = 'Microsoft.WindowsDesktop.App.Runtime.win-x64'; Source = 'LICENSE'; Destination = 'WINDOWS-DESKTOP-LICENSE.txt' }
    )
    foreach ($spec in $noticeSpecs) {
        $dependency = $dependencies | Where-Object { $_.name -eq $spec.Package } | Select-Object -First 1
        if (-not $dependency) { throw "Restored assets do not identify $($spec.Package)." }
        $versionMatch = [regex]::Match($dependency.version, '\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?')
        if (-not $versionMatch.Success) { throw "Cannot read restored runtime version: $($dependency.version)" }
        $relativePath = $spec.Package.ToLowerInvariant() + '/' + $versionMatch.Value + '/' + $spec.Source
        $restoredPath = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $relativePath
            if (Test-Path -LiteralPath $candidate) { $restoredPath = $candidate; break }
        }
        if (-not $restoredPath) { throw "Missing restored runtime notice: $relativePath" }
        $repositoryPath = Join-Path $projectRoot ('docs/licenses/' + $spec.Destination)
        $restoredText = [IO.File]::ReadAllText($restoredPath).Replace("`r`n", "`n")
        $repositoryText = [IO.File]::ReadAllText($repositoryPath).Replace("`r`n", "`n")
        if ($restoredText -cne $repositoryText) {
            throw "Runtime notices changed in $($spec.Package) $($versionMatch.Value). Update docs/licenses/$($spec.Destination) from $restoredPath and rebuild before release."
        }
        Write-Host "Verified runtime notice: $($spec.Package) $($versionMatch.Value)/$($spec.Source)"
    }
}

if ($Version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?$') { throw 'Version must be a semantic version such as 1.0.0.' }
Push-Location $projectRoot
try {
    Invoke-DotNet -DotNetArgs @('run', '--project', $testProject, '-c', $Configuration)
    foreach ($directory in @($publishRoot, $releaseRoot, $packageRoot)) {
        if (Test-Path $directory) { Remove-Item $directory -Recurse -Force }
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Invoke-DotNet -DotNetArgs @('publish', $appProject, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'true',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=false',
        "-p:Version=$Version", '-o', $publishRoot)

    $executable = Join-Path $publishRoot 'LagLens.exe'
    if (-not (Test-Path $executable)) { throw 'The publish output is missing LagLens.exe.' }
    $binary = [IO.File]::ReadAllBytes($executable)
    if ($binary.Length -lt 256 -or $binary[0] -ne 0x4D -or $binary[1] -ne 0x5A) { throw 'Output is not a PE executable.' }
    $peOffset = [BitConverter]::ToInt32($binary, 0x3C)
    if ([BitConverter]::ToUInt32($binary, $peOffset) -ne 0x4550 -or [BitConverter]::ToUInt16($binary, $peOffset + 4) -ne 0x8664) {
        throw 'The executable is not Windows AMD64 (x64).'
    }
    Assert-RuntimeNotices

    if (-not $SkipSmoke) {
        if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'Run smoke checks on Windows, or use -SkipSmoke for an unqualified cross-build.' }
        Invoke-Smoke $executable '--self-test' (Join-Path $artifactRoot 'smoke')
        Invoke-Smoke $executable '--ui-smoke' (Join-Path $artifactRoot 'ui-smoke')
    }

    Copy-Item $executable (Join-Path $packageRoot 'LagLens.exe')
    Copy-Item (Join-Path $projectRoot 'README.md') $packageRoot
    Copy-Item (Join-Path $projectRoot 'LICENSE') $packageRoot
    Copy-Item (Join-Path $projectRoot 'docs/DIAGNOSTICS.md') $packageRoot
    Copy-Item (Join-Path $projectRoot 'docs/licenses') (Join-Path $packageRoot 'licenses') -Recurse
    $assetPrefix = "LagLens-$Version-win-x64"
    Copy-Item $executable (Join-Path $releaseRoot "$assetPrefix.exe")
    Compress-Archive -Path $packageRoot -DestinationPath (Join-Path $releaseRoot "$assetPrefix.zip") -CompressionLevel Optimal -Force
    Copy-Item (Join-Path $projectRoot 'LICENSE') (Join-Path $releaseRoot 'LICENSE.txt')
    $combinedNotices = @('LagLens includes the .NET and Windows Desktop runtimes. Their notices follow.')
    foreach ($notice in (Get-ChildItem (Join-Path $projectRoot 'docs/licenses') -Filter '*.txt' | Sort-Object Name)) {
        $combinedNotices += "`n===== $($notice.Name) =====`n"
        $combinedNotices += [IO.File]::ReadAllText($notice.FullName)
    }
    [IO.File]::WriteAllText((Join-Path $releaseRoot 'THIRD-PARTY-NOTICES.txt'), ($combinedNotices -join "`n"), [Text.UTF8Encoding]::new($false))
    $checksums = Get-ChildItem $releaseRoot -File | Sort-Object Name | ForEach-Object {
        $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $($_.Name)"
    }
    [IO.File]::WriteAllLines((Join-Path $releaseRoot 'SHA256SUMS.txt'), $checksums, [Text.UTF8Encoding]::new($false))
    Write-Host "Packaged x64 EXE, portable ZIP and SHA-256 checksums: $releaseRoot"
}
finally { Pop-Location }
