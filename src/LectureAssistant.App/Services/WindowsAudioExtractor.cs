using LectureAssistant.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace LectureAssistant.App.Services;

/// <summary>
/// Pulls a 16 kHz mono PCM WAV out of a video with Windows' built-in Media Foundation,
/// so the app doesn't need to bundle FFmpeg. Handles whatever codecs Windows can decode (MP4/H.264/AAC, MOV, WMV, MKV...).
/// </summary>
public sealed class WindowsAudioExtractor : IAudioExtractor
{
    public async Task ExtractAsync(string videoPath, string outputWavPath, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var input = await StorageFile.GetFileFromPathAsync(videoPath);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(outputWavPath)!);
        var output = await folder.CreateFileAsync(Path.GetFileName(outputWavPath), CreationCollisionOption.ReplaceExisting);

        var profile = MediaEncodingProfile.CreateWav(AudioEncodingQuality.Low);
        profile.Audio = AudioEncodingProperties.CreatePcm(16000, 1, 16);

        var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
        var prepared = await transcoder.PrepareFileTranscodeAsync(input, output, profile);
        if (!prepared.CanTranscode)
            throw new InvalidOperationException(prepared.FailureReason switch
            {
                TranscodeFailureReason.CodecNotFound =>
                    "Windows can't decode this video's audio. Try converting it to MP4 (H.264/AAC) first.",
                TranscodeFailureReason.InvalidProfile => "The audio format couldn't be configured.",
                _ => "Windows couldn't read audio from this video.",
            });

        var operation = prepared.TranscodeAsync();
        if (progress is not null) operation.Progress = (_, percent) => progress.Report(percent / 100);
        await operation.AsTask(cancellationToken);
    }

    public static async Task<TimeSpan?> GetDurationAsync(string videoPath)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(videoPath);
            var properties = await file.Properties.GetVideoPropertiesAsync();
            return properties.Duration > TimeSpan.Zero ? properties.Duration : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
