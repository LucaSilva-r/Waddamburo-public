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

    private static AudioClip clip(float left, float right)
        => new(StereoFourHertz, [left, right]);
}
