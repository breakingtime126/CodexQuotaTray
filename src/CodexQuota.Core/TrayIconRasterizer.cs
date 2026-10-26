using System.IO.Compression;

namespace CodexQuota.Core;

/// <summary>A top-to-bottom, straight-alpha RGBA8 image with no platform drawing dependencies.</summary>
public sealed class RgbaImage
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public RgbaImage(int width, int height, byte[] pixels)
    {
        if (pixels is null) throw new ArgumentNullException(nameof(pixels));
        if (width <= 0 || height <= 0 || pixels.LongLength != (long)width * height * 4)
            throw new ArgumentException("Dimensions must match the RGBA pixel buffer.", nameof(pixels));
        Width = width;
        Height = height;
        Pixels = pixels;
    }
}

/// <summary>
/// Deterministic, dependency-free tray artwork. Numeric vector outlines are rasterized with
/// grayscale coverage, identically on Windows, Linux and macOS. No installed fonts or GDI text.
/// </summary>
public static class TrayIconRasterizer
{
    private readonly record struct Color(byte R, byte G, byte B);
    private readonly record struct Point(double X, double Y);
    private readonly record struct Edge(Point From, Point To);
    private static readonly Color Backing = new(16, 26, 36);
    private static readonly Color Track = new(61, 79, 94);
    private static readonly Color Mint = new(101, 236, 183);
    private static readonly Color White = new(249, 252, 255);
    private static readonly Color Neutral = new(158, 174, 186);
    private static readonly Color Amber = new(255, 193, 92);
    private static readonly Color Coral = new(255, 112, 124);
    private const int SamplesPerAxis = 8;
    private const int TextSamplesPerAxis = 12;
    private const double Tau = Math.PI * 2;
    private static readonly object MaskLock = new();
    private static readonly Dictionary<int, byte[]> TextMasks = new();
    // A bounded cache keeps animation frames cheap without retaining every possible DPI/value pair.
    private const int MaxCachedMasks = 96;

    /// <summary>
    /// Renders 16–256 px square artwork. Percent is the remaining allowance (0–100), or null when
    /// unknown. Fresh rings are green above 30, amber at 10–30, and red below 10.
    /// Stale data keeps its supplied value, replacing the live ring with neutral gray dashes.
    /// </summary>
    public static RgbaImage Render(int size, int? remainingPercent, bool stale = false) =>
        RenderFrame(size, remainingPercent, remainingPercent, stale);

    /// <summary>
    /// Renders a native-size animation frame. The display digits stay steady while ringPercent
    /// moves between real quota readings. A 0–1 sweepPhase adds one tiny fading highlight inside
    /// the active arc; NaN disables it. Unknown and stale states never show a live highlight.
    /// </summary>
    public static RgbaImage RenderFrame(int size, int? displayPercent, double? ringPercent,
        bool stale = false, double sweepPhase = double.NaN, bool notification = false)
    {
        if (size is < 16 or > 256)
            throw new ArgumentOutOfRangeException(nameof(size), "Use a size from 16 through 256 pixels.");
        if (displayPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(displayPercent), "Remaining percent must be 0–100 or null.");
        if (ringPercent.HasValue && (double.IsNaN(ringPercent.Value) || double.IsInfinity(ringPercent.Value) ||
            ringPercent.Value < 0 || ringPercent.Value > 100))
            throw new ArgumentOutOfRangeException(nameof(ringPercent), "Ring percent must be finite, 0–100, or null.");
        if (!double.IsNaN(sweepPhase) && (double.IsInfinity(sweepPhase) || sweepPhase < 0 || sweepPhase > 1))
            throw new ArgumentOutOfRangeException(nameof(sweepPhase), "Sweep phase must be 0–1 or NaN.");

        var pixels = new byte[size * size * 4];
        byte[] glyphMask = GetTextMask(size, displayPercent);
        double center = size / 2d;
        double backingRadius = center - 0.20;
        double outerRadius = center - 0.65;
        double thickness = Math.Max(1.05, size * 0.064);
        double innerRadius = outerRadius - thickness;
        double ringMidpoint = (outerRadius + innerRadius) / 2;
        double ringValue = ringPercent ?? displayPercent ?? 0;
        double progressAngle = ringValue / 100d * Tau;
        double capRadius = thickness / 2;
        double endX = center + Math.Sin(progressAngle) * ringMidpoint;
        double endY = center - Math.Cos(progressAngle) * ringMidpoint;
        bool dashed = stale || displayPercent is null;
        // Severity follows the latest displayed reading, not a transitional arc from an older
        // reading. Stale/unknown has a separate neutral treatment, never a quota-warning hue.
        var activeColor = dashed ? Neutral : displayPercent < 10 ? Coral : displayPercent <= 30 ? Amber : Mint;
        bool glint = !dashed && ringValue > 0 && !double.IsNaN(sweepPhase) && sweepPhase > 0 && sweepPhase < 1;
        double glintAngle = glint ? sweepPhase * progressAngle : 0;
        double glintStrength = glint ? Math.Sin(sweepPhase * Math.PI) * 0.80 : 0;
        double glintWidth = Math.Min(0.30, 2.0 / ringMidpoint);
        const int dashCount = 12;

        for (int y = 0; y < size; y++)
        for (int x = 0; x < size; x++)
        {
            int red = 0, green = 0, blue = 0, covered = 0;
            for (int sy = 0; sy < SamplesPerAxis; sy++)
            for (int sx = 0; sx < SamplesPerAxis; sx++)
            {
                double px = x + (sx + 0.5) / SamplesPerAxis;
                double py = y + (sy + 0.5) / SamplesPerAxis;
                double dx = px - center, dy = py - center;
                double distanceSquared = dx * dx + dy * dy;
                if (distanceSquared > backingRadius * backingRadius) continue;
                Color color = Backing;
                bool inRing = distanceSquared >= innerRadius * innerRadius && distanceSquared <= outerRadius * outerRadius;
                bool active = false;
                double angle = 0;
                if (inRing)
                {
                    angle = Math.Atan2(dx, -dy);
                    if (angle < 0) angle += Tau;
                    if (dashed)
                    {
                        // Gaps are backing-colored cuts, so stale is recognizable without color.
                        double dashPhase = (angle / Tau * dashCount + 0.31) % 1;
                        if (dashPhase < 0.64) color = activeColor;
                    }
                    else
                    {
                        color = Track;
                        active = ringValue > 0 && angle <= progressAngle;
                    }
                }
                if (!dashed && ringValue > 0 && ringValue < 100)
                {
                    // Rounded leading/trailing ends, never a misleading active dot at zero.
                    double fromStartX = px - center, fromStartY = py - (center - ringMidpoint);
                    double fromEndX = px - endX, fromEndY = py - endY;
                    if (fromStartX * fromStartX + fromStartY * fromStartY <= capRadius * capRadius ||
                        fromEndX * fromEndX + fromEndY * fromEndY <= capRadius * capRadius)
                    {
                        active = true;
                        if (!inRing)
                        {
                            angle = Math.Atan2(dx, -dy);
                            if (angle < 0) angle += Tau;
                        }
                    }
                }
                if (active)
                {
                    color = activeColor;
                    if (glint)
                    {
                        double delta = Math.Abs(angle - glintAngle);
                        delta = Math.Min(delta, Tau - delta);
                        double strength = Math.Max(0, 1 - delta / glintWidth) * glintStrength;
                        color = new Color((byte)(activeColor.R + (255 - activeColor.R) * strength),
                            (byte)(activeColor.G + (255 - activeColor.G) * strength),
                            (byte)(activeColor.B + (255 - activeColor.B) * strength));
                    }
                }
                red += color.R;
                green += color.G;
                blue += color.B;
                covered++;
            }
            int offset = (y * size + x) * 4;
            if (covered == 0) continue;
            int mask = glyphMask[y * size + x];
            // Text is a cached grayscale coverage mask, not blurred bitmap rectangles or RGB subpixels.
            pixels[offset] = Mix((red + covered / 2) / covered, White.R, mask);
            pixels[offset + 1] = Mix((green + covered / 2) / covered, White.G, mask);
            pixels[offset + 2] = Mix((blue + covered / 2) / covered, White.B, mask);
            pixels[offset + 3] = (byte)((covered * 255 + 32) / 64);
        }
        if (notification) PaintNotification(pixels, glyphMask, size);
        return new RgbaImage(size, size, pixels);
    }

    private static void PaintNotification(byte[] pixels, byte[] glyphMask, int size)
    {
        // A small static corner badge, not an animation or change to the quota ring/digits.
        var radius = Math.Max(1.5, size * .095);
        var edge = Math.Max(.45, size * .018);
        var cx = size - radius - .25;
        var cy = radius + .25;
        var outer = radius + edge;
        for (var y = 0; y < Math.Ceiling(cy + outer); y++)
        for (var x = Math.Max(0, (int)Math.Floor(cx - outer)); x < size; x++)
        {
            if (glyphMask[y * size + x] > 0) continue;
            var red = 0; var green = 0; var blue = 0; var coverage = 0;
            for (var sy = 0; sy < SamplesPerAxis; sy++)
            for (var sx = 0; sx < SamplesPerAxis; sx++)
            {
                var dx = x + (sx + .5) / SamplesPerAxis - cx;
                var dy = y + (sy + .5) / SamplesPerAxis - cy;
                var distance = dx * dx + dy * dy;
                if (distance > outer * outer) continue;
                var color = distance <= radius * radius ? new Color(240, 57, 68) : Backing;
                red += color.R; green += color.G; blue += color.B; coverage++;
            }
            if (coverage == 0) continue;
            var i = (y * size + x) * 4;
            var alpha = coverage / 64d;
            var oldAlpha = pixels[i + 3] / 255d;
            var combined = alpha + oldAlpha * (1 - alpha);
            pixels[i] = (byte)Math.Round((red / (double)coverage * alpha + pixels[i] * oldAlpha * (1 - alpha)) / combined);
            pixels[i + 1] = (byte)Math.Round((green / (double)coverage * alpha + pixels[i + 1] * oldAlpha * (1 - alpha)) / combined);
            pixels[i + 2] = (byte)Math.Round((blue / (double)coverage * alpha + pixels[i + 2] * oldAlpha * (1 - alpha)) / combined);
            pixels[i + 3] = (byte)Math.Round(combined * 255);
        }
    }

    private static byte Mix(int background, int foreground, int alpha) =>
        (byte)((background * (255 - alpha) + foreground * alpha + 127) / 255);

    private static byte[] GetTextMask(int size, int? percent)
    {
        int key = size * 128 + (percent ?? 101);
        lock (MaskLock)
        {
            if (TextMasks.TryGetValue(key, out var existing)) return existing;
            var mask = BuildTextMask(size, percent);
            if (TextMasks.Count >= MaxCachedMasks) TextMasks.Clear();
            TextMasks.Add(key, mask);
            return mask;
        }
    }

    private static byte[] BuildTextMask(int size, int? percent)
    {
        string label = percent is null ? "?" : percent.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var glyphs = label.Select(QuotaDigitGeometry.Get).ToArray();
        // Keep the same cap height at 100; fit its three glyphs horizontally, not vertically.
        double height = size * 0.49;
        double scaleY = height / 1530;
        double sourceWidth = glyphs.Sum(glyph => glyph.Advance) - glyphs[glyphs.Length - 1].Advance + glyphs[glyphs.Length - 1].Right - glyphs[0].Left;
        double widthLimit = size * (size == 16 && label.Length == 2 ? 0.64 : 0.67);
        double scaleX = Math.Min(scaleY * 0.91, widthLimit / sourceWidth);
        double width = sourceWidth * scaleX;
        double originX = (size - width) / 2 - glyphs[0].Left * scaleX;
        double originY = (size - height) / 2;
        var edges = new List<Edge>();
        foreach (var glyph in glyphs)
        {
            AddOutline(edges, glyph.Path, originX, originY, scaleX, scaleY);
            originX += glyph.Advance * scaleX;
        }
        var coverage = new int[size * size];
        var intersections = new List<double>();
        // Scanline coverage keeps curves smooth, counters open and rasterization platform-independent.
        for (int sampleY = 0; sampleY < size * TextSamplesPerAxis; sampleY++)
        {
            double y = (sampleY + 0.5) / TextSamplesPerAxis;
            intersections.Clear();
            foreach (var edge in edges)
                if ((edge.From.Y <= y && edge.To.Y > y) || (edge.To.Y <= y && edge.From.Y > y))
                    intersections.Add(edge.From.X + (y - edge.From.Y) * (edge.To.X - edge.From.X) / (edge.To.Y - edge.From.Y));
            intersections.Sort();
            for (int i = 0; i + 1 < intersections.Count; i += 2)
            {
                int first = Math.Max(0, (int)Math.Ceiling(intersections[i] * TextSamplesPerAxis - 0.5));
                int last = Math.Min(size * TextSamplesPerAxis, (int)Math.Ceiling(intersections[i + 1] * TextSamplesPerAxis - 0.5));
                for (int sampleX = first; sampleX < last; sampleX++)
                    coverage[(sampleY / TextSamplesPerAxis) * size + sampleX / TextSamplesPerAxis]++;
            }
        }
        var result = new byte[size * size];
        const int sampleCount = TextSamplesPerAxis * TextSamplesPerAxis;
        for (int i = 0; i < result.Length; i++) result[i] = (byte)((coverage[i] * 255 + sampleCount / 2) / sampleCount);
        return result;
    }

    private static void AddOutline(List<Edge> edges, short[] path, double originX, double originY, double scaleX, double scaleY)
    {
        Point Transform(int x, int y) => new(originX + x * scaleX, originY + (1510 - y) * scaleY);
        Point current = default, start = default;
        for (int i = 0; i < path.Length;)
        {
            switch (path[i++])
            {
                case 0: current = start = Transform(path[i++], path[i++]); break;
                case 1:
                    var end = Transform(path[i++], path[i++]);
                    edges.Add(new(current, end)); current = end; break;
                case 2:
                    var control = Transform(path[i++], path[i++]);
                    end = Transform(path[i++], path[i++]);
                    AddCurve(edges, current, control, end); current = end; break;
                case 3: edges.Add(new(current, start)); current = start; break;
                default: throw new InvalidDataException("Invalid numeric outline command.");
            }
        }
    }

    private static void AddCurve(List<Edge> edges, Point from, Point control, Point to)
    {
        double dx = control.X - (from.X + to.X) / 2, dy = control.Y - (from.Y + to.Y) / 2;
        if (dx * dx + dy * dy <= 0.0004)
        {
            edges.Add(new(from, to));
            return;
        }
        Point first = new((from.X + control.X) / 2, (from.Y + control.Y) / 2);
        Point second = new((control.X + to.X) / 2, (control.Y + to.Y) / 2);
        Point midpoint = new((first.X + second.X) / 2, (first.Y + second.Y) / 2);
        AddCurve(edges, from, first, midpoint);
        AddCurve(edges, midpoint, second, to);
    }

    /// <summary>Encodes any RGBA8 image as a lossless PNG using only .NET's zlib support.</summary>
    public static byte[] EncodePng(RgbaImage image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        using var output = new MemoryStream();
        byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
        output.Write(signature, 0, signature.Length);
        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)image.Width);
        WriteBigEndian(header, 4, (uint)image.Height);
        header[8] = 8; // 8 bits per channel.
        header[9] = 6; // RGBA; remaining header bytes are zero.
        WriteChunk(output, "IHDR", header);
        using var compressed = new MemoryStream();
        // DeflateStream emits raw DEFLATE on both supported runtimes. Add the RFC 1950
        // zlib header/checksum ourselves, since .NET Framework has no ZLibStream.
        compressed.WriteByte(0x78);
        compressed.WriteByte(0x9c);
        uint adlerA = 1, adlerB = 0;
        using (var deflate = new DeflateStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            int stride = checked(image.Width * 4);
            for (int row = 0; row < image.Height; row++)
            {
                deflate.WriteByte(0); // PNG filter: None.
                adlerB = (adlerB + adlerA) % 65521;
                int offset = row * stride;
                deflate.Write(image.Pixels, offset, stride);
                for (int i = offset; i < offset + stride; i++)
                {
                    adlerA = (adlerA + image.Pixels[i]) % 65521;
                    adlerB = (adlerB + adlerA) % 65521;
                }
            }
        }
        var checksum = new byte[4];
        WriteBigEndian(checksum, 0, (adlerB << 16) | adlerA);
        compressed.Write(checksum, 0, checksum.Length);
        WriteChunk(output, "IDAT", compressed.ToArray());
        WriteChunk(output, "IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    /// <summary>Returns a PNG-backed Windows ICO with native 16, 20, 24, 32, and 64 px frames.</summary>
    public static byte[] EncodeIco(int? remainingPercent, bool stale = false)
    {
        int[] sizes = [16, 20, 24, 32, 64];
        byte[][] frames = sizes.Select(size => EncodePng(Render(size, remainingPercent, stale))).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1); // Icon, rather than cursor.
        writer.Write((ushort)sizes.Length);
        int offset = 6 + sizes.Length * 16;
        for (int i = 0; i < sizes.Length; i++)
        {
            writer.Write((byte)sizes[i]);
            writer.Write((byte)sizes[i]);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames) writer.Write(frame);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>Encodes exactly one native-size frame so the shell need not rescale another DPI frame.</summary>
    public static byte[] EncodeIcoFrame(RgbaImage image)
    {
        if (image is null) throw new ArgumentNullException(nameof(image));
        if (image.Width != image.Height || image.Width is < 16 or > 256)
            throw new ArgumentException("A tray frame must be square and 16–256 pixels.", nameof(image));
        byte[] png = EncodePng(image);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write((byte)(image.Width == 256 ? 0 : image.Width));
        writer.Write((byte)(image.Height == 256 ? 0 : image.Height));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(png.Length);
        writer.Write(22);
        writer.Write(png);
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteChunk(Stream output, string type, byte[] data)
    {
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        var integer = new byte[4];
        WriteBigEndian(integer, 0, (uint)data.Length);
        output.Write(integer, 0, integer.Length);
        output.Write(typeBytes, 0, typeBytes.Length);
        output.Write(data, 0, data.Length);
        uint crc = 0xffffffff;
        foreach (byte item in typeBytes) crc = UpdateCrc(crc, item);
        foreach (byte item in data) crc = UpdateCrc(crc, item);
        WriteBigEndian(integer, 0, crc ^ 0xffffffff);
        output.Write(integer, 0, integer.Length);
    }

    private static void WriteBigEndian(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (int bit = 0; bit < 8; bit++)
            crc = (crc >> 1) ^ ((crc & 1) == 1 ? 0xedb88320u : 0);
        return crc;
    }
}
