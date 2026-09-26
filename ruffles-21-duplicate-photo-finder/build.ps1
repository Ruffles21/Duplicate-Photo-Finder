[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SelfContained,
    [switch]$IncludeRecycleTest,
    [switch]$IncludeVideoTest,
    [string]$DotnetPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($env:OS -ne 'Windows_NT') {
    throw 'Building and testing this WPF application requires Windows.'
}

$repositoryRoot = $PSScriptRoot
$bundledDotnet = Join-Path $repositoryRoot '.tools/dotnet/dotnet.exe'
if (-not $DotnetPath) {
    if (Test-Path -LiteralPath $bundledDotnet -PathType Leaf) {
        $DotnetPath = $bundledDotnet
    }
    else {
        $DotnetPath = (Get-Command dotnet -ErrorAction Stop).Source
    }
}
$DotnetPath = (Resolve-Path -LiteralPath $DotnetPath).Path

function Invoke-Dotnet {
    param([string[]]$Arguments)

    & $DotnetPath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

$previousCliHome = $env:DOTNET_CLI_HOME
$previousDotnetRoot = $env:DOTNET_ROOT
Push-Location -LiteralPath $repositoryRoot
try {
    $env:DOTNET_ROOT = Split-Path -Parent $DotnetPath
    if (-not $env:DOTNET_CLI_HOME) {
        $env:DOTNET_CLI_HOME = Join-Path $repositoryRoot '.tools/cli'
    }

    Invoke-Dotnet -Arguments @('restore', 'Ruffles21.DuplicatePhotoFinder.sln')
    Invoke-Dotnet -Arguments @('build', 'Ruffles21.DuplicatePhotoFinder.sln', '--configuration', $Configuration, '--no-restore')
    $testArguments = @('run', '--project', 'Ruffles21.DuplicatePhotoFinder.Tests/Ruffles21.DuplicatePhotoFinder.Tests.csproj', '--configuration', $Configuration, '--no-build', '--no-restore')
    $nativeTests = @()
    if ($IncludeRecycleTest) { $nativeTests += '--recycle-test' }
    if ($IncludeVideoTest) { $nativeTests += '--video-test' }
    if ($nativeTests.Count -gt 0) { $testArguments += @('--') + $nativeTests }
    Invoke-Dotnet -Arguments $testArguments

    [xml]$project = Get-Content -LiteralPath 'Ruffles21.DuplicatePhotoFinder/Ruffles21.DuplicatePhotoFinder.csproj' -Raw
    $version = [version]$project.Project.PropertyGroup.Version
    $releaseVersion = if ($version.Build -eq 0) { '{0}.{1}' -f $version.Major, $version.Minor } else { $version.ToString() }
    $packageName = "ruffles_21s-Duplicate-Photo-Finder-Windows-v$releaseVersion"
    if ($SelfContained) { $packageName += '-self-contained-x64' }

    # A fresh folder prevents obsolete files from previous releases entering the ZIP.
    $stageRoot = Join-Path $repositoryRoot ('artifacts/staging/' + [Guid]::NewGuid().ToString('N'))
    # MSBuild's item transforms misparse apostrophes in PublishDir. Publish to a
    # neutral directory, then give the completed app folder its display name.
    $publishFolder = Join-Path $stageRoot 'publish'
    $namedFolder = Join-Path $stageRoot "ruffles_21's Duplicate Photo Finder"
    $archivePath = Join-Path $repositoryRoot "artifacts/$packageName.zip"
    $selfContainedValue = $SelfContained.IsPresent.ToString().ToLowerInvariant()
    Invoke-Dotnet -Arguments @('publish', 'Ruffles21.DuplicatePhotoFinder/Ruffles21.DuplicatePhotoFinder.csproj', '--configuration', $Configuration, '--runtime', 'win-x64', '--self-contained', $selfContainedValue, '--output', $publishFolder)
    Copy-Item -LiteralPath 'README.md', 'CHANGELOG.md' -Destination $publishFolder
    Copy-Item -LiteralPath 'docs' -Destination $publishFolder -Recurse
    $rootPrefix = [IO.Path]::GetFullPath($repositoryRoot).TrimEnd('\') + '\'
    foreach ($candidatePath in @($publishFolder, $namedFolder)) {
        if (-not [IO.Path]::GetFullPath($candidatePath).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The package directory must stay inside the repository.'
        }
    }
    Move-Item -LiteralPath $publishFolder -Destination $namedFolder
    Compress-Archive -LiteralPath $namedFolder -DestinationPath $archivePath -CompressionLevel Optimal -Force

    Write-Host "Application: $namedFolder"
    Write-Host "Windows ZIP: $archivePath"
}
finally {
    Pop-Location
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:DOTNET_ROOT = $previousDotnetRoot
}
