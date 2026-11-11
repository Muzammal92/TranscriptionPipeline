namespace TranscriptionPipeline.Models;

public class TranscriptSegment
{
    public int Id { get; set; }
    public Guid TranscriptionJobId { get; set; }
    public TranscriptionJob? Job { get; set; }

    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
    public string Text { get; set; } = string.Empty;
}
