param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][ValidateSet('linux-x64','linux-arm64')][string]$Runtime
)
$ErrorActionPreference = 'Stop'
if (!$IsLinux) { throw 'AppImage must be built on Linux.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source = [IO.Path]::GetFullPath($SourceDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$root = Join-Path $repoRoot ('.build/appimage/' + [Guid]::NewGuid().ToString('N'))
$appdir = Join-Path $root 'Feilv.AppDir'; $native = Join-Path $appdir 'usr/bin'
New-Item -ItemType Directory -Path $native,$output -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $native -Recurse -Force
$run = @'
#!/bin/sh
exec "$APPDIR/usr/bin/feilv" "$@"
'@
[IO.File]::WriteAllText((Join-Path $appdir 'AppRun'), $run.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
& chmod +x (Join-Path $appdir 'AppRun') (Join-Path $native 'feilv')
if ($LASTEXITCODE -ne 0) { throw 'Cannot set AppImage executable permissions.' }
$desktop = @'
[Desktop Entry]
Type=Application
Name=Feilv
Comment=LAN file transfer and folder sharing
Exec=feilv
Icon=feilv
Categories=Network;FileTransfer;
Terminal=false
'@
[IO.File]::WriteAllText((Join-Path $appdir 'feilv.desktop'), $desktop.Replace("`r`n", "`n") + "`n", [Text.UTF8Encoding]::new($false))
& rsvg-convert -w 256 -h 256 -o (Join-Path $appdir 'feilv.png') (Join-Path $repoRoot 'src/LanChat.Desktop/Assets/Brand/donkey.svg')
if ($LASTEXITCODE -ne 0) { throw 'Cannot render the AppImage icon.' }
$architecture = if ($Runtime -eq 'linux-x64') { 'x86_64' } else { 'aarch64' }
$toolHash = if ($Runtime -eq 'linux-x64') { 'ed4ce84f0d9caff66f50bcca6ff6f35aae54ce8135408b3fa33abfc3cb384eb0' } else { 'f0837e7448a0c1e4e650a93bb3e85802546e60654ef287576f46c71c126a9158' }
$runtimeHash = if ($Runtime -eq 'linux-x64') { '617aa77314d57c8b8e4fae9c3f0b35af7b30d5529cb69e98f64f92510ed98a46' } else { '9a72a744d221576c9347ec074243acc5c3c7568896b12714e0e82e9f0bf47900' }
$tool = Join-Path $root 'appimagetool.AppImage'; $runtimeFile = Join-Path $root 'appimage-runtime'
Invoke-WebRequest -Uri "https://github.com/AppImage/appimagetool/releases/download/1.9.1/appimagetool-$architecture.AppImage" -OutFile $tool
Invoke-WebRequest -Uri "https://github.com/AppImage/type2-runtime/releases/download/continuous/runtime-$architecture" -OutFile $runtimeFile
if ((Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLowerInvariant() -ne $toolHash -or (Get-FileHash -LiteralPath $runtimeFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $runtimeHash) { throw 'AppImage tooling digest mismatch.' }
& chmod +x $tool
$package = Join-Path $output "feilv-v$Version-$Runtime.AppImage"
$previousArch = $env:ARCH
try {
    $env:ARCH = $architecture
    & $tool --appimage-extract-and-run --runtime-file $runtimeFile $appdir $package
    if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $package)) { throw 'Cannot build AppImage.' }
} finally { $env:ARCH = $previousArch }
& chmod +x $package
Write-Host "Linux AppImage: $package"
