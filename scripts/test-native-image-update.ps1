param(
    [Parameter(Mandatory = $true)][string]$Package,
    [Parameter(Mandatory = $true)][string]$Runtime,
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$MacBundle = ''
)
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or (!$IsLinux -and !$IsMacOS)) { throw 'Native update lifecycle checks require a disposable GitHub Linux or macOS runner.' }
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$packagePath = [IO.Path]::GetFullPath($Package)
$root = Join-Path $repoRoot ('.build/image-update-test/' + [Guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'user data'; $staging = Join-Path $data 'Data/Updates/test'; $payload = Join-Path $staging 'payload'
New-Item -ItemType Directory -Path $payload -Force | Out-Null
$planPath = Join-Path $staging 'install.json'; $ready = Join-Path $staging 'app-ready'
[IO.File]::WriteAllText($planPath, '{}')
$originalDisplay = $env:DISPLAY; $originalExtract = $env:APPIMAGE_EXTRACT_AND_RUN
$xvfb = $null; $parentWrapper = $null; $parent = $null; $helper = $null
function Wait-Path([string]$Path, [int]$Seconds = 90) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $Path)) {
        if ($timer.Elapsed.TotalSeconds -gt $Seconds) { throw "Timed out waiting for $Path" }
        Start-Sleep -Milliseconds 100
    }
}
try {
    if ($IsLinux) {
        $display = ':' + (Get-Random -Minimum 100 -Maximum 900)
        $xvfb = Start-Process -FilePath 'Xvfb' -ArgumentList @($display, '-screen', '0', '1280x960x24', '-nolisten', 'tcp') -PassThru
        $env:DISPLAY = $display; $env:APPIMAGE_EXTRACT_AND_RUN = '1'
        Start-Sleep -Seconds 1
        if ($xvfb.HasExited) { throw 'Xvfb failed to start.' }
        $target = Join-Path $root 'Feilv installed.AppImage'
        Copy-Item -LiteralPath $packagePath -Destination $target
        & chmod +x $target
        $asset = [IO.Path]::GetFileName($packagePath)
        $helperPath = Join-Path $payload $asset
        Copy-Item -LiteralPath $packagePath -Destination $helperPath
        & chmod +x $helperPath
        $manifest = @{ version=$Version; runtime=$Runtime; executable=$asset; files=@(@{ path=$asset; size=(Get-Item -LiteralPath $packagePath).Length; sha256=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant() }) }
        [IO.File]::WriteAllText((Join-Path $payload 'app-manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        $parentWrapper = Start-Process -FilePath $target -ArgumentList @('--no-discovery', '--port', '0', '--data-dir', ('"' + $data + '"'), '--update-ready', ('"' + $ready + '"')) -PassThru
        Wait-Path $ready
        $candidates = @(Get-Process -Name 'feilv' -ErrorAction SilentlyContinue | Where-Object {
            try { ([IO.File]::ReadAllText("/proc/$($_.Id)/cmdline")).Contains($data) } catch { $false }
        })
        if ($candidates.Count -ne 1) { throw 'Cannot identify the running AppImage application process.' }
        $parent = $candidates[0]; $parentExecutable = $parent.MainModule.FileName
    } else {
        $targetBundle = Join-Path $root 'installed/Feilv.app'
        & ditto $MacBundle $targetBundle
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage the installed macOS application.' }
        $target = Join-Path $targetBundle 'Contents/MacOS/feilv'
        $bundle = Join-Path $staging 'bundle/Feilv.app'
        & ditto $MacBundle $bundle
        & ditto (Join-Path $bundle 'Contents/MacOS') $payload
        if ($LASTEXITCODE -ne 0) { throw 'Cannot stage the macOS update bundle.' }
        Copy-Item -LiteralPath $packagePath -Destination (Join-Path $staging 'package.download')
        $manifest = Get-Content -LiteralPath (Join-Path $payload 'app-manifest.json') -Raw | ConvertFrom-Json
        $helperPath = Join-Path $payload 'feilv'
        $parent = Start-Process -FilePath $target -ArgumentList @('--no-discovery', '--port', '0', '--data-dir', ('"' + $data + '"'), '--update-ready', ('"' + $ready + '"')) -PassThru
        Wait-Path $ready
        $parentExecutable = $target
    }
    $release = @{ version=$Version; runtime=$Runtime; assetName=[IO.Path]::GetFileName($packagePath); sha256=(Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant(); size=(Get-Item -LiteralPath $packagePath).Length; releaseNotes='' }
    $plan = @{ package=@{ release=$release; payloadDirectory=$payload; manifest=$manifest }; targetExecutable=$target; parentExecutable=$parentExecutable;
        parentProcessId=$parent.Id; parentStartTime=$parent.StartTime.ToUniversalTime().Ticks; arguments=@('--smoke-test','--no-discovery','--port','0','--data-dir',$data); appImageExtractAndRun=$(if ($IsLinux) { '1' } else { $null }) }
    [IO.File]::WriteAllText($planPath, ($plan | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
    $helper = Start-Process -FilePath $helperPath -ArgumentList @('--apply-update', ('"' + $planPath + '"')) -PassThru
    Wait-Path (Join-Path $staging 'helper-ready')
    [IO.File]::WriteAllText((Join-Path $staging 'apply-approved'), 'approved')
    # Only the app process started above on this disposable runner is stopped.
    $parent.Kill(); $parent.WaitForExit()
    if (!$helper.WaitForExit(120000) -or $helper.ExitCode -ne 0) {
        if (Test-Path -LiteralPath (Join-Path $staging 'result.txt')) { Get-Content -LiteralPath (Join-Path $staging 'result.txt') | Write-Host }
        throw 'Native image update helper failed.'
    }
    if ((Get-Content -LiteralPath (Join-Path $staging 'result.txt') -Raw) -ne '更新成功' -or !(Test-Path -LiteralPath $ready)) { throw 'Native image update did not receive restart readiness.' }
    if ($IsLinux -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash) { throw 'AppImage was not replaced with the verified update.' }
    if ($IsMacOS) {
        & codesign --verify --deep --strict $targetBundle
        if ($LASTEXITCODE -ne 0) { throw 'Updated macOS bundle failed signature verification.' }
    }
    if (!(Test-Path -LiteralPath (Join-Path $data 'Data/config.xml'))) { throw 'Update lost the user configuration.' }
    Write-Host "PASS: $Runtime native image helper waits for exit, replaces the app and receives restart readiness while preserving user data."
} finally {
    foreach ($process in @($helper,$parent,$parentWrapper,$xvfb)) {
        if ($process -and !$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    }
    $env:DISPLAY = $originalDisplay; $env:APPIMAGE_EXTRACT_AND_RUN = $originalExtract
}
