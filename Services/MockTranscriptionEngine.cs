namespace TranscriptionPipeline.Services;

/// <summary>
/// Default engine for this assessment - no cloud credentials required to run it.
/// It reads the chunk's real duration via ffprobe and produces evenly-spaced,
/// clearly-labelled placeholder segments, so the rest of the pipeline (chunking,
/// offsetting, retries, storage) is exercised exactly as it would be with a real
/// speech-to-text call. Swap "TranscriptionEngine": "AzureSpeech" in appsettings.json
/// to use <see cref="AzureSpeechTranscriptionEngine"/> instead.
/// </summary>
public class MockTranscriptionEngine(IAudioProcessor audioProcessor) : ITranscriptionEngine
{
    private const double SegmentLengthSeconds = 5.0;

    public async Task<IReadOnlyList<EngineSegment>> TranscribeChunkAsync(string chunkPath, CancellationToken ct)
    {
        var duration = await audioProcessor.GetDurationSecondsAsync(chunkPath, ct);

        var segments = new List<EngineSegment>();
        var start = 0.0;
        var segmentIndex = 0;

        while (start < duration)
        {
            var end = Math.Min(start + SegmentLengthSeconds, duration);
            segments.Add(new EngineSegment(start, end, $"[mock transcript segment {segmentIndex}]"));
            start = end;
            segmentIndex++;
        }

        return segments;
    }
}
