using Microsoft.EntityFrameworkCore;
using Polly;
using Polly.Retry;
using TranscriptionPipeline.Data;
using TranscriptionPipeline.Models;
using TranscriptionPipeline.Services;

namespace TranscriptionPipeline.Workers;

/// <summary>
/// Hosts N concurrent consumers (WorkerConcurrency, default 2) over the same queue, so
/// multiple jobs are actually transcribed in parallel rather than one at a time - this
/// is the concrete answer to "how would you handle concurrent uploads?" beyond just
/// accepting them non-blockingly at the API layer.
/// </summary>
public class TranscriptionWorker : BackgroundService
{
    private readonly ITranscriptionQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TranscriptionWorker> _logger;
    private readonly int _concurrency;
    private readonly ResiliencePipeline _retryPipeline;

    public TranscriptionWorker(
        ITranscriptionQueue queue,
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<TranscriptionWorker> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _concurrency = configuration.GetValue("Worker:Concurrency", 2);

        // Transient failures (engine timeout, transient Azure error) get retried with
        // exponential backoff; anything still failing after 3 attempts is surfaced as
        // Failed rather than retried forever.
        _retryPipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromSeconds(2),
                OnRetry = args =>
                {
                    _logger.LogWarning(
                        args.Outcome.Exception,
                        "Transcription attempt {Attempt} failed, retrying.",
                        args.AttemptNumber + 1);
                    return ValueTask.CompletedTask;
                }
            })
            .Build();
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = Enumerable.Range(0, _concurrency)
            .Select(_ => ConsumeAsync(stoppingToken));

        return Task.WhenAll(consumers);
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var jobId in _queue.DequeueAllAsync(ct))
        {
            try
            {
                await ProcessJobAsync(jobId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled error processing job {JobId}.", jobId);
            }
        }
    }

    private async Task ProcessJobAsync(Guid jobId, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audioProcessor = scope.ServiceProvider.GetRequiredService<IAudioProcessor>();
        var engine = scope.ServiceProvider.GetRequiredService<ITranscriptionEngine>();

        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == jobId, ct);
        if (job is null)
        {
            _logger.LogWarning("Job {JobId} was queued but no longer exists.", jobId);
            return;
        }

        job.Status = JobStatus.Processing;
        await db.SaveChangesAsync(ct);

        var workingDirectory = Path.Combine(Path.GetTempPath(), "transcription-pipeline", jobId.ToString());
        Directory.CreateDirectory(workingDirectory);

        try
        {
            var normalizedPath = await audioProcessor.NormalizeAsync(job.StoredAudioPath, workingDirectory, ct);
            var chunks = await audioProcessor.ChunkAsync(normalizedPath, workingDirectory, ct);

            foreach (var chunk in chunks)
            {
                var chunkSegments = await _retryPipeline.ExecuteAsync(
                    async token => await engine.TranscribeChunkAsync(chunk.FilePath, token), ct);

                foreach (var seg in chunkSegments)
                {
                    db.Segments.Add(new TranscriptSegment
                    {
                        TranscriptionJobId = job.Id,
                        StartSeconds = chunk.StartOffsetSeconds + seg.StartSeconds,
                        EndSeconds = chunk.StartOffsetSeconds + seg.EndSeconds,
                        Text = seg.Text
                    });
                }
            }

            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} failed after retries.", jobId);
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
        }
        finally
        {
            job.AttemptCount++;
            await db.SaveChangesAsync(ct);

            try { Directory.Delete(workingDirectory, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }
}
