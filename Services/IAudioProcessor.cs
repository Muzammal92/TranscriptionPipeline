namespace TranscriptionPipeline.Services;

public record AudioChunk(string FilePath, double StartOffsetSeconds, double DurationSeconds);

public interface IAudioProcessor
{
    /// Normalizes any supported input (WAV/MP3/etc.) to 16kHz mono PCM WAV,
    /// so the transcription engine only ever has to deal with one format.
    Task<string> NormalizeAsync(string inputPath, string workingDirectory, CancellationToken ct);

    Task<double> GetDurationSecondsAsync(string audioPath, CancellationToken ct);

    /// Splits long audio into overlapping chunks so a single very long file
    /// doesn't have to be transcribed in one pass. Short files come back as a single chunk.
    Task<IReadOnlyList<AudioChunk>> ChunkAsync(string normalizedAudioPath, string workingDirectory, CancellationToken ct);
}
