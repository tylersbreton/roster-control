using SkiaSharp;

// Development tools for Roster Control.
//   dotnet run --project tools/RosterControl.DevTools -- icon <output-folder>
// Renders the application icon (hextech-style hexagon with three roster columns) to PNG and a multi-size ICO.

if (args.Length < 2 || args[0] != "icon")
{
    Console.WriteLine("usage: icon <output-folder>");
    return 1;
}

var outDir = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outDir);
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var pngs = new List<(int Size, byte[] Data)>();
foreach (var size in sizes)
{
    var data = Render(size);
    pngs.Add((size, data));
    if (size == 256) File.WriteAllBytes(Path.Combine(outDir, "roster-256.png"), data);
}
File.WriteAllBytes(Path.Combine(outDir, "roster.ico"), BuildIco(pngs));
Console.WriteLine($"Icon written to {outDir}");
return 0;

static byte[] Render(int size)
{
    var info = new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var c = surface.Canvas;
    c.Clear(SKColors.Transparent);
    c.Scale(size / 256f);

    // Hexagon (pointy top) with rounded joins.
    using var hex = new SKPath();
    var cx = 128f;
    var cy = 128f;
    var r = 118f;
    for (var i = 0; i < 6; i++)
    {
        var a = (float)(Math.PI / 180 * (60 * i - 90));
        var p = new SKPoint(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a));
        if (i == 0) hex.MoveTo(p);
        else hex.LineTo(p);
    }
    hex.Close();

    using var fill = new SKPaint
    {
        IsAntialias = true,
        Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, 256),
            [new SKColor(0x10, 0x1C, 0x2C), new SKColor(0x05, 0x0B, 0x14)], SKShaderTileMode.Clamp),
        PathEffect = SKPathEffect.CreateCorner(18),
    };
    c.DrawPath(hex, fill);

    using var stroke = new SKPaint
    {
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeWidth = 14,
        StrokeJoin = SKStrokeJoin.Round,
        PathEffect = SKPathEffect.CreateCorner(18),
        Shader = SKShader.CreateLinearGradient(new SKPoint(40, 10), new SKPoint(216, 246),
            [new SKColor(0xF0, 0xE6, 0xD2), new SKColor(0xC8, 0xAA, 0x6E), new SKColor(0x78, 0x5A, 0x28)], [0f, 0.45f, 1f], SKShaderTileMode.Clamp),
    };
    c.DrawPath(hex, stroke);

    // Three roster columns of different heights (gold, teal, gold).
    void Column(float x, float top, SKColor a, SKColor b)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Shader = SKShader.CreateLinearGradient(new SKPoint(x, top), new SKPoint(x, 182), [a, b], SKShaderTileMode.Clamp),
        };
        c.DrawRoundRect(new SKRect(x, top, x + 34, 182), 7, 7, p);
    }
    Column(70, 98, new SKColor(0xF0, 0xE6, 0xD2), new SKColor(0xC8, 0xAA, 0x6E));
    Column(111, 70, new SKColor(0x6F, 0xF0, 0xE6), new SKColor(0x0A, 0xC8, 0xB9));
    Column(152, 116, new SKColor(0xF0, 0xE6, 0xD2), new SKColor(0xC8, 0xAA, 0x6E));

    using var image = surface.Snapshot();
    using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
    return encoded.ToArray();
}

static byte[] BuildIco(List<(int Size, byte[] Data)> images)
{
    using var ms = new MemoryStream();
    using var w = new BinaryWriter(ms);
    w.Write((ushort)0);
    w.Write((ushort)1);
    w.Write((ushort)images.Count);
    var offset = 6 + 16 * images.Count;
    foreach (var (size, data) in images)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);
        w.Write((byte)0);
        w.Write((ushort)1);
        w.Write((ushort)32);
        w.Write(data.Length);
        w.Write(offset);
        offset += data.Length;
    }
    foreach (var (_, data) in images) w.Write(data);
    return ms.ToArray();
}
