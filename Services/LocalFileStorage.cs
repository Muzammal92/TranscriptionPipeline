namespace TranscriptionPipeline.Services;

/// <summary>
/// Local-disk implementation for this assessment. In production, raw audio belongs in
/// object storage (Azure Blob / S3) - keyed by job id, with only the resulting blob
/// path/URI and the structured transcript stored in the relational database.
/// </summary>
public class LocalFileStorage : IAudioStorage
{
    private readonly string _rootPath;

    public LocalFileStorage(IConfiguration configuration)
    {
        var configuredPath = configuration["Storage:RootPath"];
        _rootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(AppContext.BaseDirectory, "audio-storage")
            : configuredPath;
        Directory.CreateDirectory(_rootPath);
    }

    public async Task<string> SaveAsync(Guid jobId, string originalFileName, Stream content)
    {
        var jobDir = Path.Combine(_rootPath, jobId.ToString());
        Directory.CreateDirectory(jobDir);

        var safeName = Path.GetFileName(originalFileName);
        var destPath = Path.Combine(jobDir, safeName);

        await using var fileStream = File.Create(destPath);
        await content.CopyToAsync(fileStream);

        return destPath;
    }

    public string GetLocalPath(string storedPath) => storedPath;
}
