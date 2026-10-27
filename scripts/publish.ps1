param(
    [ValidateSet('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')]
    [string]$Runtime = 'win-x64'
)

# Build on the target OS with its native linker. GitHub Actions provides these toolchains.
& (Join-Path $PSScriptRoot 'publish-release.ps1') -Runtime $Runtime
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
