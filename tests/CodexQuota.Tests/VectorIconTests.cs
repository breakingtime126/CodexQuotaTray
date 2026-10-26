using System.Buffers.Binary;
using CodexQuota.Core;

internal static class VectorIconTests
{
    public static void Register(Action<string, Action> test)
    {
        foreach (int size in new[] { 16, 20, 24, 32, 64 })
        {
            int pixels = size;
            test($"vector icons: {size}px smooth native digits and open counters", () =>
            {
                foreach (var item in new[] { (0, 1), (7, 0), (11, 0), (68, 3), (88, 4), (100, 2) })
                {
                    var image = TrayIconRasterizer.RenderFrame(pixels, item.Item1, 0);
                    Assert(CountCounters(image) == item.Item2, $"{pixels}px / {item.Item1} must keep its counters open.");
                    int softPixels = 0;
                    for (int y = pixels / 4; y < pixels * 3 / 4; y++)
                    for (int x = pixels / 4; x < pixels * 3 / 4; x++)
                    {
                        int offset = (y * pixels + x) * 4;
                        byte red = image.Pixels[offset];
                        if (red is > 30 and < 240) softPixels++;
                    }
                    Assert(softPixels > 0, "Glyph edges must contain grayscale coverage rather than hard bitmap steps.");
                }
            });
            test($"vector icons: {size}px static compatibility and frame independence", () =>
            {
                foreach (int? value in new int?[] { null, 0, 7, 11, 68, 88, 97, 100 })
                foreach (bool stale in new[] { false, true })
                {
                    var original = TrayIconRasterizer.Render(pixels, value, stale);
                    var frame = TrayIconRasterizer.RenderFrame(pixels, value, value, stale);
                    Assert(original.Pixels.SequenceEqual(frame.Pixels), "Static API must equal the no-animation frame.");
                    frame.Pixels[0] ^= 0xff;
                    Assert(original.Pixels.SequenceEqual(TrayIconRasterizer.Render(pixels, value, stale).Pixels),
                        "Returned image mutation must not corrupt cached glyph masks.");
                }
            });
            test($"vector icons: {size}px glint only changes real active ring", () =>
            {
                var normal = TrayIconRasterizer.RenderFrame(pixels, 97, 37);
                var empty = TrayIconRasterizer.RenderFrame(pixels, 97, 0);
                var glint = TrayIconRasterizer.RenderFrame(pixels, 97, 37, sweepPhase: 0.5);
                int changed = 0;
                for (int offset = 0; offset < normal.Pixels.Length; offset += 4)
                {
                    Assert(normal.Pixels[offset + 3] == glint.Pixels[offset + 3], "Glint must not change silhouette alpha.");
                    if (SamePixel(normal, glint, offset)) continue;
                    changed++;
                    Assert(!SamePixel(normal, empty, offset), "Glint must stay inside the real active ring footprint.");
                }
                Assert(changed > 0, "Mid-sweep must add a visible but small highlight.");
                Assert(changed < pixels * pixels / 8, "Highlight must remain tiny, not repaint the whole ring.");
                foreach (double phase in new[] { 0d, 1d })
                    Assert(normal.Pixels.SequenceEqual(TrayIconRasterizer.RenderFrame(pixels, 97, 37, sweepPhase: phase).Pixels),
                        "Glint must fade back to the exact static image at either endpoint.");
            });
            test($"vector icons: {size}px steady digits during quota movement", () =>
            {
                var before = TrayIconRasterizer.RenderFrame(pixels, 97, 15);
                var after = TrayIconRasterizer.RenderFrame(pixels, 97, 97, sweepPhase: 0.5);
                double center = pixels / 2d;
                double radius = center - 0.65 - Math.Max(1.05, pixels * 0.064) - 1;
                for (int y = 0; y < pixels; y++)
                for (int x = 0; x < pixels; x++)
                {
                    double dx = x + 0.5 - center, dy = y + 0.5 - center;
                    if (dx * dx + dy * dy < radius * radius)
                        Assert(SamePixel(before, after, (y * pixels + x) * 4), "Central glyph pixels must not animate.");
                }
            });
        }

        test("vector icons: unknown, stale and zero never get a live glint", () =>
        {
            foreach (int? value in new int?[] { null, 0, 68, 97, 100 })
            foreach (bool stale in new[] { false, true })
            {
                if (value != null && value != 0 && !stale) continue;
                var normal = TrayIconRasterizer.RenderFrame(32, value, value, stale);
                foreach (double phase in new[] { 0d, 0.25, 0.5, 0.75, 1d })
                    Assert(normal.Pixels.SequenceEqual(TrayIconRasterizer.RenderFrame(32, value, value, stale, phase).Pixels),
                        "Unavailable/empty quota must not imply a successful live refresh.");
            }
            Assert(TrayIconRasterizer.Render(32, null).Pixels.SequenceEqual(
                TrayIconRasterizer.RenderFrame(32, null, 100, sweepPhase: 0.5).Pixels),
                "Unknown display must ignore any supplied animated quota.");
        });

        test("vector icons: fractional ring percent stays distinct from display digits", () =>
        {
            Assert(!TrayIconRasterizer.RenderFrame(32, 97, 37).Pixels.SequenceEqual(
                TrayIconRasterizer.RenderFrame(32, 97, 37.75).Pixels), "Fractional quota must move the ring smoothly.");
            Assert(!TrayIconRasterizer.RenderFrame(32, 97, 37).Pixels.SequenceEqual(
                TrayIconRasterizer.RenderFrame(32, 37, 37).Pixels), "Displayed value must come from displayPercent.");
        });

        test("vector icons: single-frame ICO preserves exact native PNG", () =>
        {
            foreach (int size in new[] { 16, 20, 24, 32, 64, 256 })
            {
                var image = TrayIconRasterizer.RenderFrame(size, 97, 83.5, sweepPhase: 0.5);
                var png = TrayIconRasterizer.EncodePng(image);
                var ico = TrayIconRasterizer.EncodeIcoFrame(image);
                Assert(BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2)) == 1, "Expected exactly one ICO frame.");
                Assert(ico[6] == (size == 256 ? 0 : size) && ico[7] == ico[6], "Native ICO dimensions must match.");
                Assert(BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(14, 4)) == png.Length, "ICO data length must match PNG.");
                Assert(BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(18, 4)) == 22, "Single-frame data starts at byte 22.");
                Assert(ico.AsSpan(22).SequenceEqual(png), "ICO must embed the unscaled exact PNG frame.");
            }
        });

        test("vector icons: animation and frame arguments are validated", () =>
        {
            foreach (double value in new[] { -0.01, 100.01, double.NaN, double.NegativeInfinity, double.PositiveInfinity })
                Throws<ArgumentOutOfRangeException>(() => TrayIconRasterizer.RenderFrame(16, 97, value));
            foreach (double phase in new[] { -0.01, 1.01, double.NegativeInfinity, double.PositiveInfinity })
                Throws<ArgumentOutOfRangeException>(() => TrayIconRasterizer.RenderFrame(16, 97, 97, sweepPhase: phase));
            foreach (int value in new[] { -1, 101 })
                Throws<ArgumentOutOfRangeException>(() => TrayIconRasterizer.RenderFrame(16, value, 50));
            foreach (int size in new[] { 15, 257 })
                Throws<ArgumentOutOfRangeException>(() => TrayIconRasterizer.RenderFrame(size, 97, 97));
            Throws<ArgumentNullException>(() => TrayIconRasterizer.EncodeIcoFrame(null!));
            Throws<ArgumentException>(() => TrayIconRasterizer.EncodeIcoFrame(new RgbaImage(16, 20, new byte[16 * 20 * 4])));
            Throws<ArgumentException>(() => TrayIconRasterizer.EncodeIcoFrame(new RgbaImage(1, 1, new byte[4])));
        });
    }

    private static bool SamePixel(RgbaImage a, RgbaImage b, int offset) =>
        a.Pixels[offset] == b.Pixels[offset] && a.Pixels[offset + 1] == b.Pixels[offset + 1] &&
        a.Pixels[offset + 2] == b.Pixels[offset + 2] && a.Pixels[offset + 3] == b.Pixels[offset + 3];

    // Flood-fill the dark background at 50% ink coverage. Enclosed regions are numeral counters.
    private static int CountCounters(RgbaImage image)
    {
        int size = image.Width;
        var seen = new bool[size * size];
        var queue = new Queue<int>();
        int holes = 0;
        for (int start = 0; start < seen.Length; start++)
        {
            if (seen[start] || image.Pixels[start * 4] > 120) continue;
            bool touchesEdge = false;
            seen[start] = true;
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                int pixel = queue.Dequeue();
                int x = pixel % size, y = pixel / size;
                touchesEdge |= x == 0 || y == 0 || x == size - 1 || y == size - 1;
                void Visit(int nx, int ny)
                {
                    if (nx < 0 || nx >= size || ny < 0 || ny >= size) return;
                    int index = ny * size + nx;
                    if (seen[index] || image.Pixels[index * 4] > 120) return;
                    seen[index] = true;
                    queue.Enqueue(index);
                }
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }
            if (!touchesEdge) holes++;
        }
        return holes;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }
}
