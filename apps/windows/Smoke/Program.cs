using System.Buffers.Binary;
using Ovi.Screenshot.Interop;

string destination = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(AppContext.BaseDirectory, "core-smoke.png");
try
{
    // Padded rows prove native input stride is respected and no input pointer is retained.
    byte[] pixels = [255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 99, 99, 99, 99,
                    255, 255, 255, 255, 0, 0, 0, 255, 255, 0, 255, 255];
    using var document = ImageDocument.Import(ImageDescriptor.Rgba(3, 2, 16), pixels);
    Array.Fill(pixels, (byte)0); // Rust must own its copied source.
    var info = document.Info;
    Require(info.Image.Width == 3 && info.Image.Height == 2 && info.Image.StrideBytes == 12, "Imported dimensions/stride mismatch.");
    var preview = document.RenderPreview(3, 2);
    Require(preview.Image.Width == 3 && preview.Image.Height == 2 && preview.Rgba.Length == 24, "Preview descriptor mismatch.");
    Require(preview.Rgba[0] == 255 && preview.Rgba[3] == 255 && preview.Rgba[12] == 255, "Copied source pixels or padded row import corrupted.");
    var scaled = document.RenderPreview(2, 2);
    Require(scaled.Image.Width <= 2 && scaled.Image.Height <= 2 && scaled.Rgba.Length == (int)(scaled.Image.StrideBytes * scaled.Image.Height), "Scaled preview layout mismatch.");
    byte[] png = document.ExportPng();
    byte[] signature = [137, 80, 78, 71, 13, 10, 26, 10];
    Require(png.AsSpan(0, 8).SequenceEqual(signature), "PNG signature mismatch.");
    Require(BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) == 3 && BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) == 2, "PNG dimensions mismatch.");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    File.WriteAllBytes(destination, png);
    for (int index = 0; index < 100; index++)
    {
        using var repeated = ImageDocument.Import(ImageDescriptor.Rgba(1, 1, 4), [10, 20, 30, 255]);
        Require(repeated.ExportPng().Length > 24, "Repeated import/export failed.");
    }
    using var region = ImageDocument.ImportBgraRegion(2, 1, [30, 20, 10, 255, 60, 50, 40, 255], 1, 0, 1, 1);
    Require(region.RenderPreview(1, 1).Rgba.AsSpan().SequenceEqual(new byte[] { 40, 50, 60, 255 }), "BGRA region import changed channels.");
    using var styled = region.AddWindowShadow();
    Require(styled.Info.Image.Width == 49 && styled.Info.Image.Height == 49 && region.Info.Image.Width == 1, "Styled document changed its source or padding.");
    Require(styled.RenderPreview(49, 49).Rgba[3] == 0, "Styled document lost its transparent margin.");
    Console.WriteLine($"PASS ABI v2; RGBA/BGRA region ownership; styled document; preview dimensions; PNG 3x2; 100 import/export/release cycles. Output: {destination}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"FAIL {error.Message}");
    return 1;
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
