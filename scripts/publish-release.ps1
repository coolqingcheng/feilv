param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')]
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
if ($verify.ExitCode -ne 0) { throw "Native package verification failed: $($verify.ExitCode)" }
$extension = if ($isWindowsRuntime) { '.zip' } else { '.tar.gz' }
$package = Join-Path $OutputDirectory "feilv-v$version-$Runtime$extension"
if ($isWindowsRuntime) { [IO.Compression.ZipFile]::CreateFromDirectory($stage, $package) }
else { & tar -czf $package -C $stage .; if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' } }
Write-Host "Native AOT package: $package"
