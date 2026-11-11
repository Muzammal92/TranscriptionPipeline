using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;

namespace TranscriptionPipeline.Services;

/// <summary>
/// Real implementation, used in production once Azure Speech credentials are configured
/// (Speech:SubscriptionKey / Speech:Region in appsettings or environment variables -
/// never hard-coded). Runs continuous recognition over the chunk and collects each
/// recognized phrase with its offset/duration, which is exactly the segment shape
/// the rest of the pipeline expects.
/// </summary>
public class AzureSpeechTranscriptionEngine : ITranscriptionEngine
{
    private readonly string _subscriptionKey;
    private readonly string _region;

    public AzureSpeechTranscriptionEngine(IConfiguration configuration)
    {
        _subscriptionKey = configuration["Speech:SubscriptionKey"]
            ?? throw new InvalidOperationException("Speech:SubscriptionKey is not configured.");
        _region = configuration["Speech:Region"]
            ?? throw new InvalidOperationException("Speech:Region is not configured.");
    }

    public async Task<IReadOnlyList<EngineSegment>> TranscribeChunkAsync(string chunkPath, CancellationToken ct)
    {
        var speechConfig = SpeechConfig.FromSubscription(_subscriptionKey, _region);
        using var audioConfig = AudioConfig.FromWavFileInput(chunkPath);
        using var recognizer = new SpeechRecognizer(speechConfig, audioConfig);

        var segments = new List<EngineSegment>();
        var done = new TaskCompletionSource();

        recognizer.Recognized += (_, e) =>
        {
            if (e.Result.Reason == ResultReason.RecognizedSpeech && !string.IsNullOrWhiteSpace(e.Result.Text))
            {
                var start = e.Result.OffsetInTicks / (double)TimeSpan.TicksPerSecond;
                var end = start + e.Result.Duration.TotalSeconds;
                segments.Add(new EngineSegment(start, end, e.Result.Text));
            }
        };
        recognizer.SessionStopped += (_, _) => done.TrySetResult();
        recognizer.Canceled += (_, e) => done.TrySetException(
            new InvalidOperationException($"Azure Speech recognition canceled: {e.Reason} - {e.ErrorDetails}"));

        await using (ct.Register(() => done.TrySetCanceled(ct)))
        {
            await recognizer.StartContinuousRecognitionAsync();
            await done.Task;
            await recognizer.StopContinuousRecognitionAsync();
        }

        return segments;
    }
}
