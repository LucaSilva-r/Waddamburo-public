using Waddamburo.Game.Don;
using Waddamburo.Lumen.Rendering;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Presentation;

/// <summary>The Lumen hosts' view of the 3D Don renderer: player slots, cameras, motions, costumes.</summary>
internal sealed class DonPresentationController(SdlDonRenderer renderer) : IDonPresentationController
{
    private readonly SdlDonRenderer _renderer = renderer;
    private readonly LumenNativeSurfaceKey[] _surfaces =
    [
        new("don:0"),
        new("don:1"),
        new("don:2"),
    ];

    /// <summary>
    /// The player's drum. The right drum's player alone is drawn as Katsu-chan (the renderer's
    /// second slot) and never with the mirrored second-player camera.
    /// </summary>
    public int GameplaySide { get; set; }

    /// <summary>
    /// Scenes that draw the one player in their first Don slot (gameplay, results, revival): with a
    /// right-drum player, slot 0 is Katsu-chan's. Entry and Song Select address him as player 1.
    /// </summary>
    public bool MapPlayerZero { get; set; }

    private int slot(int playerIndex) => MapPlayerZero && playerIndex == 0 ? 1 : playerIndex;

    public void Reset(DonPresentationLayout layout)
    {
        // A right-drum player's Katsu-chan is never mirrored: he faces the centre as in entry
        // (the mirrored camera only suits a second player standing beside a first).
        _renderer.Reset(mirrorPlayerTwoCamera: layout != DonPresentationLayout.OpposedPlayers && GameplaySide != 1,
            layout: cameraLayout(layout));
    }

    public void SetCameraLayout(DonPresentationLayout layout) => _renderer.SetCameraLayout(cameraLayout(layout));

    // Gameplay overlays: the shared kusudama shows both Dons facing the centre, so Katsu-chan takes
    // the reflected camera; each lane's balloon shows its Don unreflected (user-confirmed).
    public void SetCameraLayout(int playerIndex, DonPresentationLayout layout)
    {
        _renderer.MirrorPlayerTwoCamera = layout == DonPresentationLayout.Kusudama && GameplaySide != 1;
        _renderer.SetCameraLayout(slot(playerIndex), cameraLayout(layout));
    }

    private static DonCameraLayout cameraLayout(DonPresentationLayout layout) => layout switch
    {
        DonPresentationLayout.Gameplay => DonCameraLayout.Gameplay,
        DonPresentationLayout.Retry => DonCameraLayout.Retry,
        DonPresentationLayout.RetrySuccess => DonCameraLayout.RetrySuccess,
        _ => DonCameraLayout.Standard,
    };

    public LumenNativeSurfaceKey GetSurface(int playerIndex) => _surfaces[slot(playerIndex)];

    public void SetMotion(DonMotionRequest request) =>
        _renderer.SetMotion(slot(request.PlayerIndex), request.OneShot, request.Loop);

    public void SetIdle(int playerIndex, string loop) => _renderer.SetIdle(slot(playerIndex), loop);

    public void SetCostume(int playerIndex, DonCostume costume)
    {
        try
        {
            _renderer.SetCostume(playerIndex, costume.Whole, costume.Head, costume.Body, costume.Paint);
        }
        catch (FileNotFoundException exception)
        {
            // A costume the installed data lacks keeps the current look.
            Console.Error.WriteLine($"Don costume {costume}: {exception.Message}");
        }
    }

    public void SetLook(int playerIndex, DonLook? look)
    {
        SetCostume(playerIndex, look?.ToCostume() ?? DonCostume.Default);
        _renderer.SetColors(playerIndex, look?.Colors);
    }

    public void SetDialogDon(bool visible)
    {
        _renderer.DialogVisible = visible;
        if (visible)
            _renderer.SetMotion(2, null, "don_entry_loop"); // traced p2 don_entry_loop
    }

    public RenderTextureId? Resolve(LumenNativeSurfaceKey surface)
    {
        for (var index = 0; index < _surfaces.Length; index++)
            if (surface == _surfaces[index])
                return _renderer.GetTexture(index);
        return null;
    }
}
