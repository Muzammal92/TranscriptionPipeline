namespace TranscriptionPipeline.Models;

public record SegmentDto(double Start, double End, string Text);

public record TranscriptionJobDto(
    Guid Id,
    string OriginalFileName,
    string Status,
    string? ErrorMessage,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<SegmentDto> Segments)
{
    public static TranscriptionJobDto FromEntity(TranscriptionJob job) => new(
        job.Id,
        job.OriginalFileName,
        job.Status.ToString(),
        job.ErrorMessage,
        job.AttemptCount,
        job.CreatedAt,
        job.CompletedAt,
        job.Segments
            .OrderBy(s => s.StartSeconds)
            .Select(s => new SegmentDto(s.StartSeconds, s.EndSeconds, s.Text))
            .ToList());
}
