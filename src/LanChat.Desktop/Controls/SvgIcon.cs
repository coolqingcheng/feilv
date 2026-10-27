using System.Collections.Concurrent;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Platform;

namespace LanChat.Desktop.Controls;

/// <summary>Renders the bundled Lucide SVG primitives directly, without a font or runtime code generation.</summary>
public sealed class SvgIcon : Control
{
    private static readonly ConcurrentDictionary<string, IReadOnlyList<Geometry>> Cache = new();
    public static readonly StyledProperty<string> KindProperty = AvaloniaProperty.Register<SvgIcon, string>(nameof(Kind), "file");
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<SvgIcon>();
    public string Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    static SvgIcon() => AffectsRender<SvgIcon>(KindProperty, ForegroundProperty);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var geometries = Cache.GetOrAdd(Kind, Load);
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var transform = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2);
        var pen = new Pen { Brush = Foreground, Thickness = 1.8, LineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        using (context.PushTransform(transform))
            foreach (var geometry in geometries) context.DrawGeometry(null, pen, geometry);
    }

    private static IReadOnlyList<Geometry> Load(string kind)
    {
        // Names come from compiled views. Keep the loader limited to embedded library assets.
        if (kind.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) return [];
        using var stream = AssetLoader.Open(new Uri($"avares://LanChat.Desktop/Assets/Icons/{kind}.svg"));
        using var reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var document = XDocument.Load(reader);
        var result = new List<Geometry>();
        foreach (var element in document.Root!.Elements())
        {
            double Number(string attribute) => double.TryParse(element.Attribute(attribute)?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : 0;
            var shape = element.Name.LocalName;
            switch (shape)
            {
                case "path": result.Add(Geometry.Parse(element.Attribute("d")!.Value)); break;
                case "circle":
                    var radius = Number("r");
                    result.Add(new EllipseGeometry(new Rect(Number("cx") - radius, Number("cy") - radius, radius * 2, radius * 2))); break;
                case "rect":
                    result.Add(new RectangleGeometry(new Rect(Number("x"), Number("y"), Number("width"), Number("height")), Number("rx"), Number("rx"))); break;
                case "line": result.Add(Geometry.Parse(FormattableString.Invariant($"M {Number("x1")},{Number("y1")} L {Number("x2")},{Number("y2")}"))); break;
                case "polyline":
                case "polygon":
                    result.Add(Geometry.Parse("M " + element.Attribute("points")!.Value + (shape == "polygon" ? " Z" : ""))); break;
            }
        }
        return result;
    }
}
