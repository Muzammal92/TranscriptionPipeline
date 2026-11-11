using System.Diagnostics;
using System.Globalization;

namespace TranscriptionPipeline.Services;

public class FfmpegAudioProcessor : IAudioProcessor
{
    private readonly string _ffmpegPath;
    private readonly string _ffprobePath;
    private readonly double _chunkThresholdSeconds;
    private readonly double _chunkLengthSeconds;
    private readonly double _chunkOverlapSeconds;
    private readonly ILogger<FfmpegAudioProcessor> _logger;

    public FfmpegAudioProcessor(IConfiguration configuration, ILogger<FfmpegAudioProcessor> logger)
    {
        _ffmpegPath = configuration["Ffmpeg:FfmpegPath"] ?? "ffmpeg";
        _ffprobePath = configuration["Ffmpeg:FfprobePath"] ?? "ffprobe";
        _chunkThresholdSeconds = configuration.GetValue("Chunking:ThresholdSeconds", 120.0);
        _chunkLengthSeconds = configuration.GetValue("Chunking:ChunkLengthSeconds", 60.0);
        _chunkOverlapSeconds = configuration.GetValue("Chunking:OverlapSeconds", 5.0);
        _logger = logger;
    }

    public async Task<string> NormalizeAsync(string inputPath, string workingDirectory, CancellationToken ct)
    {
        var outputPath = Path.Combine(workingDirectory, "normalized.wav");

        // -ar 16000 -ac 1: 16kHz mono PCM - the one canonical format every downstream
        // step (chunking, transcription) is written against, regardless of what came in.
        var args = $"-y -i \"{inputPath}\" -ar 16000 -ac 1 -c:a pcm_s16le \"{outputPath}\"";
        await RunProcessAsync(_ffmpegPath, args, ct);

        return outputPath;
    }

    public async Task<double> GetDurationSecondsAsync(string audioPath, CancellationToken ct)
    {
        var args = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{audioPath}\"";
        var output = await RunProcessAsync(_ffprobePath, args, ct);

        return double.TryParse(output.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : throw new InvalidOperationException($"ffprobe did not return a parseable duration for '{audioPath}'.");
    }

    public async Task<IReadOnlyList<AudioChunk>> ChunkAsync(string normalizedAudioPath, string workingDirectory, CancellationToken ct)
    {
        var totalDuration = await GetDurationSecondsAsync(normalizedAudioPath, ct);

        if (totalDuration <= _chunkThresholdSeconds)
        {
            return [new AudioChunk(normalizedAudioPath, 0, totalDuration)];
        }

        _logger.LogInformation(
            "Audio is {Duration:F1}s, above the {Threshold}s threshold - splitting into {Length}s chunks with {Overlap}s overlap.",
            totalDuration, _chunkThresholdSeconds, _chunkLengthSeconds, _chunkOverlapSeconds);

        var chunks = new List<AudioChunk>();
        var step = _chunkLengthSeconds - _chunkOverlapSeconds;
        var start = 0.0;
        var index = 0;

        while (start < totalDuration)
        {
            var duration = Math.Min(_chunkLengthSeconds, totalDuration - start);
            var chunkPath = Path.Combine(workingDirectory, $"chunk_{index:D4}.wav");

            var args = $"-y -i \"{normalizedAudioPath}\" -ss {start.ToString(CultureInfo.InvariantCulture)} " +
                       $"-t {duration.ToString(CultureInfo.InvariantCulture)} -c copy \"{chunkPath}\"";
            await RunProcessAsync(_ffmpegPath, args, ct);

            chunks.Add(new AudioChunk(chunkPath, start, duration));

            index++;
            start += step;
        }

        return chunks;
    }

    private static async Task<string> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var stdOutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stdErrTask = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct);
        var stdOut = await stdOutTask;
        var stdErr = await stdErrTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{fileName}' exited with code {process.ExitCode}: {stdErr}");
        }

        return string.IsNullOrWhiteSpace(stdOut) ? stdErr : stdOut;
    }
}
