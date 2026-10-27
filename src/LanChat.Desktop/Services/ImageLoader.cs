using Avalonia.Media.Imaging;

namespace LanChat.Desktop.Services;

public static class ImageLoader
{
    public static Bitmap? Load(string? path, int width = 600)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { return null; }
    }

    public static string FileSize(long size) => size >= 1L << 30 ? $"{size / (double)(1L << 30):0.0} GB"
        : size >= 1 << 20 ? $"{size / (double)(1 << 20):0.0} MB"
        : size >= 1024 ? $"{size / 1024.0:0.0} KB" : $"{size} B";

    public static string TransferSpeed(double bytesPerSecond) => FileSize((long)Math.Max(0, bytesPerSecond)) + "/s";
}
