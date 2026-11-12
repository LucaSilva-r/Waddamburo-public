using Waddamburo.App.Home;
using Waddamburo.App.Presentation;
using Waddamburo.Platform.Sdl;
using Waddamburo.Platform.Sdl.Rendering;

namespace Waddamburo.App.Flow;

/// <summary>The first custom-library entry's upload choice, using the settings menu's modal presentation.</summary>
internal sealed class CustomUploadPrompt(GameShell shell, string fontPath) : IInputOverlay, IDisposable
{
    private readonly OverlayMenu _menu = new(shell.Application, fontPath);

    public bool IsOpen => _menu.IsOpen;

    public void Open(Action enterLibrary)
    {
        _menu.Sound = don => shell.Sounds?.Bank.Play("SE_COM", don ? 0 : 3,
            Waddamburo.Platform.Sdl.Media.AudioBus.DrumHit, trace: false);
        _menu.Open(new OverlayPage(() => Strings.T("custom_upload.title"), () =>
            [new(Strings.T("menu.yes"), Enter: () => answer(true)), new(Strings.T("menu.no"), Enter: () => answer(false))],
            Centred: true, Modal: true, Back: static () => { }, Body: () =>
            [(Strings.T("custom_upload.message"), OverlayMenu.White), (Strings.T("custom_upload.marker"), OverlayMenu.White),
                (Strings.T("custom_upload.question"), OverlayMenu.Yellow)]), home: true);

        void answer(bool disable)
        {
            shell.SaveArcade(shell.Arcade with { UploadCustomSongs = !disable, CustomUploadNoticeSeen = true });
            _menu.Open(new OverlayPage(() => Strings.T("custom_upload.title"),
                () => [new(Strings.T("service.ok"), Enter: finish)], Centred: true, Modal: true, Back: finish,
                Body: () => [(Strings.T(disable ? "custom_upload.disabled" : "custom_upload.enabled"), OverlayMenu.White),
                    (Strings.T("custom_upload.reminder"), OverlayMenu.White)]), home: true);
        }

        void finish()
        {
            _menu.Close();
            enterLibrary();
        }
    }

    public bool Tick(SdlKeyboardSnapshot keys, bool escape)
    {
        if (!IsOpen) return false;
        _menu.Tick(keys, keys, escape);
        return true;
    }

    public IEnumerable<RenderQuad> Quads() => shell.Unsqueezed(_menu.Quads(), centred: true);

    public void Dispose() => _menu.Dispose();
}
