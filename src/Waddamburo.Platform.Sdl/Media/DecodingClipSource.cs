namespace Waddamburo.Platform.Sdl.Media;

/// <summary>
/// A clip still decoding on another thread, as a stream: silence until it is ready (so a stop's fade ends it there too),
/// then the clip, looping where the file says. Playing a file never waits for its decoding.
/// </summary>
public sealed class DecodingClipSource(SdlAudioFormat format, Task<AudioClip> decoding, bool loop) : IAudioStreamSource
{
    private int _position; // in frames

    /// <summary>Read ahead of the output (device voices): nothing until decoded, or the silence would queue ahead of it.</summary>
    internal bool ReadAhead { get; set; }

    public SdlAudioFormat Format { get; } = format;

    public bool IsCompleted => decoding.IsCompleted && (Failure is not null || !loop && _position >= decoding.Result.FrameCount);

    public Exception? Failure => decoding.IsFaulted ? decoding.Exception!.InnerException : null;

    public int Read(Span<float> interleavedDestination)
    {
        if (!decoding.IsCompletedSuccessfully)
        {
            if (ReadAhead)
                return 0;
            interleavedDestination.Clear();
            return interleavedDestination.Length;
        }
        var clip = decoding.Result;
        var end = loop ? clip.LoopRegion?.EndFrame ?? clip.FrameCount : clip.FrameCount;
        var written = 0;
        while (written < interleavedDestination.Length)
        {
            if (_position >= end)
            {
                if (!loop)
                    break;
                _position = clip.LoopRegion?.StartFrame ?? 0;
            }
            var samples = clip.Samples[(_position * Format.Channels)..(end * Format.Channels)];
            var count = Math.Min(samples.Length, interleavedDestination.Length - written);
            samples[..count].CopyTo(interleavedDestination[written..]);
            written += count;
            _position += count / Format.Channels;
        }
        return written;
    }

    public void Dispose()
    {
    }
}
