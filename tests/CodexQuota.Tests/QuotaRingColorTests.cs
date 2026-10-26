using CodexQuota.Core;

internal static class QuotaRingColorTests
{
    private readonly record struct Rgb(byte R, byte G, byte B);
    private static readonly Rgb Mint = new(101, 236, 183);
    private static readonly Rgb Amber = new(255, 193, 92);
    private static readonly Rgb Red = new(255, 112, 124);
    private static readonly Rgb Neutral = new(158, 174, 186);
    private static readonly Rgb White = new(249, 252, 255);
    private static readonly Rgb Backing = new(16, 26, 36);
    private static readonly Rgb Track = new(61, 79, 94);
    private static readonly Rgb[] SeverityColors = [Mint, Amber, Red];

    public static void Register(Action<string, Action> test)
    {
        foreach (var (percent, color) in new[] { (9, Red), (10, Amber), (30, Amber), (31, Mint), (100, Mint) })
        {
            test($"quota colors: {percent}% uses the exact remaining-quota severity", () =>
            {
                // At 64px each real arc has multiple fully covered samples. Count colors across
                // the image rather than pinning an assertion to one antialiased edge pixel.
                var image = TrayIconRasterizer.Render(64, percent);
                AssertPalette(image, color);
                Check.True(CountColor(image, White) > 0, "Digits must keep their existing white ink.");
            });
        }

        test("quota colors: zero has only the neutral track, without a fake severity arc", () =>
        {
            foreach (int size in new[] { 16, 20, 24, 32, 64 })
            {
                var image = TrayIconRasterizer.Render(size, 0);
                foreach (var color in SeverityColors)
                    Check.Equal(0, CountColor(image, color));
                foreach (double phase in new[] { 0.25, 0.5, 0.75 })
                    Check.True(image.Pixels.SequenceEqual(
                        TrayIconRasterizer.RenderFrame(size, 0, 0, sweepPhase: phase).Pixels),
                        "An empty ring must not grow a highlighted dot or progress arc.");
                AssertSameRing(image, TrayIconRasterizer.RenderFrame(size, 100, 0),
                    "Zero must use the same empty track as any other zero-length ring.");
            }
            Check.True(CountColor(TrayIconRasterizer.Render(64, 0), Track) > 0,
                "Zero still needs a visible neutral track.");
        });

        test("quota colors: stale and unknown use the same neutral dashed ring at every severity", () =>
        {
            foreach (int size in new[] { 16, 20, 24, 32, 64 })
            {
                var unknown = TrayIconRasterizer.Render(size, null);
                foreach (int? percent in new int?[] { null, 0, 9, 10, 30, 31, 100 })
                {
                    var stale = TrayIconRasterizer.Render(size, percent, stale: true);
                    AssertSameRing(unknown, stale, "Untrusted readings must have identical neutral dashes.");
                    foreach (double ring in new[] { 0d, 9d, 30d, 100d })
                    foreach (double phase in new[] { 0.25, 0.5, 0.75 })
                        Check.True(stale.Pixels.SequenceEqual(
                            TrayIconRasterizer.RenderFrame(size, percent, ring, stale: true, sweepPhase: phase).Pixels),
                            "Stale data must ignore animated progress and suppress the success glint.");
                }
                foreach (double ring in new[] { 0d, 9d, 30d, 100d })
                    Check.True(unknown.Pixels.SequenceEqual(
                        TrayIconRasterizer.RenderFrame(size, null, ring, sweepPhase: 0.5).Pixels),
                        "Unknown data must ignore animated progress and suppress the success glint.");
            }

            foreach (int? percent in new int?[] { null, 0, 9, 10, 30, 31, 100 })
            {
                var image = TrayIconRasterizer.Render(64, percent, stale: true);
                AssertPalette(image, Neutral);
                Check.True(CountRingInteriorColor(image, Neutral) > 0, "Neutral dashes must be visible.");
                Check.True(CountRingInteriorColor(image, Backing) > 0,
                    "The ring needs actual backing-colored gaps, not just a muted solid arc.");
            }
        });

        test("quota colors: severity changes never tint the central white digits", () =>
        {
            foreach (int size in new[] { 16, 20, 24, 32, 64 })
            foreach (int percent in new[] { 0, 9, 10, 30, 31, 100 })
            {
                var empty = TrayIconRasterizer.RenderFrame(size, percent, 0);
                var fresh = TrayIconRasterizer.RenderFrame(size, percent, 100, sweepPhase: 0.5);
                var stale = TrayIconRasterizer.Render(size, percent, stale: true);
                foreach (int offset in RegionOffsets(empty, ring: false))
                {
                    Check.True(SamePixel(empty, fresh, offset), "Quota movement must leave the digits unchanged.");
                    Check.True(SamePixel(empty, stale, offset), "Staleness must not recolor the digits.");
                }
            }
        });

        test("quota colors: animated ring uses displayed quota, never intermediate severity", () =>
        {
            foreach (var (percent, color) in new[] { (0, Red), (9, Red), (10, Amber), (30, Amber), (31, Mint), (100, Mint) })
            foreach (double ring in new[] { 9d, 10d, 30d, 31d, 100d })
            {
                var image = TrayIconRasterizer.RenderFrame(64, percent, ring);
                AssertPalette(image, color);
            }
        });

        test("quota colors: glint stays in the same severity family on real ring pixels", () =>
        {
            foreach (var (percent, color) in new[] { (9, Red), (10, Amber), (30, Amber), (31, Mint), (100, Mint) })
            {
                // Both directions across thresholds exercise cases where displayed severity and
                // the interpolated arc disagree, including a low-quota label over a large arc.
                foreach (double ring in new[] { 9d, 31d, 100d })
                {
                    var normal = TrayIconRasterizer.RenderFrame(64, percent, ring);
                    var empty = TrayIconRasterizer.RenderFrame(64, percent, 0);
                    var glint = TrayIconRasterizer.RenderFrame(64, percent, ring, sweepPhase: 0.5);
                    int highlightedSolidPixels = 0;
                    for (int offset = 0; offset < normal.Pixels.Length; offset += 4)
                    {
                        Check.Equal(normal.Pixels[offset + 3], glint.Pixels[offset + 3]);
                        if (SamePixel(normal, glint, offset)) continue;
                        Check.False(SamePixel(normal, empty, offset), "Glint must stay on the actual progress arc.");
                        if (!IsColor(normal, offset, color)) continue;
                        highlightedSolidPixels++;
                        var highlighted = new Rgb(glint.Pixels[offset], glint.Pixels[offset + 1], glint.Pixels[offset + 2]);
                        Check.True(SameSeverityFamily(highlighted, color),
                            "A white-tinted warning glint must not flash green or another severity.");
                        Check.True(highlighted.R >= Math.Min(color.R, White.R) &&
                            highlighted.G >= Math.Min(color.G, White.G) &&
                            highlighted.B >= Math.Min(color.B, White.B),
                            "Glint must lighten the current severity toward white, not replace it with mint.");
                    }
                    Check.True(highlightedSolidPixels > 0, "Sweep must visibly highlight some fully covered ring pixels.");
                }
            }
        });

        test("quota colors: full animation never flashes green while falling to low or empty quota", () =>
        {
            foreach (var (target, color) in new[] { (0, Red), (9, Red), (10, Amber), (30, Amber) })
            {
                var animation = new TrayAnimation();
                Check.True(animation.Start(ReadStatus.Ready, 100, target, false, 0, true));
                foreach (int time in new[] { 0, 67, 134, 201, 268, 335, 402, 420 })
                {
                    var frame = animation.Sample(target, false, time);
                    var image = TrayIconRasterizer.RenderFrame(64, target, frame.RingPercent,
                        sweepPhase: frame.SweepPhase);
                    Check.Equal(0, CountColor(image, Mint));
                    if (frame.RingPercent > 0) AssertPalette(image, color);
                    else foreach (var severity in SeverityColors) Check.Equal(0, CountColor(image, severity));
                }
                Check.False(animation.Sample(target, false, 420).IsActive);
            }
        });
    }

    private static void AssertPalette(RgbaImage image, Rgb expected)
    {
        Check.True(CountColor(image, expected) > 0, $"Expected visible RGB {expected} ring pixels.");
        foreach (var color in SeverityColors.Append(Neutral).Where(color => color != expected))
            Check.Equal(0, CountColor(image, color));
    }

    private static bool SameSeverityFamily(Rgb sample, Rgb severity) => severity == Mint
        ? sample.G > sample.B && sample.B > sample.R
        : severity == Amber ? sample.R > sample.G && sample.G > sample.B
        : sample.R > sample.B && sample.B > sample.G;

    private static int CountColor(RgbaImage image, Rgb color) =>
        Enumerable.Range(0, image.Width * image.Height).Count(pixel => IsColor(image, pixel * 4, color));

    private static bool IsColor(RgbaImage image, int offset, Rgb color) =>
        image.Pixels[offset] == color.R && image.Pixels[offset + 1] == color.G &&
        image.Pixels[offset + 2] == color.B && image.Pixels[offset + 3] == 255;

    private static bool SamePixel(RgbaImage a, RgbaImage b, int offset) =>
        a.Pixels.AsSpan(offset, 4).SequenceEqual(b.Pixels.AsSpan(offset, 4));

    private static void AssertSameRing(RgbaImage a, RgbaImage b, string message)
    {
        foreach (int offset in RegionOffsets(a, ring: true)) Check.True(SamePixel(a, b, offset), message);
    }

    private static IEnumerable<int> RegionOffsets(RgbaImage image, bool ring)
    {
        // A broad radial region outside the numeral bounds; no fixed edge-pixel coordinates.
        double center = image.Width / 2d;
        double boundary = image.Width * 0.41;
        double textBoundary = center - 0.65 - Math.Max(1.05, image.Width * 0.064) - 1;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            double dx = x + 0.5 - center, dy = y + 0.5 - center;
            double radiusSquared = dx * dx + dy * dy;
            if (ring ? radiusSquared > boundary * boundary : radiusSquared < textBoundary * textBoundary)
                yield return (y * image.Width + x) * 4;
        }
    }

    private static int CountRingInteriorColor(RgbaImage image, Rgb color)
    {
        double center = image.Width / 2d;
        double outer = center - 0.65;
        double inner = outer - Math.Max(1.05, image.Width * 0.064);
        int count = 0;
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            double dx = x + 0.5 - center, dy = y + 0.5 - center;
            double radius = Math.Sqrt(dx * dx + dy * dy);
            if (radius > inner + 1 && radius < outer - 1 && IsColor(image, (y * image.Width + x) * 4, color)) count++;
        }
        return count;
    }
}
