using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanChat.Desktop.Controls;
using LanChat.Desktop.ViewModels;
using LanChat.Desktop.Views;
using LanChat.Infrastructure.Storage;
using ZXing;
using ZXing.Common;
using ZXing.QrCode;

namespace LanChat.IntegrationTests;

internal static partial class Program
{
    private static void RunQrShareUiTests(MainWindow window, MainWindowViewModel model, IndexedFileStore store, TestUiServices ui)
    {
        var button = window.FindControl<Button>("QrShareButton")!;
        var settings = window.FindControl<Button>("MainMenuButton")!;
        Check(button.Command == model.OpenQrShareCommand && button.TranslatePoint(default, window)!.Value.X < settings.TranslatePoint(default, window)!.Value.X,
            "QR sharing button sits beside settings and binds its start action");
        Pump(model.OpenQrShareCommand.ExecuteAsync(null)); Layout();
        Check(model.IsQr && model.QrShare.IsRunning && model.QrShare.Url.StartsWith("http://", StringComparison.Ordinal)
            && model.QrShare.EffectiveUploadDirectory == Path.Combine(store.DataDirectory, "qr-http")
            && model.QrShare.EffectiveDownloadDirectory == Path.Combine(store.DataDirectory, "qr-http"), "QR button starts HTTP sharing and both unset roots default to the application qr-http folder");
        var url = model.QrShare.Url;
        Pump(model.OpenQrShareCommand.ExecuteAsync(null));
        Check(model.QrShare.Url == url, "reopening the QR page reuses the current server and QR link");
        Pump(model.QrShare.CopyLinkCommand.ExecuteAsync(null)); model.QrShare.OpenBrowserCommand.Execute(null);
        Check(ui.CopiedText == url && ui.LastOpened == url, "QR view can copy the exact link and open it in the browser");
        var view = window.GetVisualDescendants().OfType<QrShareView>().Single();
        var code = view.FindControl<QrCode>("ShareQrCode")!;
        foreach (var theme in new[] { "Dark", "Light" })
        {
            Pump(model.Settings.SetThemeCommand.ExecuteAsync(theme));
            foreach (var width in new[] { 420, 360 })
            {
                window.Width = width; var scroll = view.FindControl<ScrollViewer>("QrScroll")!; scroll.ScrollToHome(); Layout();
                Check(scroll.Extent.Width <= scroll.Viewport.Width + 1, theme + " QR sharing page fits " + width + " px without horizontal overflow");
                Render(window, $"qr-share-{theme.ToLowerInvariant()}-{width}.png");
                using var frame = window.CaptureRenderedFrame()!;
                var point = code.TranslatePoint(default, window)!.Value;
                var scale = window.RenderScaling;
                var rect = new PixelRect((int)(point.X * scale), (int)(point.Y * scale), (int)(code.Bounds.Width * scale), (int)(code.Bounds.Height * scale));
                var pixels = new byte[rect.Width * rect.Height * 4];
                var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
                try { frame.CopyPixels(rect, pin.AddrOfPinnedObject(), pixels.Length, rect.Width * 4); } finally { pin.Free(); }
                var decoded = new QRCodeReader().decode(new BinaryBitmap(new HybridBinarizer(new RGBLuminanceSource(pixels, rect.Width, rect.Height, RGBLuminanceSource.BitmapFormat.BGRA32))));
                Check(decoded?.Text == url, "independent QR decoder reads the live HTTP link from the " + theme + " screenshot at " + width + " px");
                scroll.ScrollToEnd(); Layout(); Render(window, $"qr-share-directories-{theme.ToLowerInvariant()}-{width}.png");
            }
        }
        var upload = Path.Combine(TestRoot, "qr-ui-upload"); var download = Path.Combine(TestRoot, "qr-ui-download");
        ui.NextFolder = upload; Pump(model.QrShare.ChooseUploadCommand.ExecuteAsync(null));
        ui.NextFolder = download; Pump(model.QrShare.ChooseDownloadCommand.ExecuteAsync(null));
        Pump(model.QrShare.ApplyDirectoriesCommand.ExecuteAsync(null)); Layout();
        var profile = XmlConfiguration.Load(store.DataDirectory).Profile;
        Check(profile.QrUploadDirectory == upload && profile.QrDownloadDirectory == download && model.QrShare.IsRunning && model.QrShare.Url != url,
            "separate folder pickers persist both roots and restart sharing with a new QR link");
        Pump(model.QrShare.StopCommand.ExecuteAsync(null)); Layout();
        Check(!model.QrShare.IsRunning && model.QrShare.Url == "", "stopping QR sharing removes the live URL and QR content");
        window.Width = 420; model.Page = "Files"; Layout();

        void Layout() { window.UpdateLayout(); Pump(Task.Delay(40)); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); window.UpdateLayout(); Pump(Task.Delay(20)); }
    }
}
