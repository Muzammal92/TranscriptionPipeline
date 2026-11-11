using Microsoft.EntityFrameworkCore;
using TranscriptionPipeline.Data;
using TranscriptionPipeline.Models;
using TranscriptionPipeline.Services;

namespace TranscriptionPipeline.Endpoints;

public static class TranscriptionEndpoints
{
    public static IEndpointRouteBuilder MapTranscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/transcribe", SubmitTranscriptionAsync)
            .DisableAntiforgery()
            .RequireAuthorization()
            .WithName("SubmitTranscription");

        app.MapGet("/api/transcribe/{id:guid}", GetTranscriptionAsync)
            .RequireAuthorization()
            .WithName("GetTranscription");

        return app;
    }

    private static async Task<IResult> SubmitTranscriptionAsync(
        IFormFile file,
        IAudioValidator validator,
        IAudioStorage storage,
        ITranscriptionQueue queue,
        AppDbContext db,
        CancellationToken ct)
    {
        if (file.Length == 0)
        {
            return Results.BadRequest(new { error = "No file was uploaded." });
        }

        const long maxFileSizeBytes = 200 * 1024 * 1024;
        if (file.Length > maxFileSizeBytes)
        {
            return Results.BadRequest(new { error = "File exceeds the 200 MB limit." });
        }

        await using (var stream = file.OpenReadStream())
        {
            if (!await validator.IsSupportedAudioAsync(stream))
            {
                return Results.BadRequest(new { error = "Unsupported or corrupt audio file. Only WAV and MP3 are accepted." });
            }
        }

        var jobId = Guid.NewGuid();

        await using (var stream = file.OpenReadStream())
        {
            var storedPath = await storage.SaveAsync(jobId, file.FileName, stream);

            db.Jobs.Add(new TranscriptionJob
            {
                Id = jobId,
                OriginalFileName = file.FileName,
                StoredAudioPath = storedPath,
                Status = JobStatus.Pending
            });
            await db.SaveChangesAsync(ct);
        }

        // Enqueue and return immediately - transcription happens on a background
        // worker, so concurrent uploads never queue up behind each other at the API.
        await queue.EnqueueAsync(jobId, ct);

        return Results.Accepted($"/api/transcribe/{jobId}", new { jobId });
    }

    private static async Task<IResult> GetTranscriptionAsync(Guid id, AppDbContext db, CancellationToken ct)
    {
        var job = await db.Jobs
            .Include(j => j.Segments)
            .FirstOrDefaultAsync(j => j.Id == id, ct);

        return job is null
            ? Results.NotFound(new { error = $"No job found with id '{id}'." })
            : Results.Ok(TranscriptionJobDto.FromEntity(job));
    }
}
