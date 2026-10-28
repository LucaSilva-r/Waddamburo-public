namespace Waddamburo.Game.SongSelect;

/// <summary>Categories leave vertically; song rows arrive from above and below at their normal size.</summary>
public sealed class SongFolderTransition
{
    public const int Duration = 54;
    public const float ReturnDuration = Duration * .9f;
    private bool _closing;
    public int Folder { get; private set; } = -1;
    public float Progress { get; private set; }
    public float PreviousProgress { get; private set; }
    public bool Busy => Folder >= 0 && Progress < 1;
    public bool Opening => Folder >= 0 && !_closing && Progress >= PreviousProgress && Progress < 1;
    public float OtherAlpha => Progress < .5f ? 1 : 0;
    public float OtherOffset => 840 * VerticalSongBrowser.Ease(Progress / .5f);
    public float RowsAlpha => Math.Max(Progress, PreviousProgress) >= .3f ? 1 : 0;
    public bool CategoryVisible => Math.Min(Progress, PreviousProgress) < .3f;
    public float At(float interpolation) => PreviousProgress
        + (Progress - PreviousProgress) * Math.Clamp(interpolation, 0, 1);
    public float CategoryWidth(float interpolation) => 1 - VerticalSongBrowser.Ease(At(interpolation) / .3f);
    public float SongWidth(float interpolation) => VerticalSongBrowser.Ease((At(interpolation) - .3f) / .3f);
    public float SongContentOpacity(float interpolation) => _closing || Progress < PreviousProgress
        ? VerticalSongBrowser.Ease((At(interpolation) - .6f) / .3f)
        : VerticalSongBrowser.Ease((At(interpolation) - .55f) / .05f);
    public float RowsOffset => 420 * (1 - VerticalSongBrowser.Ease((Progress - .3f) / .7f));

    /// <summary>The first preview opens only after its frame has become visible.</summary>
    public NativeSongMotion PreviewMotion(NativeSongMotion native) => Opening
        ? new(1, VerticalSongBrowser.Ease((Progress - .6f) / .4f)) : native;

    public void Close() => _closing = Folder >= 0;

    public void Advance(int openedFolder, bool rowsReady)
    {
        PreviousProgress = Progress;
        if (openedFolder < 0) _closing = false;
        var opening = openedFolder >= 0 && !_closing;
        if (opening && Folder != openedFolder) { Folder = openedFolder; Progress = PreviousProgress = 0; }
        if (opening)
        {
            // A confirmed folder reacts immediately. Only the handoff to song
            // geometry waits for native rows, retaining the last category pose.
            var limit = rowsReady ? 1 : Math.Max(Progress, .3f - 1f / Duration);
            Progress = Math.Min(limit, Progress + 1f / Duration);
            if (Progress > 1 - .000001f) Progress = 1;
        }
        else
        {
            Progress = Math.Max(0, Progress - 1f / ReturnDuration);
            if (Progress < .000001f) Progress = 0;
            // Keep both endpoint geometries alive for the last interpolated frame.
            if (Progress == 0 && PreviousProgress == 0) Folder = -1;
        }
    }
}
