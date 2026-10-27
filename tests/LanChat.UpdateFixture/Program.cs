using LanChat.Infrastructure.Updates;
using LanChat.Core.Models;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

if (args is ["--apply-update", var plan]) return await UpdateInstaller.RunHelperAsync(plan);
if (args.Length >= 3 && args[0] == "--installer-update")
{
    var installer = args[1]; var version = args[2];
    var runtime = AppReleases.Runtime;
    var payload = Path.Combine(AppContext.BaseDirectory, "Data", "Updates", "installer-probe", "payload");
    Directory.CreateDirectory(payload);
    var name = AppReleases.AssetName(version, runtime);
    File.Copy(installer, Path.Combine(payload, name));
    string sha;
    await using (var input = File.OpenRead(installer)) sha = Convert.ToHexString(await SHA256.HashDataAsync(input)).ToLowerInvariant();
    var release = new AppRelease(version, runtime, name, sha, new FileInfo(installer).Length);
    var manifest = new PackageManifest(version, runtime, name, [new(name, release.Size, sha)]);
    await File.WriteAllTextAsync(Path.Combine(payload, "app-manifest.json"), JsonSerializer.Serialize(manifest, FixtureJsonContext.Default.PackageManifest));
    await UpdateInstaller.StartAsync(new(release, payload, manifest));
    return 0;
}
var readyIndex = Array.IndexOf(args, "--update-ready");
if (readyIndex >= 0) { UpdateInstaller.SignalReady(args[readyIndex + 1]); return 0; }
if (args is ["--parent", var stop])
{
    await File.WriteAllTextAsync(stop + ".started", "started");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
    while (!File.Exists(stop)) await Task.Delay(50, timeout.Token);
    return 0;
}
return 2;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PackageManifest))]
internal partial class FixtureJsonContext : JsonSerializerContext;
