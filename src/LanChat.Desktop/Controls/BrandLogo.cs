using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;

namespace LanChat.Desktop.Controls;

/// <summary>Draws the original, transparent donkey mark from its bundled SVG paths.</summary>
public sealed class BrandLogo : Control
{
    private static readonly Lazy<IReadOnlyList<Part>> Parts = new(Load);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 64;
        var transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 64 * scale) / 2, (Bounds.Height - 64 * scale) / 2);
        using (context.PushTransform(transform))
            foreach (var part in Parts.Value) context.DrawGeometry(part.Fill, part.Stroke, part.Geometry);
    }

    private static IReadOnlyList<Part> Load()
    {
        using var stream = AssetLoader.Open(new Uri("avares://LanChat.Desktop/Assets/Brand/donkey.svg"));
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var parts = new List<Part>();
        foreach (var path in document.Root!.Elements().Where(x => x.Name.LocalName == "path"))
        {
            static IBrush? Brush(string? color) => color is null or "none" ? null : new SolidColorBrush(Color.Parse(color));
            var stroke = Brush(path.Attribute("stroke")?.Value);
            var width = double.Parse(path.Attribute("stroke-width")?.Value ?? "1", CultureInfo.InvariantCulture);
            parts.Add(new Part(Geometry.Parse(path.Attribute("d")!.Value), Brush(path.Attribute("fill")?.Value),
                stroke is null ? null : new Pen(stroke, width, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round)));
        }
        return parts;
    }

    private sealed record Part(Geometry Geometry, IBrush? Fill, Pen? Stroke);
}
