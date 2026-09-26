[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repositoryRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$rootPrefix = $repositoryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$stageRoot = Join-Path $repositoryRoot ('artifacts/github-source/' + [Guid]::NewGuid().ToString('N'))
$sourceFolder = Join-Path $stageRoot 'ruffles-21-duplicate-photo-finder'

function Assert-WorkspacePath {
    param([string]$Path)
    if (-not [IO.Path]::GetFullPath($Path).StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Source packaging must stay inside the repository.'
    }
}

function Copy-SourceFile {
    param([string]$RelativePath)
    $source = Join-Path $repositoryRoot $RelativePath
    $destination = Join-Path $sourceFolder $RelativePath
    Assert-WorkspacePath $source
    Assert-WorkspacePath $destination
    if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Linked files are not included in the source package: $RelativePath"
    }
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $destination
}

# Only application sources and repository documentation enter this archive.
foreach ($relative in @('README.md', 'CHANGELOG.md', 'build.ps1', 'package-source.ps1',
        'global.json', '.editorconfig', '.gitattributes', '.gitignore', 'Ruffles21.DuplicatePhotoFinder.sln',
        '.github/workflows/build.yml')) {
    Copy-SourceFile $relative
}
foreach ($licenseName in @('LICENSE', 'LICENSE.md', 'LICENSE.txt')) {
    if (Test-Path -LiteralPath (Join-Path $repositoryRoot $licenseName)) { Copy-SourceFile $licenseName }
}
foreach ($folder in @('Ruffles21.DuplicatePhotoFinder', 'Ruffles21.DuplicatePhotoFinder.Tests', 'docs')) {
    $folderPath = Join-Path $repositoryRoot $folder
    if ((Get-Item -LiteralPath $folderPath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "Linked source folders are not supported: $folder"
    }
    foreach ($file in Get-ChildItem -LiteralPath $folderPath -Recurse -File) {
        $relative = $file.FullName.Substring($rootPrefix.Length)
        if ($relative -match '(^|[\\/])(bin|obj|\.vs|\.git)([\\/]|$)') { continue }
        if ($file.Extension -notin @('.cs', '.xaml', '.csproj', '.md', '.png', '.ico', '.svg')) { continue }
        Copy-SourceFile $relative
    }
}

[xml]$project = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Ruffles21.DuplicatePhotoFinder/Ruffles21.DuplicatePhotoFinder.csproj') -Raw
$version = [version]$project.Project.PropertyGroup.Version
$releaseVersion = if ($version.Build -eq 0) { '{0}.{1}' -f $version.Major, $version.Minor } else { $version.ToString() }
$archivePath = Join-Path $repositoryRoot "artifacts/ruffles_21s-Duplicate-Photo-Finder-GitHub-v$releaseVersion.zip"
$temporaryZip = Join-Path $stageRoot 'source.zip'
Assert-WorkspacePath $sourceFolder
Assert-WorkspacePath $archivePath
Assert-WorkspacePath $temporaryZip
[IO.Compression.ZipFile]::CreateFromDirectory($sourceFolder, $temporaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
Move-Item -LiteralPath $temporaryZip -Destination $archivePath -Force
Write-Host "GitHub folder: $sourceFolder"
Write-Host "Source ZIP: $archivePath"
