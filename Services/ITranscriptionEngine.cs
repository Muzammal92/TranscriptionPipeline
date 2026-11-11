namespace TranscriptionPipeline.Services;

public record EngineSegment(double StartSeconds, double EndSeconds, string Text);

public interface ITranscriptionEngine
{
    /// Transcribes a single (already-normalized) audio chunk. Timestamps are
    /// relative to the start of this chunk - the caller offsets them.
    Task<IReadOnlyList<EngineSegment>> TranscribeChunkAsync(string chunkPath, CancellationToken ct);
}
