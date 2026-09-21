using Waddamburo.Platform.Sdl.Media;

namespace Waddamburo.Architecture.Tests;

public sealed class AudioMixerTests
{
    private static readonly SdlAudioFormat StereoFourHertz = new(4, 2);

    [Fact]
    public void VoicesMixAcrossBusesWithVolumeAndClipping()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var first = clip(0.75f, 0.75f);
        var second = clip(0.5f, -0.5f);
        mixer.SetBusVolume(AudioBus.MenuSound, 0.5f);
        mixer.Play(first, AudioBus.Bgm);
        mixer.Play(second, AudioBus.MenuSound);
        var output = new float[2];

        Assert.True(mixer.Render(output));

        Assert.Equal([1f, 0.5f], output);
        Assert.False(mixer.HasActiveVoices);
    }

    [Fact]
    public void MutedBusAdvancesVoiceAndLoopWraps()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var source = new AudioClip(StereoFourHertz, [0.1f, 0.2f, 0.3f, 0.4f]);
        mixer.SetBusMuted(AudioBus.MenuSound, true);
        mixer.Play(source, AudioBus.MenuSound, loop: true);
        var first = new float[2];
        mixer.Render(first);
        mixer.SetBusMuted(AudioBus.MenuSound, false);
        var following = new float[6];

        mixer.Render(following);

        Assert.Equal([0.3f, 0.4f, 0.1f, 0.2f, 0.3f, 0.4f], following);
    }

    [Fact]
    public void AuthoredLoopRegionPlaysIntroOnceAndWrapsBeforeTheTail()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var source = new AudioClip(
            StereoFourHertz,
            [0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f, 0.4f, 0.4f, 0.9f, 0.9f],
            new AudioLoopRegion(1, 4));
        mixer.Play(source, AudioBus.Bgm, loop: true);
        var output = new float[14];

        mixer.Render(output);

        Assert.Equal(
            [0.1f, 0.1f, 0.2f, 0.2f, 0.3f, 0.3f, 0.4f, 0.4f,
             0.2f, 0.2f, 0.3f, 0.3f, 0.4f, 0.4f],
            output);
    }

    [Fact]
    public void StopFadeReachesSilenceAndRemovesVoice()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var handle = mixer.Play(
            new AudioClip(StereoFourHertz, [1f, 1f, 1f, 1f, 1f, 1f, 1f, 1f]),
            AudioBus.Voice);
        mixer.Stop(handle, TimeSpan.FromSeconds(0.5));
        var output = new float[6];

        mixer.Render(output);

        Assert.Equal([1f, 1f, 0.5f, 0.5f, 0f, 0f], output);
        Assert.False(mixer.HasActiveVoices);
    }

    [Fact]
    public void ClipCopiesCallerSamplesAndRejectsMismatchedMixerFormat()
    {
        var source = new[] { 0.25f, -0.25f };
        var audioClip = new AudioClip(StereoFourHertz, source);
        source[0] = 1f;
        var mixer = new AudioMixer(StereoFourHertz);
        mixer.Play(audioClip, AudioBus.DrumHit);
        var output = new float[2];
        mixer.Render(output);

        Assert.Equal([0.25f, -0.25f], output);
        Assert.Throws<ArgumentException>(() => new AudioMixer(new SdlAudioFormat(48000, 2))
            .Play(audioClip, AudioBus.DrumHit));
    }

    [Fact]
    public void StreamingVoiceMixesWithoutBlockingAndIsDisposedAtEnd()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var source = new TestStreamSource(StereoFourHertz, [0.25f, -0.25f, 0.5f, -0.5f]);
        mixer.PlayStream(source, AudioBus.Preview);
        var output = new float[6];

        Assert.True(mixer.Render(output));

        Assert.Equal([0.25f, -0.25f, 0.5f, -0.5f, 0f, 0f], output);
        Assert.True(source.Disposed);
        Assert.False(mixer.HasActiveVoices);
    }

    [Fact]
    public void StoppingStreamingVoiceDisposesItsSource()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var source = new TestStreamSource(StereoFourHertz, [1f, 1f], completeAfterRead: false);
        var handle = mixer.PlayStream(source, AudioBus.Preview);

        mixer.Stop(handle);

        Assert.True(source.Disposed);
        Assert.False(mixer.HasActiveVoices);
    }

    [Fact]
    public void StopAllDisposesEveryStreamingSource()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var first = new TestStreamSource(StereoFourHertz, [], completeAfterRead: false);
        var second = new TestStreamSource(StereoFourHertz, [], completeAfterRead: false);
        mixer.PlayStream(first, AudioBus.Bgm);
        mixer.PlayStream(second, AudioBus.Preview);

        mixer.StopAll();

        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.False(mixer.HasActiveVoices);
    }

    [Fact]
    public void PlaybackHandleReportsWhetherItsVoiceIsStillActive()
    {
        var mixer = new AudioMixer(StereoFourHertz);
        var first = mixer.Play(new AudioClip(StereoFourHertz, [1f, 1f, 1f, 1f]), AudioBus.Voice);
        var second = mixer.Play(new AudioClip(StereoFourHertz, [0.5f, 0.5f]), AudioBus.MenuSound);

        Assert.True(mixer.IsPlaying(first));
        Assert.True(mixer.IsPlaying(second));
        mixer.Render(new float[2]);
        Assert.True(mixer.IsPlaying(first));
        Assert.False(mixer.IsPlaying(second));

        mixer.Stop(first);
        Assert.False(mixer.IsPlaying(first));
    }

    private static AudioClip clip(float left, float right)
        => new(StereoFourHertz, [left, right]);

    private sealed class TestStreamSource(
        SdlAudioFormat format,
        float[] samples,
        bool completeAfterRead = true) : IAudioStreamSource
    {
        private int _position;

        public SdlAudioFormat Format { get; } = format;
        public bool IsCompleted => completeAfterRead && _position == samples.Length;
        public Exception? Failure => null;
        public bool Disposed { get; private set; }

        public int Read(Span<float> interleavedDestination)
        {
            var count = Math.Min(interleavedDestination.Length, samples.Length - _position);
            samples.AsSpan(_position, count).CopyTo(interleavedDestination);
            _position += count;
            return count;
        }

        public void Dispose() => Disposed = true;
    }
}
