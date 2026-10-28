using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LanChat.Core.Models;
using LanChat.Core.Services;
using LanChat.Desktop.Services;

namespace LanChat.Desktop.ViewModels;

public partial class ProfileReminderViewModel : ViewModelBase, IDisposable
{
    private readonly ILanService _lan;
    private readonly ILocalStore _store;
    private readonly IUiServices _ui;
    private bool _avatarChanged;
    [ObservableProperty] private string _nickname;
    [ObservableProperty] private Bitmap? _avatar;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] private bool _isBusy;
    public string Initial => string.IsNullOrWhiteSpace(Nickname) ? "?" : Nickname.Trim()[..1].ToUpperInvariant();
    public bool HasError => Error.Length > 0;
    public event Action? Saved;

    public ProfileReminderViewModel(ILanService lan, ILocalStore store, IUiServices ui)
    {
        _lan = lan; _store = store; _ui = ui;
        _nickname = lan.Profile.Nickname;
        _avatar = ImageLoader.Load(lan.Profile.AvatarPath, 192);
    }

    public static bool IsDue(UserProfile profile, DateTimeOffset now, string defaultName) =>
        string.Equals(profile.Nickname.Trim(), defaultName.Trim(), StringComparison.OrdinalIgnoreCase)
        && (profile.ProfileReminderAt is null || now - profile.ProfileReminderAt.Value >= TimeSpan.FromDays(7));

    public async Task MarkShownAsync(DateTimeOffset now)
    {
        var previous = _lan.Profile.ProfileReminderAt;
        _lan.Profile.ProfileReminderAt = now;
        try { await _store.SaveProfileAsync(_lan.Profile); }
        catch { _lan.Profile.ProfileReminderAt = previous; throw; }
    }

    partial void OnNicknameChanged(string value) => OnPropertyChanged(nameof(Initial));

    [RelayCommand]
    private async Task ChooseAvatarAsync()
    {
        string? path;
        try { path = await _ui.PickFileAsync(true); }
        catch (Exception ex) { Error = "头像更新失败：" + ex.Message; return; }
        if (path is null) return;
        var image = ImageLoader.Load(path, 192);
        if (image is null) { Error = "无法读取这张图片"; return; }
        Avatar?.Dispose(); Avatar = image; _avatarChanged = true; Error = "";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var name = Nickname.Trim();
        if (name.Length is < 1 or > 40) { Error = "设备名称应为 1–40 个字符"; return; }
        IsBusy = true;
        var previousName = _lan.Profile.Nickname;
        var previousAvatar = _lan.Profile.AvatarPath;
        string? target = null;
        try
        {
            if (_avatarChanged && Avatar is not null)
            {
                var folder = AppPaths.DailyMedia(_store.DataDirectory, true, "Avatars");
                Directory.CreateDirectory(folder);
                target = Path.Combine(folder, "self-" + Guid.NewGuid().ToString("N") + ".png");
                Avatar.Save(target);
                if (new FileInfo(target).Length > 131072) throw new IOException("头像过大，请选择较小的图片");
                _lan.Profile.AvatarPath = target;
            }
            _lan.Profile.Nickname = name;
            await _lan.UpdateProfileAsync();
            Error = "";
            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            _lan.Profile.Nickname = previousName; _lan.Profile.AvatarPath = previousAvatar;
            if (target is not null) try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            Error = "保存失败：" + ex.Message;
        }
        finally { IsBusy = false; }
    }

    public void Dispose() { Avatar?.Dispose(); Avatar = null; }
}
