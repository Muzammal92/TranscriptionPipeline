using System.Threading.Channels;

namespace TranscriptionPipeline.Services;

/// <summary>
/// In-memory, thread-safe queue so concurrent uploads never block each other at the API
/// layer - a request enqueues and returns immediately. Swapping this for Azure Storage
/// Queue / Service Bus (same interface) is the natural next step for multi-instance scale.
/// </summary>
public interface ITranscriptionQueue
{
    ValueTask EnqueueAsync(Guid jobId, CancellationToken ct);
    IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct);
}

public class TranscriptionQueue : ITranscriptionQueue
{
    private readonly Channel<Guid> _channel = Channel.CreateUnbounded<Guid>();

    public ValueTask EnqueueAsync(Guid jobId, CancellationToken ct) => _channel.Writer.WriteAsync(jobId, ct);

    public IAsyncEnumerable<Guid> DequeueAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);
}
