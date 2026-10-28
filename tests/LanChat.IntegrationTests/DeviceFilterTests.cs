using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Core.Localization;
using LanChat.Core.Models;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Networking;
using LanChat.Infrastructure.Storage;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunDeviceFilterTests()
    {
        var store = new IndexedFileStore(Path.Combine(TestRoot, "device-filter"));
        var node = new LanService(store, new UserProfile { StartWithWindows = false }, new NodeOptions { EnableDiscovery = false });
        var ui = new TestUiServices();
        using var model = new MainWindowViewModel(node, store, ui);
        var alice = Device("Alice Workstation", "192.168.1.10");
        var bob = Device("研发电脑", "10.20.30.40");
        var cara = Device("Cara Laptop", "192.168.1.33");
        model.Inbox.UpdatePeer(alice); model.Inbox.UpdatePeer(bob); model.Inbox.UpdatePeer(cara);
        var window = new MainWindow { DataContext = model }; window.Show();
        try
        {
            Layout();
            var view = window.GetVisualDescendants().OfType<FileInboxView>().Single();
            var devices = view.FindControl<ItemsControl>("OnlineDeviceList")!;
            var search = view.FindControl<TextBox>("DeviceSearch")!;
            var clear = view.FindControl<Button>("ClearDeviceSearchButton")!;
            var empty = view.FindControl<StackPanel>("NoMatchingDevices")!;
            Check(devices.ItemsSource == model.Inbox.VisibleDevices && model.Inbox.VisibleDevices.SequenceEqual(new[] { alice, bob, cara })
                && !clear.IsVisible, "online device filter initially shows every device in the original order");
            search.Text = "  ALICE  "; Layout();
            Check(model.Inbox.VisibleDevices.SequenceEqual(new[] { alice }) && clear.IsEffectivelyVisible
                && model.Inbox.Recipients.Count == 3, "device name search ignores case and surrounding whitespace without changing recipients");
            search.Text = "20.30"; Layout();
            Check(model.Inbox.VisibleDevices.SequenceEqual(new[] { bob }), "partial IP address search matches the correct online device");
            bob.IsRecipientSelected = true;
            model.Inbox.BeginSendFiles([]); Layout();
            Check(model.Inbox.Recipients.Count == 3 && bob.IsRecipientSelected && model.Inbox.CanConfirm
                && view.FindControl<ItemsControl>("RecipientList")!.ItemsSource == model.Inbox.Recipients,
                "filtering the online view preserves the complete recipient picker and checked devices");
            model.Inbox.CancelPicker();
            bob.Update(bob.Model with { Address = "10.20.31.40" }); Layout();
            Check(model.Inbox.VisibleDevices.Count == 0 && empty.IsEffectivelyVisible && !model.Inbox.NoRecipients,
                "changing an online device IP immediately removes it from results and shows a distinct no-match state");
            alice.Update(alice.Model with { Address = "10.20.30.8" }); Layout();
            Check(model.Inbox.VisibleDevices.SequenceEqual(new[] { alice }) && !empty.IsVisible, "device address changes can immediately add a matching result");
            alice.Update(alice.Model with { IsOnline = false }); model.Inbox.UpdatePeer(alice); Layout();
            Check(model.Inbox.VisibleDevices.Count == 0 && model.Inbox.Recipients.Count == 2, "offline devices disappear from filtered results and the recipient list");
            alice.Update(alice.Model with { IsOnline = true }); model.Inbox.UpdatePeer(alice); Layout();
            Check(model.Inbox.VisibleDevices.SequenceEqual(new[] { alice }), "returning devices are immediately re-evaluated against the active filter");
            search.Text = "设计"; Layout();
            cara.Update(cara.Model with { Nickname = "设计工作站" }); Layout();
            Check(model.Inbox.VisibleDevices.SequenceEqual(new[] { cara }), "renaming a device updates the active search without reopening the view");
            Localizer.SetPreference("en-US"); Layout();
            Check(search.Watermark == "Search device name or IP" && AutomationProperties.GetName(clear) == "Clear device filter", "search placeholder and clear control switch to English live");
            var point = clear.TranslatePoint(new Point(clear.Bounds.Width / 2, clear.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Layout();
            Check(model.Inbox.DeviceSearch == "" && !clear.IsVisible && model.Inbox.VisibleDevices.SequenceEqual(model.Inbox.Recipients),
                "clicking the clear cross restores all online devices and hides the cross");
            search.Text = "  "; Layout();
            Check(model.Inbox.VisibleDevices.Count == 3 && !empty.IsVisible && clear.IsVisible, "whitespace-only search shows all devices and can still be cleared");
            model.Inbox.ClearDeviceSearchCommand.Execute(null);
            foreach (var theme in new[] { "Dark", "Light" })
            foreach (var width in new[] { 360d, 420d })
            {
                Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme)); window.Width = width; Layout();
                Check(view.FindControl<ScrollViewer>("DeviceScroll")!.Extent.Width <= view.FindControl<ScrollViewer>("DeviceScroll")!.Viewport.Width + 1
                    && search.Bounds.Width > 0 && search.TranslatePoint(new Point(search.Bounds.Width, 0), window)!.Value.X <= window.Bounds.Width,
                    $"{theme} online search and rows fit at {width}px");
                Render(window, $"device-filter-{theme.ToLowerInvariant()}-{width}.png");
            }
            search.Text = "unmatched";
            foreach (var peer in new[] { alice, bob, cara }) { peer.Update(peer.Model with { IsOnline = false }); model.Inbox.UpdatePeer(peer); }
            Layout();
            Check(model.Inbox.NoRecipients && !model.Inbox.NoMatchingDevices && !empty.IsVisible, "no online devices shows the original discovery state even with a search term");
        }
        finally
        {
            Localizer.SetPreference("System"); model.Dispose();
            window.AllowClose = true; window.Close();
            alice.Dispose(); bob.Dispose(); cara.Dispose(); Pump(node.DisposeAsync().AsTask());
        }

        static PeerViewModel Device(string name, string address) => new(new Peer { Id = Guid.NewGuid().ToString("N"), Nickname = name,
            Address = address, Port = 45832, Fingerprint = new string('A', 64) });
        void Layout() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Pump(Task.Delay(20)); window.UpdateLayout(); }
    }
}
