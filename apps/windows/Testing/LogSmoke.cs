using System.Text;
using Ovi.Screenshot.Capture;
using Ovi.Screenshot.Services;

namespace Ovi.Screenshot.Testing;

internal static class LogSmoke
{
    internal static async Task VerifyAsync(string directory)
    {
        var paths = new AppPaths(directory);
        Directory.CreateDirectory(paths.Root);
        var log = new AppLog(paths);
        string previous = paths.Log + ".previous";
        int seededBytes = AppLog.MaxFileBytes - 16;
        await File.WriteAllTextAsync(previous, "obsolete archive", new UTF8Encoding(false));
        await File.WriteAllTextAsync(paths.Log, new string('r', seededBytes), new UTF8Encoding(false));
        await CaptureTiming.WriteAsync(log, "fixture.rotate", TimeSpan.FromMilliseconds(1.25), 320, 200);
        Require(new FileInfo(previous).Length == seededBytes, "timing messages did not rotate and replace the previous archive");
        Require((await File.ReadAllTextAsync(paths.Log)).Contains("capture-timing fixture.rotate 1.25ms 320x200", StringComparison.Ordinal),
            "timing messages did not use the common log writer");

        await Task.WhenAll(Enumerable.Range(0, 64).Select(index => index % 2 == 0
            ? CaptureTiming.WriteAsync(log, $"fixture.concurrent-{index:D3}", TimeSpan.Zero, 320, 200)
            : Task.Run(() => log.Write(new InvalidOperationException($"fixture.concurrent-{index:D3}")))));
        string[] lines = await File.ReadAllLinesAsync(paths.Log);
        for (int index = 0; index < 64; index++)
        {
            string marker = $"fixture.concurrent-{index:D3}";
            Require(lines.Count(line => line.Contains(marker, StringComparison.Ordinal)) == 1,
                "concurrent timing/exception writes lost or interleaved an entry");
        }

        // The cutoff lands between a surrogate pair in this valid input.
        string huge = new string('界', AppLog.MaxMessageChars - 1) + "😀" + new string('x', AppLog.MaxMessageChars * 32);
        long before = new FileInfo(paths.Log).Length;
        log.Write(huge);
        long after = new FileInfo(paths.Log).Length;
        Require(after - before <= AppLog.MaxEntryBytes, "one oversized Unicode message exceeded the entry budget");
        before = after;
        log.Write(new InvalidOperationException(huge));
        after = new FileInfo(paths.Log).Length;
        Require(after - before <= AppLog.MaxEntryBytes, "one oversized exception exceeded the entry budget");
        string text = await File.ReadAllTextAsync(paths.Log, new UTF8Encoding(false, true));
        Require(text.Contains("[truncated]", StringComparison.Ordinal) && !text.Contains('\ufffd'),
            "message truncation was missing or split a Unicode surrogate pair");
        Require(new FileInfo(paths.Log).Length <= AppLog.MaxFileBytes && new FileInfo(previous).Length <= AppLog.MaxFileBytes,
            "current or previous log exceeded the file budget");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Log regression: " + message + ".");
    }
}
