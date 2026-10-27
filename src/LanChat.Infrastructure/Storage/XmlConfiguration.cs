using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using LanChat.Core.Models;
using LanChat.Core.Localization;

namespace LanChat.Infrastructure.Storage;

public sealed class XmlConfiguration
{
    public UserProfile Profile { get; set; } = new();
    public List<SharedFolder> Shares { get; set; } = [];
    public int ListenPort { get; set; } = 45832;
    public int DiscoveryPort { get; set; } = 45831;
    public bool EnableDiscovery { get; set; } = true;

    public static XmlConfiguration Load(string baseDirectory)
    {
        var path = AppPaths.Configuration(baseDirectory);
        if (!File.Exists(path)) return new XmlConfiguration();
        XDocument document;
        using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
            document = XDocument.Load(reader);
        var root = document.Element("LanChat") ?? throw new InvalidDataException("config.xml 缺少 LanChat 根节点。");
        var profile = root.Element("Profile");
        var network = root.Element("Network");
        var qr = root.Element("QrHttp");
        string Value(XElement? element, string name, string fallback) => element?.Element(name)?.Value ?? fallback;
        int Port(string name, int fallback, bool allowZero) => int.TryParse(Value(network, name, fallback.ToString(CultureInfo.InvariantCulture)), out var port) && port >= (allowZero ? 0 : 1) && port <= 65535 ? port : fallback;
        var result = new XmlConfiguration
        {
            Profile = new UserProfile
            {
                Id = Value(profile, "Id", Guid.NewGuid().ToString("N")),
                Nickname = Value(profile, "Nickname", Environment.UserName),
                AvatarPath = profile?.Element("AvatarPath")?.Value is { Length: > 0 } avatar ? Path.GetFullPath(Path.Combine(baseDirectory, avatar)) : null,
                Theme = Value(profile, "Theme", "System"),
                Language = Localizer.Normalize(Value(profile, "Language", "System")),
                ShowNotifications = Value(profile, "ShowNotifications", "true") != "false",
                AutoReceive = Value(profile, "AutoReceive", "true") != "false",
                StartWithWindows = Value(profile, "StartWithWindows", "true") != "false",
                AutoDownloadUpdates = Value(profile, "AutoDownloadUpdates", "true") != "false",
                QrUploadDirectory = ResolveOptionalPath(baseDirectory, Value(qr, "UploadDirectory", "")),
                QrDownloadDirectory = ResolveOptionalPath(baseDirectory, Value(qr, "DownloadDirectory", ""))
            },
            ListenPort = Port("ListenPort", 45832, true),
            DiscoveryPort = Port("DiscoveryPort", 45831, false),
            EnableDiscovery = Value(network, "EnableDiscovery", "true") != "false"
        };
        if (!Guid.TryParseExact(result.Profile.Id, "N", out _) || string.IsNullOrWhiteSpace(result.Profile.Nickname) || result.Profile.Nickname.Length > 40)
            throw new InvalidDataException("config.xml 中的设备标识或昵称无效。");
        foreach (var folder in root.Element("SharedFolders")?.Elements("Folder") ?? [])
        {
            var id = folder.Attribute("id")?.Value;
            var localPath = folder.Element("Path")?.Value;
            if (id is not null && localPath is not null)
                result.Shares.Add(new SharedFolder(id, folder.Element("Name")?.Value ?? new DirectoryInfo(localPath).Name, localPath));
        }
        return result;
    }

    public void Save(string baseDirectory)
    {
        var path = AppPaths.Configuration(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        CreateDocument(baseDirectory).Save(temporary);
        File.Move(temporary, path, true);
    }

    public async Task SaveAsync(string baseDirectory, CancellationToken ct = default)
    {
        var path = AppPaths.Configuration(baseDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
            await CreateDocument(baseDirectory).SaveAsync(stream, SaveOptions.None, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, path, true);
    }

    private XDocument CreateDocument(string baseDirectory) => new(new XDeclaration("1.0", "utf-8", null),
            new XElement("LanChat", new XAttribute("version", "1"),
                new XElement("Profile", new XElement("Id", Profile.Id), new XElement("Nickname", Profile.Nickname),
                    new XElement("AvatarPath", Profile.AvatarPath is { } avatar ? Path.GetRelativePath(baseDirectory, avatar) : ""),
                    new XElement("Theme", Profile.Theme), new XElement("Language", Localizer.Normalize(Profile.Language)), new XElement("ShowNotifications", Profile.ShowNotifications),
                    new XElement("AutoReceive", Profile.AutoReceive),
                    new XElement("StartWithWindows", Profile.StartWithWindows), new XElement("AutoDownloadUpdates", Profile.AutoDownloadUpdates)),
                new XElement("Network", new XElement("ListenPort", ListenPort), new XElement("DiscoveryPort", DiscoveryPort), new XElement("EnableDiscovery", EnableDiscovery)),
                new XElement("QrHttp", new XElement("UploadDirectory", StoreOptionalPath(baseDirectory, Profile.QrUploadDirectory)), new XElement("DownloadDirectory", StoreOptionalPath(baseDirectory, Profile.QrDownloadDirectory))),
                new XElement("SharedFolders", Shares.Select(x => new XElement("Folder", new XAttribute("id", x.Id), new XElement("Name", x.Name), new XElement("Path", x.LocalPath))))));

    private static string ResolveOptionalPath(string root, string value) => string.IsNullOrWhiteSpace(value) ? "" : Path.GetFullPath(Path.Combine(root, value));
    private static string StoreOptionalPath(string root, string value) => string.IsNullOrWhiteSpace(value) ? "" : Path.GetRelativePath(root, value);
}
