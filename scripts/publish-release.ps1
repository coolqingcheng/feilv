param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime,
    [string]$OutputDirectory = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $repoRoot 'src/LanChat.Desktop/LanChat.Desktop.csproj'
$versionOutput = dotnet msbuild $project -getProperty:Version -nologo
if ($LASTEXITCODE -ne 0) { throw 'Cannot read application version.' }
$version = ($versionOutput | Out-String).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version.' }
if ($env:GITHUB_REF_TYPE -eq 'tag' -and $env:GITHUB_REF_NAME -ne "v$version") { throw 'Tag must match Directory.Build.props.' }
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$stage = Join-Path $repoRoot ('.build/native/' + $Runtime + '/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage,$OutputDirectory -Force | Out-Null

dotnet publish $project -c Release -r $Runtime --self-contained true -o $stage `
    -p:PublishAot=true -p:PublishSingleFile=false -p:StripSymbols=true -p:DebugSymbols=false -p:DebugType=None
if ($LASTEXITCODE -ne 0) { throw 'Native AOT publish failed. No managed fallback is permitted.' }
if (Test-Path -LiteralPath (Join-Path $stage 'LanChat.Desktop.dll')) { throw 'Managed application DLL found in Native AOT output.' }
$isWindowsRuntime = $Runtime.StartsWith('win-')
$executable = if ($isWindowsRuntime) { '飞驴.exe' } else { 'feilv' }
$original = if ($isWindowsRuntime) { 'LanChat.Desktop.exe' } else { 'LanChat.Desktop' }
Move-Item -LiteralPath (Join-Path $stage $original) -Destination (Join-Path $stage $executable)
Get-ChildItem -LiteralPath $stage -Recurse -File | Where-Object { $_.Extension -in '.pdb','.dbg' } | Remove-Item -Force
Get-ChildItem -LiteralPath $stage -Directory -Filter '*.dSYM' | ForEach-Object {
    $resolvedSymbols = [IO.Path]::GetFullPath($_.FullName)
    if ((Split-Path -Parent $resolvedSymbols) -ne [IO.Path]::GetFullPath($stage)) { throw 'Unsafe symbols directory.' }
    Remove-Item -LiteralPath $resolvedSymbols -Recurse -Force
}
if (!$isWindowsRuntime) { & chmod +x (Join-Path $stage $executable); if ($LASTEXITCODE -ne 0) { throw 'Cannot set executable mode.' } }
if ($Runtime.StartsWith('osx-')) {
    Get-ChildItem -LiteralPath $stage -Filter '*.dylib' -File | ForEach-Object { & codesign --force --sign - $_.FullName; if ($LASTEXITCODE -ne 0) { throw 'Cannot ad-hoc sign native library.' } }
    & codesign --force --sign - (Join-Path $stage $executable)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot ad-hoc sign executable.' }
}
$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = @{ version = $version; runtime = $Runtime; executable = $executable; files = $files }
[IO.File]::WriteAllText((Join-Path $stage 'app-manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))

# Execute the actual native binary: validates manifest, hashes and source-generated JSON without a .NET runtime.
$verify = Start-Process -FilePath (Join-Path $stage $executable) -ArgumentList '--verify-package' -WorkingDirectory $stage -PassThru -Wait
if ($verify.ExitCode -ne 0) {
    $diagnostic = Join-Path $stage 'verify-error.txt'
    if (Test-Path -LiteralPath $diagnostic) { Get-Content -LiteralPath $diagnostic | Write-Host }
    throw "Native package verification failed: $($verify.ExitCode)"
}
$mcpVerify = Start-Process -FilePath (Join-Path $stage $executable) -ArgumentList '--verify-mcp' -WorkingDirectory $stage -PassThru -Wait
if ($mcpVerify.ExitCode -ne 0) { throw "Native MCP verification failed: $($mcpVerify.ExitCode)" }
Write-Host 'PASS: Native MCP server authenticates, lists and calls tools over HTTP.'
$smokeDirectory = Join-Path $repoRoot ('.build/smoke/' + $Runtime + '/' + [Guid]::NewGuid().ToString('N'))
$smokeArguments = @('--smoke-test', '--no-discovery', '--port', '0', '--data-dir', ('"' + $smokeDirectory + '"'))
if ($Runtime.StartsWith('linux-') -and !$env:DISPLAY) {
    $smoke = Start-Process -FilePath 'xvfb-run' -ArgumentList (@('-a', (Join-Path $stage $executable)) + $smokeArguments) -PassThru
} else {
    $startOptions = @{ FilePath = (Join-Path $stage $executable); ArgumentList = $smokeArguments; WorkingDirectory = $stage; PassThru = $true }
    if ($isWindowsRuntime) { $startOptions.WindowStyle = 'Hidden' }
    $smoke = Start-Process @startOptions
}
if (!$smoke.WaitForExit(60000)) { throw 'Native GUI smoke test did not finish.' }
if ($smoke.ExitCode -ne 0) { throw "Native GUI smoke test failed: $($smoke.ExitCode)" }
if ($isWindowsRuntime) {
    & (Join-Path $PSScriptRoot 'build-installer.ps1') -SourceDirectory $stage -OutputDirectory $OutputDirectory
    if ($env:GITHUB_ACTIONS -eq 'true') { & (Join-Path $PSScriptRoot 'test-installer.ps1') -InstallerPath (Join-Path $OutputDirectory "feilv-v$version-win-x64-setup.exe") }
} elseif ($Runtime -eq 'osx-arm64') {
    & (Join-Path $PSScriptRoot 'build-macos-image.ps1') -SourceDirectory $stage -OutputDirectory $OutputDirectory -Version $version
    $package = Join-Path $OutputDirectory "feilv-v$version-$Runtime.dmg"
    $mount = Join-Path $repoRoot ('.build/macos-mount/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $mount -Force | Out-Null
    & hdiutil attach -readonly -nobrowse -mountpoint $mount $package
    if ($LASTEXITCODE -ne 0) { throw 'Cannot mount the generated macOS disk image.' }
    try {
        $app = Join-Path $mount 'Feilv.app'
        & codesign --verify --deep --strict $app
        if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath (Join-Path $mount 'Applications'))) { throw 'Invalid macOS application bundle or Applications shortcut.' }
        $installed = Join-Path $repoRoot ('.build/macos-test/' + [Guid]::NewGuid().ToString('N') + '/Feilv.app')
        & ditto $app $installed
        if ($LASTEXITCODE -ne 0) { throw 'Cannot copy the application out of the disk image.' }
        $binary = Join-Path $installed 'Contents/MacOS/feilv'
        $verifyRoot = Join-Path $repoRoot ('.build/macos-verify/' + [Guid]::NewGuid().ToString('N'))
        & ditto (Split-Path -Parent $binary) $verifyRoot
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage macOS manifest verification.' }
        Copy-Item -LiteralPath (Join-Path $mount '.app-manifest.json') -Destination (Join-Path $verifyRoot 'app-manifest.json')
        $verify = Start-Process -FilePath (Join-Path $verifyRoot 'feilv') -ArgumentList '--verify-package' -WorkingDirectory $verifyRoot -PassThru -Wait
        if ($verify.ExitCode -ne 0) {
            $diagnostic = Join-Path $verifyRoot 'verify-error.txt'
            if (Test-Path -LiteralPath $diagnostic) { Get-Content -LiteralPath $diagnostic | Write-Host }
            throw 'Installed macOS bundle verification failed.'
        }
        $smoke = Start-Process -FilePath $binary -ArgumentList $smokeArguments -WorkingDirectory (Split-Path -Parent $binary) -PassThru
        if (!$smoke.WaitForExit(60000) -or $smoke.ExitCode -ne 0) { throw 'Installed macOS application failed its GUI smoke test.' }
        Write-Host 'PASS: DMG contains a verified native application, icon and Applications shortcut; copied application starts.'
        if ($env:GITHUB_ACTIONS -eq 'true') { & (Join-Path $PSScriptRoot 'test-native-image-update.ps1') -Package $package -Runtime $Runtime -Version $version -MacBundle $installed -MacManifest (Join-Path $mount '.app-manifest.json') }
    } finally { & hdiutil detach $mount -quiet }
} else {
    & (Join-Path $PSScriptRoot 'build-linux-appimage.ps1') -SourceDirectory $stage -OutputDirectory $OutputDirectory -Version $version -Runtime $Runtime
    $package = Join-Path $OutputDirectory "feilv-v$version-$Runtime.AppImage"
    $verify = Start-Process -FilePath $package -ArgumentList @('--appimage-extract-and-run', '--verify-package') -PassThru -Wait
    if ($verify.ExitCode -ne 0) { throw 'AppImage package verification failed.' }
    $appImageArgs = @($package, '--appimage-extract-and-run') + $smokeArguments
    $smoke = Start-Process -FilePath 'xvfb-run' -ArgumentList (@('-a') + $appImageArgs) -PassThru
    if (!$smoke.WaitForExit(90000) -or $smoke.ExitCode -ne 0) { throw 'AppImage GUI smoke test failed.' }
    Write-Host 'PASS: AppImage runs its verified native program without a .NET runtime or FUSE.'
    if ($env:GITHUB_ACTIONS -eq 'true') { & (Join-Path $PSScriptRoot 'test-native-image-update.ps1') -Package $package -Runtime $Runtime -Version $version }
}
