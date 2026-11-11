namespace TranscriptionPipeline.Services;

public interface IAudioValidator
{
    /// Checks magic bytes rather than trusting the filename/extension.
    Task<bool> IsSupportedAudioAsync(Stream fileStream);
}
