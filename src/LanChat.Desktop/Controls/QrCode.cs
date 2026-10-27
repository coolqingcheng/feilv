using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace LanChat.Desktop.Controls;

/// <summary>Renders QR modules as vectors, retaining the standard white quiet zone in both themes.</summary>
public sealed class QrCode : Control
{
    public static readonly StyledProperty<string> TextProperty = AvaloniaProperty.Register<QrCode, string>(nameof(Text), "");
    public string Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    private bool[][] _modules = [];
    static QrCode() => AffectsRender<QrCode>(TextProperty);
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != TextProperty) return;
        _modules = [];
        if (string.IsNullOrEmpty(Text)) return;
        using var data = QRCodeGenerator.GenerateQrCode(Text, QRCodeGenerator.ECCLevel.M);
        _modules = new bool[data.ModuleMatrix.Count][];
        for (var row = 0; row < _modules.Length; row++)
        {
            _modules[row] = new bool[data.ModuleMatrix[row].Length];
            data.ModuleMatrix[row].CopyTo(_modules[row], 0);
        }
    }
    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.White, new Rect(Bounds.Size));
        if (_modules.Length == 0) return;
        var size = Math.Floor(Math.Min(Bounds.Width, Bounds.Height) / _modules.Length);
        if (size < 1) return;
        var x = Math.Floor((Bounds.Width - size * _modules.Length) / 2);
        var y = Math.Floor((Bounds.Height - size * _modules.Length) / 2);
        for (var row = 0; row < _modules.Length; row++)
            for (var col = 0; col < _modules[row].Length; col++)
                if (_modules[row][col]) context.FillRectangle(Brushes.Black, new Rect(x + col * size, y + row * size, size, size));
    }
}
