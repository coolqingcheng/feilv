using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using LanChat.Core.Localization;
using LanChat.Core.Models;
using LanChat.Desktop.Controls;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunThemeToggleTests(MainWindow window, MainWindowViewModel model, IndexedFileStore store)
    {
        var button = window.FindControl<Button>("ThemeToggleButton")!;
        var icon = window.FindControl<SvgIcon>("ThemeToggleIcon")!;
        var settings = window.FindControl<Button>("MainMenuButton")!;
        var heading = window.FindControl<TextBlock>("PageHeading")!;
        var qr = window.FindControl<Button>("QrShareButton")!;
        var originalPage = model.Page;
        Pump(model.Settings.SetThemeCommand.ExecuteAsync("System")); Layout();
        var target = window.ActualThemeVariant == ThemeVariant.Dark ? "Light" : "Dark";
        Check(button.CommandParameter?.ToString() == target && icon.Kind == (target == "Light" ? "sun" : "moon"),
            "theme shortcut derives its icon and target from the actual system appearance");
        Click();
        Check(model.Settings.Theme == target && XmlConfiguration.Load(store.DataDirectory).Profile.Theme == target
            && Application.Current!.RequestedThemeVariant == (target == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light) && model.Page == originalPage,
            "clicking the theme icon immediately switches and persists an explicit theme without navigating");
        Click();
        Check(model.Settings.Theme != target && icon.Kind == (model.Settings.Theme == "Dark" ? "sun" : "moon")
            && XmlConfiguration.Load(store.DataDirectory).Profile.Theme == model.Settings.Theme, "a second click switches back and refreshes the SVG destination icon");

        model.Inbox.BeginSendFiles([]); Layout(); var picker = model.Inbox.IsPickerOpen;
        Click();
        Check(picker && model.Inbox.IsPickerOpen, "switching appearance keeps an open recipient picker and its selection intact");
        model.Inbox.CancelPicker();
        var saved = model.Settings.Theme;
        using (var lockedConfig = new FileStream(AppPaths.Configuration(store.DataDirectory), FileMode.Open, FileAccess.Read, FileShare.None)) Click();
        Check(model.Settings.Theme == saved && model.Status.StartsWith("主题保存失败：", StringComparison.Ordinal)
            && window.ActualThemeVariant == (saved == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light)
            && XmlConfiguration.Load(store.DataDirectory).Profile.Theme == saved, "a configuration write failure restores the previous appearance and saved preference");

        foreach (var language in new[] { "zh-CN", "en-US" })
        {
            Localizer.SetPreference(language); Layout();
            Check(AutomationProperties.GetName(button) == (language == "zh-CN" ? "切换亮暗主题" : "Toggle light/dark theme"),
                language + " theme shortcut has a localized accessible name");
            foreach (var width in new[] { 360d, 420d })
            foreach (var page in new[] { "Files", "Settings", "Shares", "Qr" })
            {
                window.Width = width; model.Page = page; Layout();
                var headingEnd = heading.TranslatePoint(new Point(heading.Bounds.Width, 0), window)!.Value.X;
                var toolbarStart = qr.TranslatePoint(default, window)!.Value.X;
                var buttonEnd = button.TranslatePoint(new Point(button.Bounds.Width, 0), window)!.Value.X;
                var settingsStart = settings.TranslatePoint(default, window)!.Value.X;
                Check(heading.Bounds.Width > 0 && headingEnd <= toolbarStart && Math.Abs(buttonEnd - settingsStart) < 1,
                    $"{language} {page} heading fits beside the theme and settings buttons at {width}px");
            }
        }
        Localizer.SetPreference("System"); window.Width = 420; model.Page = originalPage;
        Pump(model.Settings.SetThemeCommand.ExecuteAsync("System")); Layout();
        Check(Application.Current!.RequestedThemeVariant == ThemeVariant.Default && XmlConfiguration.Load(store.DataDirectory).Profile.Theme == "System",
            "settings can restore system appearance after using the title-bar shortcut");

        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Pump(Task.Delay(20)); window.UpdateLayout(); }
        void Click()
        {
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Dispatcher.UIThread.RunJobs();
            if (model.Settings.SetThemeCommand.ExecutionTask is { } changing) Pump(changing);
            Layout();
        }
    }
}
