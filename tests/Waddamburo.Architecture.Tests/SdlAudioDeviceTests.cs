using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.Architecture.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SdlAudioTestGroup
{
    public const string Name = "SDL audio";
}

[Collection(SdlAudioTestGroup.Name)]
public sealed class SdlAudioDeviceTests
{
    [Fact]
    public void PlaybackDeviceAcceptsAndClearsInterleavedFloatFrames()
    {
        using var device = new SdlAudioDevice();
        var samples = new float[256 * device.Format.Channels];

        device.Queue(samples);

        Assert.Equal(new SdlAudioFormat(48000, 2), device.Format);
        Assert.Equal(256UL, device.SubmittedFrames);
        Assert.Equal(256UL, device.QueuedFrames);
        Assert.False(string.IsNullOrWhiteSpace(device.Driver));
        Assert.True(device.HardwareBufferFrames > 0);
        device.Clear();
        Assert.Equal(0UL, device.QueuedFrames);
        device.Resume();
        device.Pause();
    }

    [Fact]
    public void QueueRequiresWholeFramesAndDisposedDeviceRejectsUse()
    {
        var device = new SdlAudioDevice();

        Assert.Throws<ArgumentException>(() => device.Queue(new float[3]));
        device.Dispose();
        device.Dispose();
        Assert.Throws<ObjectDisposedException>(() => device.Resume());
    }
}
