param([Parameter(Mandatory = $true)][string]$InstallerPath)

$ErrorActionPreference = 'Stop'
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true') { throw 'Installer lifecycle checks run only on the disposable Windows CI runner.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$testRoot = Join-Path $repoRoot ('.build/installer-tests/' + [Guid]::NewGuid().ToString('N'))
$installed = Join-Path $testRoot '安装目录 with spaces'
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit(90000)) { throw "Process timed out: $Executable" }
    if ($process.ExitCode -ne 0) { throw "Process failed ($($process.ExitCode)): $Executable" }
}
function Wait-File([string]$Path) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $Path)) {
        if ($timer.Elapsed.TotalSeconds -gt 60) { throw "Timed out waiting for $Path" }
        Start-Sleep -Milliseconds 100
    }
}
$installArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/NOICONS', '/SP-', ('/DIR="' + $installed + '"'), ('/LOG="' + (Join-Path $testRoot 'install.log') + '"'))
Invoke-Checked $InstallerPath $installArguments
$executable = Join-Path $installed '飞驴.exe'
if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath (Join-Path $installed 'unins000.exe'))) { throw 'Installed program or uninstaller is missing.' }
Invoke-Checked $executable @('--verify-package')
$version = (Get-Content -LiteralPath (Join-Path $installed 'app-manifest.json') -Raw | ConvertFrom-Json).version
New-Item -ItemType Directory -Path (Join-Path $installed 'Data'),(Join-Path $installed 'Files/2026-10-27/Received') -Force | Out-Null
$config = '<LanChat version="1"><Profile><Id>0123456789abcdef0123456789abcdef</Id><Nickname>安装验证</Nickname><Theme>Dark</Theme><StartWithWindows>false</StartWithWindows><AutoDownloadUpdates>false</AutoDownloadUpdates></Profile><Network><ListenPort>0</ListenPort><DiscoveryPort>45831</DiscoveryPort><EnableDiscovery>false</EnableDiscovery></Network></LanChat>'
[IO.File]::WriteAllText((Join-Path $installed 'Data/config.xml'), $config, [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $installed 'Files/2026-10-27/Received/保留.txt'), 'keep received files', [Text.UTF8Encoding]::new($false))
Invoke-Checked $executable @('--smoke-test', '--no-discovery', '--port', '0')
$configurationHash = (Get-FileHash -LiteralPath (Join-Path $installed 'Data/config.xml') -Algorithm SHA256).Hash
# Same-version corruption must be repaired while identical native dependencies are skipped.
Add-Content -LiteralPath (Join-Path $installed 'libHarfBuzzSharp.dll') -Value 'corrupted dependency'
Invoke-Checked $InstallerPath $installArguments
if ((Get-Content -LiteralPath (Join-Path $testRoot 'install.log') -Raw) -notmatch 'Reusing unchanged file: libSkiaSharp.dll') { throw 'Reinstall did not reuse an identical native library.' }
if ((Get-FileHash -LiteralPath (Join-Path $installed 'Data/config.xml') -Algorithm SHA256).Hash -ne $configurationHash) { throw 'Reinstall changed user configuration.' }
Invoke-Checked $executable @('--verify-package')
Write-Host 'PASS: silent first install, native GUI launch and in-place reinstall preserve configuration and received files.'

# Run the real update helper from a stopped old application, then launch the installed Native AOT GUI.
$fixtureRoot = Join-Path $testRoot 'fixture'
dotnet build (Join-Path $repoRoot 'tests/LanChat.UpdateFixture/LanChat.UpdateFixture.csproj') -c Release -p:Platform=AnyCPU -o $fixtureRoot
if ($LASTEXITCODE -ne 0) { throw 'Cannot build installer transaction fixture.' }
$fixtureFiles = @(Get-ChildItem -LiteralPath $fixtureRoot -File)
foreach ($file in $fixtureFiles) {
    $name = if ($file.Name -eq 'LanChat.UpdateFixture.exe') { '飞驴.exe' } else { $file.Name }
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $installed $name) -Force
}
$oldFiles = @($fixtureFiles | ForEach-Object {
    $name = if ($_.Name -eq 'LanChat.UpdateFixture.exe') { '飞驴.exe' } else { $_.Name }
    @{ path = $name; size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$oldManifest = @{ version = '1.4.0'; runtime = 'win-x64'; executable = '飞驴.exe'; files = $oldFiles }
[IO.File]::WriteAllText((Join-Path $installed 'app-manifest.json'), ($oldManifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
Invoke-Checked $executable @('--installer-update', ('"' + $InstallerPath + '"'), $version, '--smoke-test', '--no-discovery', '--port', '0')
$resultFile = Join-Path $installed 'Data/Updates/installer-probe/result.txt'
$clock = [Diagnostics.Stopwatch]::StartNew()
while (!(Test-Path -LiteralPath $resultFile)) {
    if ($clock.Elapsed.TotalSeconds -gt 90) { throw 'Update helper did not complete.' }
    Start-Sleep -Milliseconds 200
}
if ((Get-Content -LiteralPath $resultFile -Raw) -ne '更新成功') { throw ('Installer update failed: ' + (Get-Content -LiteralPath $resultFile -Raw)) }
Invoke-Checked $executable @('--verify-package')
if ((Get-FileHash -LiteralPath (Join-Path $installed 'Data/config.xml') -Algorithm SHA256).Hash -ne $configurationHash) { throw 'Update changed configuration.' }
Write-Host 'PASS: confirmed installer update copies its helper, waits for exit, installs and receives Native AOT restart readiness.'
Get-Content -LiteralPath (Join-Path (Split-Path $resultFile) 'update-timing.log') | Write-Host

# Native smoke test exits after readiness; wait for its process before uninstalling.
Start-Sleep -Seconds 2

# Exercise the actual AOT helper entry point and its independent Avalonia progress window.
$nativeStage = Join-Path $installed 'Data/Updates/native-helper-probe'
$nativePayload = Join-Path $nativeStage 'payload'
$nativeHelper = Join-Path $nativeStage 'helper'
New-Item -ItemType Directory -Path $nativePayload,$nativeHelper -Force | Out-Null
$nativeManifest = Get-Content -LiteralPath (Join-Path $installed 'app-manifest.json') -Raw | ConvertFrom-Json
foreach ($file in $nativeManifest.files) {
    $destination = Join-Path $nativeHelper $file.path
    New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $installed $file.path) -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $installed 'app-manifest.json') -Destination $nativeHelper
$setupName = [IO.Path]::GetFileName($InstallerPath)
Copy-Item -LiteralPath $InstallerPath -Destination (Join-Path $nativePayload $setupName)
$setupInfo = Get-Item -LiteralPath $InstallerPath
$setupHash = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash.ToLowerInvariant()
$parentProbe = Join-Path $nativeStage 'parent-probe'
New-Item -ItemType Directory -Path $parentProbe -Force | Out-Null
[IO.File]::WriteAllText((Join-Path $parentProbe 'install.json'), '{}')
$parentReady = Join-Path $parentProbe 'app-ready'
$nativeParent = Start-Process -FilePath $executable -ArgumentList @('--no-discovery', '--port', '0', '--update-ready', ('"' + $parentReady + '"')) -PassThru -WindowStyle Hidden
$nativeWorker = $null
try {
    Wait-File $parentReady
    $nativePlan = @{
        package = @{
            release = @{ version = $version; runtime = 'win-x64'; assetName = $setupName; sha256 = $setupHash; size = $setupInfo.Length }
            payloadDirectory = $nativePayload
            manifest = @{ version = $version; runtime = 'win-x64'; executable = $setupName; files = @(@{ path = $setupName; size = $setupInfo.Length; sha256 = $setupHash }) }
        }
        targetExecutable = $executable; parentProcessId = $nativeParent.Id; parentStartTime = $nativeParent.StartTime.ToUniversalTime().Ticks
        arguments = @('--smoke-test', '--no-discovery', '--port', '0')
    }
    $nativePlanPath = Join-Path $nativeStage 'install.json'
    [IO.File]::WriteAllText($nativePlanPath, ($nativePlan | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
    $nativeWorker = Start-Process -FilePath (Join-Path $nativeHelper '飞驴.exe') -ArgumentList @('--apply-update', ('"' + $nativePlanPath + '"')) -PassThru -WindowStyle Hidden
    Wait-File (Join-Path $nativeStage 'helper-ready')
    [IO.File]::WriteAllText((Join-Path $nativeStage 'apply-approved'), 'approved')
    # Only this disposable test parent is stopped; the helper still waits for its real process handle.
    $nativeParent.Kill(); $nativeParent.WaitForExit()
    if (!$nativeWorker.WaitForExit(90000) -or $nativeWorker.ExitCode -ne 0) { throw 'Native progress helper did not complete.' }
    if ((Get-Content -LiteralPath (Join-Path $nativeStage 'result.txt') -Raw) -ne '更新成功') { throw 'Native progress helper did not receive restart readiness.' }
    Get-Content -LiteralPath (Join-Path $nativeStage 'update-timing.log') | Write-Host
    Write-Host 'PASS: the actual Native AOT helper opens its progress window, installs and restarts the client.'
} finally {
    if (!$nativeParent.HasExited) { $nativeParent.Kill(); $nativeParent.WaitForExit() }
    if ($nativeWorker -and !$nativeWorker.HasExited) { $nativeWorker.Kill(); $nativeWorker.WaitForExit() }
}
Invoke-Checked $executable @('--verify-package')
Start-Sleep -Seconds 2
Invoke-Checked (Join-Path $installed 'unins000.exe') @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', ('/LOG="' + (Join-Path $testRoot 'uninstall.log') + '"'))
if (Test-Path -LiteralPath $executable) { throw 'Uninstall left the application executable.' }
if (!(Test-Path -LiteralPath (Join-Path $installed 'Data/config.xml')) -or !(Test-Path -LiteralPath (Join-Path $installed 'Files/2026-10-27/Received/保留.txt'))) { throw 'Uninstall removed user data.' }
Write-Host 'PASS: uninstall removes program files and preserves local configuration, history and received files.'
