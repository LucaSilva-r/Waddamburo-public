using Waddamburo.Game.SongSelect;

namespace Waddamburo.App.Hosting;

internal sealed class TracePreviewController : ISongPreviewController
{
    public static TracePreviewController Instance { get; } = new();

    public void SetPreview(SongPreviewRequest? request)
    {
        if (request is not null)
            Console.WriteLine($"Song preview requested at {request.Start.TotalSeconds:0.###}s for {request.Song}.");
    }

    public void Silence() { }

    public void Hold() { }
}
