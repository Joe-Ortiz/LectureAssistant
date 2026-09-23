using System.IO.Compression;
using System.Text;

namespace LectureAssistant.Export;

internal static class ZipWriting
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task AddTextAsync(this ZipArchive zip, string path, string text, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(cancellationToken);
        var bytes = Utf8NoBom.GetBytes(text);
        await stream.WriteAsync(bytes, cancellationToken);
    }

    public static async Task AddStreamAsync(this ZipArchive zip, string path, Stream source, CancellationToken cancellationToken)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        await using var stream = await entry.OpenAsync(cancellationToken);
        await source.CopyToAsync(stream, cancellationToken);
    }
}
