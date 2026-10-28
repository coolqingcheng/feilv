using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using LanChat.Core.Localization;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunProfileReminderTests()
    {
        var now = DateTimeOffset.UtcNow;
        var profile = new UserProfile { Nickname = "default-user" };
        Check(ProfileReminderViewModel.IsDue(profile, now, "default-user"), "default device name prompts on first use");
        profile.ProfileReminderAt = now;
        Check(!ProfileReminderViewModel.IsDue(profile, now.AddDays(6), "default-user")
            && ProfileReminderViewModel.IsDue(profile, now.AddDays(7), "default-user"), "default name reminder waits seven days");
        profile.Nickname = "Alice";
        Check(!ProfileReminderViewModel.IsDue(profile, now.AddDays(30), "default-user"), "custom names are not prompted again");
        profile.Nickname = "default-user"; profile.ProfileReminderAt = DateTimeOffset.MaxValue;
        Check(!ProfileReminderViewModel.IsDue(profile, now, "default-user"), "a future reminder timestamp does not cause a crash or repeated prompts");

        var store = new IndexedFileStore(Path.Combine(TestRoot, "profile-reminder"));
        profile.ProfileReminderAt = null;
        var lan = new LanService(store, profile, new NodeOptions { ListenPort = 0, EnableDiscovery = false });
        Pump(lan.StartAsync());
        var ui = new TestUiServices();
        using var vm = new ProfileReminderViewModel(lan, store, ui);
        Pump(vm.MarkShownAsync(now));
        var loaded = XmlConfiguration.Load(store.DataDirectory).Profile;
        Check(loaded.ProfileReminderAt == now && !ProfileReminderViewModel.IsDue(loaded, now.AddDays(1), "default-user"), "reminder date persists across restarts");
        var imagePath = Path.Combine(TestRoot, "reminder-avatar.png");
        using (var bitmap = new WriteableBitmap(new PixelSize(32, 32), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque)) bitmap.Save(imagePath);
        ui.NextFile = imagePath;
        Pump(vm.ChooseAvatarCommand.ExecuteAsync(null));
        Check(vm.Avatar is not null && profile.AvatarPath is null, "avatar selection is only a preview until confirmation");
        vm.Nickname = " ";
        Pump(vm.SaveCommand.ExecuteAsync(null));
        Check(vm.HasError && profile.Nickname == "default-user" && profile.AvatarPath is null, "invalid name leaves the profile unchanged");
        vm.Nickname = "Alice"; var saved = false; vm.Saved += () => saved = true;
        Pump(vm.SaveCommand.ExecuteAsync(null));
        loaded = XmlConfiguration.Load(store.DataDirectory).Profile;
        Check(saved && loaded.Nickname == "Alice" && loaded.AvatarPath is { } path && File.Exists(path), "confirmation saves the name and selected avatar");
        var settings = new SettingsViewModel(lan, store, ui, _ => { });
        settings.RefreshProfile();
        Check(settings.Nickname == "Alice" && settings.Avatar is not null, "settings reflect the confirmed profile");
        settings.Dispose();
        var previousLanguage = Localizer.Preference;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            Application.Current!.RequestedThemeVariant = theme;
            var window = new ProfileReminderWindow { DataContext = vm };
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Check(window.FindControl<Button>("LaterButton") is not null && window.FindControl<Button>("ConfirmButton") is not null
                && window.FindControl<TextBox>("NicknameBox") is not null, "profile prompt exposes name, confirm and later controls in " + theme);
            Render(window, "profile-reminder-" + theme + ".png");
            window.Close();
        }
        Localizer.SetPreference("en-US");
        var englishWindow = new ProfileReminderWindow { DataContext = vm };
        englishWindow.Show(); Dispatcher.UIThread.RunJobs(); englishWindow.UpdateLayout();
        Check((string?)englishWindow.FindControl<Button>("LaterButton")?.Content == "Maybe later", "profile prompt switches to English");
        Render(englishWindow, "profile-reminder-English.png");
        englishWindow.Close(); Localizer.SetPreference(previousLanguage);
        Application.Current!.RequestedThemeVariant = ThemeVariant.Default;
        Pump(lan.DisposeAsync().AsTask());
    }
}
