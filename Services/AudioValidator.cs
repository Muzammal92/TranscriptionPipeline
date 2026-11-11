namespace TranscriptionPipeline.Services;

public class AudioValidator : IAudioValidator
{
    public async Task<bool> IsSupportedAudioAsync(Stream fileStream)
    {
        var header = new byte[12];
        var position = fileStream.Position;
        var read = await fileStream.ReadAsync(header.AsMemory(0, header.Length));
        fileStream.Position = position;

        if (read < 12)
        {
            return false;
        }

        // WAV: "RIFF....WAVE"
        var isWav = header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F'
                    && header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';

        // MP3: "ID3" tag, or an MPEG frame sync (0xFF, 0xFB/0xF3/0xF2)
        var isMp3 = (header[0] == 'I' && header[1] == 'D' && header[2] == '3')
                    || (header[0] == 0xFF && (header[1] == 0xFB || header[1] == 0xF3 || header[1] == 0xF2));

        return isWav || isMp3;
    }
}
