param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory,
    [Parameter(Mandatory = $true)][string]$Version
)
$ErrorActionPreference = 'Stop'
if (!$IsMacOS) { throw 'The macOS disk image must be built on macOS.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$source = [IO.Path]::GetFullPath($SourceDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$imageRoot = Join-Path $repoRoot ('.build/macos-image/' + [Guid]::NewGuid().ToString('N'))
$content = Join-Path $imageRoot 'content'
$bundle = Join-Path $content 'Feilv.app'
$native = Join-Path $bundle 'Contents/MacOS'
$resources = Join-Path $bundle 'Contents/Resources'
New-Item -ItemType Directory -Path $native,$resources,$output -Force | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $native -Recurse -Force
& chmod +x (Join-Path $native 'feilv')
if ($LASTEXITCODE -ne 0) { throw 'Cannot set macOS executable permissions.' }
# The existing ICO contains PNG frames; extract its 256px frame without introducing a new icon source.
$ico = [IO.File]::ReadAllBytes((Join-Path $repoRoot 'src/LanChat.Desktop/Assets/lanchat.ico'))
$count = [BitConverter]::ToUInt16($ico, 4)
$frame = -1
for ($i = 0; $i -lt $count; $i++) { if ($ico[6 + $i * 16] -eq 0 -and $ico[7 + $i * 16] -eq 0) { $frame = 6 + $i * 16; break } }
if ($frame -lt 0) { throw 'The 256px application icon is missing.' }
$length = [BitConverter]::ToInt32($ico, $frame + 8); $offset = [BitConverter]::ToInt32($ico, $frame + 12)
$baseIcon = Join-Path $imageRoot 'icon.png'
[IO.File]::WriteAllBytes($baseIcon, $ico[$offset..($offset + $length - 1)])
$iconset = Join-Path $imageRoot 'AppIcon.iconset'; New-Item -ItemType Directory -Path $iconset | Out-Null
foreach ($entry in @(@('icon_16x16.png',16), @('icon_16x16@2x.png',32), @('icon_32x32.png',32), @('icon_32x32@2x.png',64), @('icon_128x128.png',128), @('icon_128x128@2x.png',256), @('icon_256x256.png',256), @('icon_256x256@2x.png',512), @('icon_512x512.png',512), @('icon_512x512@2x.png',1024))) {
    & sips -z $entry[1] $entry[1] $baseIcon --out (Join-Path $iconset $entry[0]) | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Cannot generate the macOS icon.' }
}
& iconutil -c icns $iconset -o (Join-Path $resources 'AppIcon.icns')
if ($LASTEXITCODE -ne 0) { throw 'Cannot create the macOS icon bundle.' }
$plist = @"
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>CFBundleName</key><string>Feilv</string>
<key>CFBundleDisplayName</key><string>飞驴 / Feilv</string>
<key>CFBundleIdentifier</key><string>com.github.coolqingcheng.feilv</string>
<key>CFBundleExecutable</key><string>feilv</string>
<key>CFBundlePackageType</key><string>APPL</string>
<key>CFBundleShortVersionString</key><string>$Version</string>
<key>CFBundleVersion</key><string>$Version</string>
<key>CFBundleIconFile</key><string>AppIcon</string>
<key>LSMinimumSystemVersion</key><string>12.0</string>
<key>NSHighResolutionCapable</key><true/>
</dict></plist>
"@
[IO.File]::WriteAllText((Join-Path $bundle 'Contents/Info.plist'), $plist, [Text.UTF8Encoding]::new($false))
& codesign --force --deep --sign - $bundle
if ($LASTEXITCODE -ne 0) { throw 'Cannot ad-hoc sign the macOS application bundle.' }
& ln -s /Applications (Join-Path $content 'Applications')
if ($LASTEXITCODE -ne 0) { throw 'Cannot add the Applications shortcut.' }
$package = Join-Path $output "feilv-v$Version-osx-arm64.dmg"
& hdiutil create -volname Feilv -srcfolder $content -ov -format UDZO -fs HFS+ $package
if ($LASTEXITCODE -ne 0 -or !(Test-Path -LiteralPath $package)) { throw 'Cannot build the macOS disk image.' }
Write-Host "macOS disk image: $package"
