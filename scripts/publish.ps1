param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$projectPath = Join-Path $repoRoot 'src/LanChat.Desktop/LanChat.Desktop.csproj'
$buildRoot = Join-Path $repoRoot '.build/publish'
$stagingPath = Join-Path $buildRoot ([Guid]::NewGuid().ToString('N'))
$artifactRoot = Join-Path $repoRoot 'artifacts'
$versionOutput = dotnet msbuild $projectPath -getProperty:Version -nologo
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the application version.' }
$version = ($versionOutput | Out-String).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "Invalid application version: $version" }
$executablePath = Join-Path $artifactRoot "飞驴-v$version.exe"
$published = $false

New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null
New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
try {
    $publishArguments = @('publish', $projectPath, '-c', 'Release', '-r', $Runtime, '--self-contained', 'true',
        '-p:PublishAot=false', '-p:PublishTrimmed=true', '-p:TrimMode=full',
        '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=embedded', '-p:DebugSymbols=false', '-p:EnableCompressionInSingleFile=true', '-o', $stagingPath)
    dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed; the existing executable was not replaced.' }

    $previousPackages = @(Get-ChildItem -LiteralPath $artifactRoot -File | Where-Object { $_.Name -match '^飞驴(-v\d+\.\d+\.\d+)?\.exe$' })
    # Check locks before changing any delivered package.
    foreach ($package in $previousPackages) {
        try {
            $handle = [System.IO.File]::Open($package.FullName, [System.IO.FileMode]::Open, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            $handle.Dispose()
        } catch {
            throw "Exit the running client from its tray menu before updating. Candidate retained at: $stagingPath. $($_.Exception.Message)"
        }
    }
    $backupPath = Join-Path $repoRoot ('.build/previous-publish/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null
    $movedPackages = @()
    try {
        foreach ($package in $previousPackages) {
            Move-Item -LiteralPath $package.FullName -Destination (Join-Path $backupPath $package.Name)
            $movedPackages += $package
        }
        Copy-Item -LiteralPath (Join-Path $stagingPath 'LanChat.Desktop.exe') -Destination $executablePath -Force
        $published = $true
    } catch {
        if (Test-Path -LiteralPath $executablePath) { Remove-Item -LiteralPath $executablePath -Force }
        foreach ($package in $movedPackages) { Move-Item -LiteralPath (Join-Path $backupPath $package.Name) -Destination $package.FullName }
        throw "Cannot update the executable. Candidate retained at: $stagingPath. $($_.Exception.Message)"
    }
    Write-Host "Executable: $executablePath" -ForegroundColor Green
} finally {
    # Remove only this invocation's verified staging directory.
    $resolvedStaging = [System.IO.Path]::GetFullPath($stagingPath)
    $expectedParent = [System.IO.Path]::GetFullPath($buildRoot)
    if ((Split-Path -Parent $resolvedStaging) -ne $expectedParent) { throw 'Refusing to remove a staging directory outside the build folder.' }
    if ($published -and (Test-Path -LiteralPath $resolvedStaging)) { Remove-Item -LiteralPath $resolvedStaging -Recurse -Force }
}
