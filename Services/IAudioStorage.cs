namespace TranscriptionPipeline.Services;

public interface IAudioStorage
{
    /// Returns the path/key the file was stored under. A cloud implementation
    /// (Azure Blob, S3) would return a blob URI instead of a local path.
    Task<string> SaveAsync(Guid jobId, string originalFileName, Stream content);

    string GetLocalPath(string storedPath);
}
