using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using SkiaSharp;

var root = Path.GetFullPath(args.FirstOrDefault() ?? Directory.GetCurrentDirectory());
var assetRoot = Path.Combine(root, "src", "LanChat.Desktop", "Assets");
using var reader = XmlReader.Create(Path.Combine(assetRoot, "Brand", "donkey.svg"), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
var document = XDocument.Load(reader);
var paths = document.Root!.Elements().Where(x => x.Name.LocalName == "path").ToArray();
var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var frames = sizes.Select(Render).ToArray();
var iconPath = Path.Combine(assetRoot, "lanchat.ico");
using (var output = new BinaryWriter(File.Create(iconPath)))
{
    output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
    var offset = 6 + sizes.Length * 16;
    for (var i = 0; i < sizes.Length; i++)
    {
        output.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        output.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
        output.Write((byte)0); output.Write((byte)0);
        output.Write((ushort)1); output.Write((ushort)32);
        output.Write(frames[i].Length); output.Write(offset);
        offset += frames[i].Length;
    }
    foreach (var frame in frames) output.Write(frame);
}
Console.WriteLine("Generated transparent multi-resolution icon: " + iconPath);

// Development preview: actual icon sizes on both common tray backgrounds.
var previewRoot = Path.Combine(root, "artifacts", "screenshots");
Directory.CreateDirectory(previewRoot);
using (var preview = new SKBitmap(760, 320, SKColorType.Bgra8888, SKAlphaType.Premul))
using (var canvas = new SKCanvas(preview))
using (var background = new SKPaint())
using (var text = new SKPaint { IsAntialias = true, TextSize = 17 })
{
    foreach (var row in new[] { 0, 1 })
    {
        background.Color = SKColor.Parse(row == 0 ? "#17212B" : "#F3F6F8");
        canvas.DrawRect(0, row * 160, 760, 160, background);
        text.Color = SKColor.Parse(row == 0 ? "#E8EEF4" : "#203344");
        var x = 32f;
        foreach (var size in new[] { 16, 20, 24, 32, 48, 64, 96 })
        {
            using var iconBitmap = SKBitmap.Decode(Render(size));
            canvas.DrawBitmap(iconBitmap, x + (96 - size) / 2f, row * 160 + 16 + (96 - size) / 2f);
            canvas.DrawText(size + " px", x + 23, row * 160 + 140, text);
            x += 102;
        }
    }
    using var image = SKImage.FromBitmap(preview);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var stream = File.Create(Path.Combine(previewRoot, "donkey-icon-preview.png"));
    data.SaveTo(stream);
}
File.WriteAllBytes(Path.Combine(previewRoot, "donkey-logo.png"), frames[^1]);

byte[] Render(int size)
{
    using var large = new SKBitmap(size * 4, size * 4, SKColorType.Bgra8888, SKAlphaType.Premul);
    using (var canvas = new SKCanvas(large))
    {
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size * 4 / 64f);
        foreach (var element in paths)
        {
            using var path = SKPath.ParseSvgPathData(element.Attribute("d")!.Value);
            using var paint = new SKPaint { IsAntialias = true, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
            if (element.Attribute("fill")?.Value is { } fill && fill != "none")
            {
                paint.Style = SKPaintStyle.Fill; paint.Color = SKColor.Parse(fill);
                canvas.DrawPath(path, paint);
            }
            if (element.Attribute("stroke")?.Value is { } stroke && stroke != "none")
            {
                paint.Style = SKPaintStyle.Stroke; paint.Color = SKColor.Parse(stroke);
                paint.StrokeWidth = float.Parse(element.Attribute("stroke-width")?.Value ?? "1", CultureInfo.InvariantCulture);
                canvas.DrawPath(path, paint);
            }
        }
    }
    using var bitmap = large.Resize(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul), SKFilterQuality.High);
    // The ICO must preserve the silhouette's transparent corners at every size.
    if (bitmap.GetPixel(0, 0).Alpha != 0 || bitmap.GetPixel(size - 1, size - 1).Alpha != 0)
        throw new InvalidOperationException("The donkey icon background must be transparent.");
    using var image = SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}
