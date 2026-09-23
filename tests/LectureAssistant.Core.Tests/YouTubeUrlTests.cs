using LectureAssistant.Core.YouTube;

namespace LectureAssistant.Core.Tests;

public class YouTubeUrlTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=42s")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc")]
    [InlineData("youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube-nocookie.com/embed/dQw4w9WgXcQ?rel=0")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    [InlineData("  dQw4w9WgXcQ  ")]
    public void Extracts_video_id(string input)
    {
        Assert.True(YouTubeUrl.TryGetVideoId(input, out var id));
        Assert.Equal("dQw4w9WgXcQ", id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://vimeo.com/123456")]
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/channel/UCabcdefghijk")]
    [InlineData("https://evil.example/watch?v=dQw4w9WgXcQ")]
    public void Rejects_non_video_urls(string? input)
    {
        Assert.False(YouTubeUrl.TryGetVideoId(input, out _));
    }
}
