using System.Runtime.InteropServices;

namespace LanChat.Core.Models;

public static class AppReleases
{
    public const string Repository = "coolqingcheng/feilv";
    public const string HomeUrl = "https://github.com/" + Repository;
    public static string Runtime => (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux")
        + "-" + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
    public static string AssetName(string version, string runtime) => runtime.StartsWith("win-", StringComparison.Ordinal)
        ? $"feilv-v{version}-{runtime}-setup.exe" : ArchiveAssetName(version, runtime);
    public static string ArchiveAssetName(string version, string runtime) => $"feilv-v{version}-{runtime}" + (runtime.StartsWith("win-", StringComparison.Ordinal) ? ".zip" : ".tar.gz");
    public static bool IsInstaller(AppRelease release) => release.Runtime.StartsWith("win-", StringComparison.Ordinal) && release.AssetName == AssetName(release.Version, release.Runtime);
}

public sealed record AppRelease(string Version, string Runtime, string AssetName, string Sha256, long Size);
public sealed record PackageFile(string Path, long Size, string Sha256);
public sealed record PackageManifest(string Version, string Runtime, string Executable, List<PackageFile> Files);
public sealed record PreparedAppUpdate(AppRelease Release, string PayloadDirectory, PackageManifest Manifest);
public sealed record UpdateDownloadProgress(long Received, long Total, double BytesPerSecond);
