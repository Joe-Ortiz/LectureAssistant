using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using LectureAssistant.Core.Downloads;

namespace LectureAssistant.Core.Tests;

public sealed class FileDownloaderTests : IDisposable
{
    private static readonly Uri Url = new("https://example.test/model.gguf");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "la-dl-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _content = RandomNumberGenerator.GetBytes(3_000_000);

    private string Destination => Path.Combine(_dir, "model.gguf");
    private string Sha => Convert.ToHexString(SHA256.HashData(_content));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    [Fact]
    public async Task Downloads_and_verifies()
    {
        var server = new FakeServer(_content);
        await new FileDownloader(new HttpClient(server)).DownloadAsync(Url, Destination, _content.Length, Sha, null, default);

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
        Assert.False(File.Exists(FileDownloader.PartialPath(Destination)));
    }

    [Fact]
    public async Task Resumes_from_a_partial_file_with_a_range_request()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(FileDownloader.PartialPath(Destination), _content[..1_000_000]);
        var server = new FakeServer(_content);

        await new FileDownloader(new HttpClient(server)).DownloadAsync(Url, Destination, _content.Length, Sha, null, default);

        Assert.Equal(1_000_000, server.LastRangeStart);
        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task Starts_over_when_the_server_ignores_the_range()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(FileDownloader.PartialPath(Destination), _content[..1_000_000]);
        var server = new FakeServer(_content) { SupportsRanges = false };

        await new FileDownloader(new HttpClient(server)).DownloadAsync(Url, Destination, _content.Length, Sha, null, default);

        Assert.Equal(_content, await File.ReadAllBytesAsync(Destination));
    }

    [Fact]
    public async Task Rejects_and_deletes_a_corrupted_download()
    {
        var corrupted = (byte[])_content.Clone();
        corrupted[123_456] ^= 0xFF;
        var server = new FakeServer(corrupted);

        await Assert.ThrowsAsync<DownloadException>(() =>
            new FileDownloader(new HttpClient(server)).DownloadAsync(Url, Destination, _content.Length, Sha, null, default));

        Assert.False(File.Exists(Destination));
        Assert.False(File.Exists(FileDownloader.PartialPath(Destination)));
    }

    [Fact]
    public async Task Keeps_the_partial_file_when_cancelled()
    {
        var server = new FakeServer(_content) { DelayPerChunk = TimeSpan.FromMilliseconds(50) };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FileDownloader(new HttpClient(server)).DownloadAsync(Url, Destination, _content.Length, Sha, null, cts.Token));

        Assert.False(File.Exists(Destination));
        Assert.InRange(FileDownloader.PartialBytes(Destination), 1, _content.Length - 1);
    }

    [Fact]
    public async Task Reports_progress_up_to_the_total()
    {
        var reports = new List<DownloadProgress>();
        var progress = new SynchronousProgress<DownloadProgress>(reports.Add);
        await new FileDownloader(new HttpClient(new FakeServer(_content))).DownloadAsync(Url, Destination, _content.Length, Sha, progress, default);

        Assert.NotEmpty(reports);
        Assert.Equal(_content.Length, reports[^1].BytesReceived);
        Assert.Equal(1.0, reports[^1].Fraction);
    }

    [Fact]
    public void Progress_text_is_readable()
    {
        var p = new DownloadProgress(1_200_000_000, 2_600_000_000, 20_000_000);
        Assert.Equal("1.2 GB of 2.6 GB · 20 MB/s · about 2 min left", p.ToString());
    }

    private sealed class SynchronousProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }

    /// <summary>Serves a byte array, honouring Range requests unless told not to; streams slowly when asked.</summary>
    private sealed class FakeServer(byte[] content) : HttpMessageHandler
    {
        public bool SupportsRanges { get; init; } = true;
        public TimeSpan DelayPerChunk { get; init; }
        public long? LastRangeStart { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            long start = 0;
            var status = HttpStatusCode.OK;
            if (SupportsRanges && request.Headers.Range?.Ranges.FirstOrDefault()?.From is { } from)
            {
                start = from;
                LastRangeStart = from;
                status = HttpStatusCode.PartialContent;
            }
            var body = new SlowStream(content.AsMemory((int)start).ToArray(), DelayPerChunk);
            var response = new HttpResponseMessage(status) { Content = new StreamContent(body, 64 * 1024) };
            if (status == HttpStatusCode.PartialContent)
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, content.Length - 1, content.Length);
            return Task.FromResult(response);
        }
    }

    private sealed class SlowStream(byte[] data, TimeSpan delay) : MemoryStream(data)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(buffer.Length, 64 * 1024)], cancellationToken);
        }
    }
}
