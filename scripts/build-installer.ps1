param(
    [Parameter(Mandatory = $true)][string]$SourceDirectory,
    [string]$OutputDirectory = '',
    [string]$CompilerPath = ''
)

$ErrorActionPreference = 'Stop'
if (!$IsWindows) { throw 'Windows installer packaging requires Windows.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$SourceDirectory = [IO.Path]::GetFullPath($SourceDirectory)
if (!$OutputDirectory) { $OutputDirectory = Join-Path $repoRoot 'artifacts' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$manifest = Get-Content -LiteralPath (Join-Path $SourceDirectory 'app-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.runtime -ne 'win-x64' -or $manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid native Windows distribution.' }
if (Test-Path -LiteralPath (Join-Path $SourceDirectory 'LanChat.Desktop.dll')) { throw 'A managed build cannot be packaged as Native AOT.' }
foreach ($file in $manifest.files) {
    if ($file.path -match '(^|[\\/])\.\.([\\/]|$)' -or [IO.Path]::IsPathRooted($file.path)) { throw 'Invalid distribution path.' }
    $path = Join-Path $SourceDirectory $file.path
    if ((Get-Item -LiteralPath $path).Length -ne $file.size -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw ('Distribution integrity failure: ' + $file.path) }
}
if (!$CompilerPath) {
    $candidates = @((Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe'), (Join-Path $env:ProgramFiles 'Inno Setup 7/ISCC.exe'))
    $CompilerPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (!$CompilerPath -and (Get-Command ISCC.exe -ErrorAction SilentlyContinue)) { $CompilerPath = (Get-Command ISCC.exe).Source }
}
if (!$CompilerPath -or !(Test-Path -LiteralPath $CompilerPath)) { throw 'Inno Setup compiler was not found. Use the GitHub Actions Windows runner or supply -CompilerPath.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
& $CompilerPath ('/DAppVersion=' + $manifest.version) ('/DSourceDirectory=' + $SourceDirectory) ('/DOutputDirectory=' + $OutputDirectory) (Join-Path $PSScriptRoot 'installer/feilv.iss')
if ($LASTEXITCODE -ne 0) { throw 'Windows installer compilation failed.' }
$installer = Join-Path $OutputDirectory ('feilv-v' + $manifest.version + '-win-x64-setup.exe')
if (!(Test-Path -LiteralPath $installer)) { throw 'Installer output was not found.' }
Write-Host "Wizard installer: $installer"
