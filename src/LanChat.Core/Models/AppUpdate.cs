using System.Runtime.InteropServices;

namespace LanChat.Core.Models;

public static class AppReleases
{
    public const string Repository = "coolqingcheng/feilv";
    public const string HomeUrl = "https://github.com/" + Repository;
    public static string CurrentVersion { get; } = typeof(AppReleases).Assembly.GetName().Version!.ToString(3);
    public static string Runtime => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string AssetName(string version, string runtime) => runtime switch
    {
        "win-x64" => $"feilv-v{version}-{runtime}-setup.exe",
        "osx-arm64" => $"feilv-v{version}-{runtime}.dmg",
        "linux-x64" or "linux-arm64" => $"feilv-v{version}-{runtime}.AppImage",
        _ => ArchiveAssetName(version, runtime)
    };
    public static string ArchiveAssetName(string version, string runtime) => $"feilv-v{version}-{runtime}" + (runtime.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
    public static bool IsInstaller(AppRelease release) => release.Runtime == "win-x64" && release.AssetName == AssetName(release.Version, release.Runtime);
    public static bool IsAppImage(AppRelease release) => release.Runtime is "linux-x64" or "linux-arm64" && release.AssetName == AssetName(release.Version, release.Runtime);
    public static bool IsDiskImage(AppRelease release) => release.Runtime == "osx-arm64" && release.AssetName == AssetName(release.Version, release.Runtime);
    public static bool IsSingleFilePackage(AppRelease release) => IsInstaller(release) || IsAppImage(release);
    public static bool SamePackage(AppRelease first, AppRelease second) => first.Version == second.Version && first.Runtime == second.Runtime
        && first.AssetName == second.AssetName && first.Sha256 == second.Sha256 && first.Size == second.Size;
}

public sealed record AppRelease(string Version, string Runtime, string AssetName, string Sha256, long Size)
{
    public string ReleaseNotes { get; init; } = "";
}
public sealed record PackageFile(string Path, long Size, string Sha256);
public sealed record PackageManifest(string Version, string Runtime, string Executable, List<PackageFile> Files);
public sealed record PreparedAppUpdate(AppRelease Release, string PayloadDirectory, PackageManifest Manifest);
public sealed record UpdateDownloadProgress(long Received, long Total, double BytesPerSecond);
