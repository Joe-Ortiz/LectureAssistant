using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LectureAssistant.Core.Downloads;

/// <param name="BytesReceived">Bytes on disk so far, including any resumed part.</param>
/// <param name="TotalBytes">Expected size.</param>
/// <param name="BytesPerSecond">Recent transfer rate; 0 until measurable.</param>
public sealed record DownloadProgress(long BytesReceived, long TotalBytes, double BytesPerSecond)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)BytesReceived / TotalBytes, 0, 1) : 0;

    public TimeSpan? Remaining => BytesPerSecond > 0
        ? TimeSpan.FromSeconds((TotalBytes - BytesReceived) / BytesPerSecond)
        : null;

    /// <summary>"1.2 GB of 2.6 GB · 18 MB/s · about 2 min left"</summary>
    public override string ToString()
    {
        var text = $"{FormatBytes(BytesReceived)} of {FormatBytes(TotalBytes)}";
        if (BytesPerSecond > 0) text += $" · {FormatBytes((long)BytesPerSecond)}/s";
        if (Remaining is { } left) text += " · " + FormatRemaining(left);
        return text;
    }

    public static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1e9:0.0} GB",
        >= 1_000_000 => $"{bytes / 1e6:0} MB",
        _ => $"{bytes / 1e3:0} KB",
    };

    private static string FormatRemaining(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => "less than a minute left",
        < 60 => $"about {Math.Ceiling(t.TotalMinutes):0} min left",
        _ => $"about {t.TotalHours:0.#} h left",
    };
}

public sealed class DownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Downloads large files (AI models) reliably: resumes interrupted downloads from a <c>.partial</c> file,
/// verifies the SHA-256 before the file is used, and checks free disk space first.
/// </summary>
public sealed class FileDownloader(HttpClient http)
{
    private const string PartialSuffix = ".partial";

    public static string PartialPath(string destination) => destination + PartialSuffix;

    /// <summary>Bytes already downloaded for <paramref name="destination"/> by an interrupted download.</summary>
    public static long PartialBytes(string destination)
    {
        var partial = new FileInfo(PartialPath(destination));
        return partial.Exists ? partial.Length : 0;
    }

    public async Task DownloadAsync(
        Uri url,
        string destination,
        long expectedBytes,
        string expectedSha256,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (File.Exists(destination)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var partialPath = PartialPath(destination);

        long existing = PartialBytes(destination);
        if (existing > expectedBytes)
        {
            File.Delete(partialPath);
            existing = 0;
        }
        EnsureFreeSpace(destination, expectedBytes - existing);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        if (existing > 0) await HashExistingAsync(partialPath, hash, cancellationToken);

        if (existing < expectedBytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0) request.Headers.Range = new RangeHeaderValue(existing, null);

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                throw new DownloadException("Couldn't connect to the download server. Check your internet connection and try again.", ex);
            }

            using (response)
            {
                if (existing > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    // Server ignored the range request; start over from zero.
                    response.Dispose();
                    File.Delete(partialPath);
                    await DownloadAsync(url, destination, expectedBytes, expectedSha256, progress, cancellationToken);
                    return;
                }
                if (!response.IsSuccessStatusCode)
                    throw new DownloadException($"The download server returned an error ({(int)response.StatusCode}). Please try again later.");

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var target = new FileStream(partialPath, existing > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
                try
                {
                    await CopyAsync(source, target, hash, existing, expectedBytes, progress, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException && !cancellationToken.IsCancellationRequested)
                {
                    throw new DownloadException("The download was interrupted. Try again; it will continue where it stopped.", ex);
                }
            }
        }

        var actual = Convert.ToHexString(hash.GetHashAndReset());
        if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(partialPath);
            throw new DownloadException("The downloaded file was damaged in transit and has been removed. Please try the download again.");
        }
        File.Move(partialPath, destination, overwrite: true);
    }

    private static async Task CopyAsync(Stream source, Stream target, IncrementalHash hash, long received, long total,
        IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = new byte[1 << 20];
        var clock = Stopwatch.StartNew();
        long windowStartBytes = received;
        var windowStart = clock.Elapsed;
        double rate = 0;
        var lastReport = TimeSpan.Zero;

        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            hash.AppendData(buffer, 0, read);
            received += read;

            var now = clock.Elapsed;
            if (now - windowStart >= TimeSpan.FromSeconds(2))
            {
                var instant = (received - windowStartBytes) / (now - windowStart).TotalSeconds;
                rate = rate == 0 ? instant : rate * 0.7 + instant * 0.3;
                windowStart = now;
                windowStartBytes = received;
            }
            if (now - lastReport >= TimeSpan.FromMilliseconds(250))
            {
                progress?.Report(new DownloadProgress(received, total, rate));
                lastReport = now;
            }
        }
        progress?.Report(new DownloadProgress(received, total, rate));

        if (received < total)
            throw new DownloadException("The download was interrupted. Try again; it will continue where it stopped.");
    }

    private static async Task HashExistingAsync(string path, IncrementalHash hash, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var buffer = new byte[1 << 20];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0) hash.AppendData(buffer, 0, read);
    }

    private static void EnsureFreeSpace(string destination, long needed)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(destination));
        if (string.IsNullOrEmpty(root)) return;
        var drive = new DriveInfo(root);
        const long headroom = 500_000_000;
        if (drive.IsReady && drive.AvailableFreeSpace < needed + headroom)
            throw new DownloadException(
                $"Not enough free disk space. The download needs {DownloadProgress.FormatBytes(needed + headroom)} free on drive {drive.Name.TrimEnd('\\')}, " +
                $"but only {DownloadProgress.FormatBytes(drive.AvailableFreeSpace)} is available.");
    }
}
