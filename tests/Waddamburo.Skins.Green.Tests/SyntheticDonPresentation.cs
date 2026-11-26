using Waddamburo.Lumen.Rendering;
using Waddamburo.Skins.Green.Don;

namespace Waddamburo.Game.Tests;

internal sealed class SyntheticDonPresentation : IDonPresentationController
{
    public List<DonMotionRequest> Requests { get; } = [];

    public List<DonPresentationLayout> ResetLayouts { get; } = [];

    public void Reset(DonPresentationLayout layout) => ResetLayouts.Add(layout);

    public LumenNativeSurfaceKey GetSurface(int playerIndex) => new($"don:{playerIndex}");

    public void SetMotion(DonMotionRequest request) => Requests.Add(request);
}
