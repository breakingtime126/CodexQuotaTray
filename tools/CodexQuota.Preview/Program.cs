using System.Buffers.Binary;
using System.Text.Json;
using CodexQuota.Core;

string outputDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "docs/previews");
Directory.CreateDirectory(outputDirectory);
(int? Value, bool Stale, string Name, string Label)[] examples =
[
    (0, false, "0", "0% remaining"),
    (7, false, "7", "7% remaining"),
    (9, false, "9", "9% remaining · red"),
    (10, false, "10", "10% remaining · amber"),
    (11, false, "11", "11% remaining"),
    (30, false, "30", "30% remaining · amber"),
    (31, false, "31", "31% remaining · green"),
    (68, false, "68", "68% remaining"),
    (88, false, "88", "88% remaining"),
    (97, false, "97", "97% remaining"),
    (100, false, "100", "100% remaining"),
    (null, false, "unknown", "Unknown"),
    (68, true, "stale-68", "68% · stale")
];
int[] sizes = [16, 20, 24, 32, 64];
var manifest = new List<object>();
foreach (var example in examples)
{
    foreach (int size in sizes)
    {
        var image = TrayIconRasterizer.Render(size, example.Value, example.Stale);
        if (image.Pixels.Length != size * size * 4 || image.Pixels[3] != 0)
            throw new InvalidDataException($"RGBA size/transparency self-check failed for {example.Name}/{size}.");
        byte[] png = TrayIconRasterizer.EncodePng(image);
        if (!png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)) != size ||
            BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)) != size)
            throw new InvalidDataException("PNG header self-check failed.");
        string filename = $"quota-{example.Name}-{size}.png";
        File.WriteAllBytes(Path.Combine(outputDirectory, filename), png);
        manifest.Add(new { file = filename, size, percent = example.Value, stale = example.Stale, label = example.Label });
    }
    var ico = TrayIconRasterizer.EncodeIco(example.Value, example.Stale);
    if (BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4, 2)) != sizes.Length)
        throw new InvalidDataException("ICO frame count self-check failed.");
    for (int index = 0; index < sizes.Length; index++)
    {
        int entry = 6 + index * 16;
        int length = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 8, 4));
        int offset = BinaryPrimitives.ReadInt32LittleEndian(ico.AsSpan(entry + 12, 4));
        if (ico[entry] != sizes[index] || offset + length > ico.Length || ico[offset] != 137)
            throw new InvalidDataException("ICO directory self-check failed.");
    }
    File.WriteAllBytes(Path.Combine(outputDirectory, $"quota-{example.Name}.ico"), ico);
}
// These sequential frames show a same-hue glint on the true quota arc, never a success-fill.
foreach (int value in new[] { 7, 30, 97 })
for (int index = 0; index <= 6; index++)
    File.WriteAllBytes(Path.Combine(outputDirectory, $"quota-glint-{value}-{index}.png"),
        TrayIconRasterizer.EncodePng(TrayIconRasterizer.RenderFrame(32, value, value, sweepPhase: index / 6d)));
File.WriteAllText(Path.Combine(outputDirectory, "manifest.json"), JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
foreach (var size in sizes)
foreach (var value in new int?[] { 0, 9, 30, 88, 100, null })
    File.WriteAllBytes(Path.Combine(outputDirectory, $"quota-notification-{value?.ToString() ?? "unknown"}-{size}.png"),
        TrayIconRasterizer.EncodePng(TrayIconRasterizer.RenderFrame(size, value, value, notification: true)));
Console.WriteLine($"Rendered {examples.Length * sizes.Length} PNGs and {examples.Length} ICOs to {outputDirectory}");
Console.WriteLine("Passed RGBA, transparency, PNG-header, and ICO-directory self-checks.");
Console.WriteLine("These are code-rendered artwork previews, not Windows screenshots.");
