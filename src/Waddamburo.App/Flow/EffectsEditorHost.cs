using Waddamburo.Catalog;
using Waddamburo.Editor;
using Waddamburo.Game.Flow;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.App.Flow;

/// <summary>
/// The effects editor (F8 in a song) and the layout editor (pause › Layouts) over the song on screen: opening them,
/// their frame (Escape and leaving the song close them), and what the play reads from them.
/// </summary>
internal sealed class EffectsEditorHost(GameShell shell, GameplayFlow gameplay, Func<bool> closeHomeMenu) : IDisposable
{
    private EffectsEditorSession? _session;

    public bool IsOpen => _session is not null;

    /// <summary>The layout editor shows the parts that only appear now and then (combo announcement, resume timer).</summary>
    public bool ShowHiddenElements => _session?.ShowHidden == true;

    /// <summary>The editor's toggle: the scene's movies don't advance while it's open.</summary>
    public bool FreezeAnimations { get; set; }

    /// <summary>The effects editor on the playing chart, or (<paramref name="layout"/>) on the player's gameplay layout.</summary>
    public void Open(bool layout = false)
    {
        // The editors draw with ImGui, which has no phone build (choosing a layout still works there).
        if (_session is not null || PlatformInfo.IsMobile || !(layout ? gameplay.CanEditLayout : gameplay.CanEditEffects)
            || !closeHomeMenu())
            return;
        gameplay.BeginEffectsEditing(autoplay: layout);
        _session = layout
            ? new EffectsEditorSession(shell.Application, gameplay.LayoutEditorChart, gameplay.Training!, gameplay.LayoutFiles(),
                gameplay.ReplaceEditorEffects, gameplay.LayoutElements, layout: true, layoutName: gameplay.ActiveLayout, renameLayout: renameLayout,
                twoPlayers: gameplay.TwoPlayerLayouts)
            : new EffectsEditorSession(shell.Application, stockLayout(), gameplay.Training!, gameplay.EditorFiles(),
                gameplay.ReplaceEditorEffects, gameplay.EditorElements, gameplay.LoadEditorTja, gameplay.EditorTjaPath());
    }

    /// <summary>Each display frame: Escape asks the editor to close; a closed editor or a song left closes it. Returns the keys the game still gets.</summary>
    public SdlKeyboardSnapshot Frame(SdlKeyboardSnapshot keyboard)
    {
        if (_session is not { } editor)
            return keyboard;
        if (keyboard.Presses.Any(static press => press.Key == SdlKeyboardKey.Escape))
        {
            editor.RequestClose();
            keyboard = SdlKeyboardSnapshot.Empty;
        }
        if (editor.CloseRequested || !gameplay.CanEditLayout)
        {
            Close();
            keyboard = SdlKeyboardSnapshot.Empty;
        }
        return keyboard;
    }

    public void Close()
    {
        _session?.Dispose();
        _session = null;
        FreezeAnimations = false;
    }

    // The modchart editor works on the stock screen: the song drops the player's layout (and its 4:3) as it opens.
    private PlayableChart stockLayout()
    {
        var chart = gameplay.EditorChartWithoutLayout;
        gameplay.ReplaceEditorEffects(chart.Effects);
        return chart;
    }

    // The layout editor's name field: the active layout's file renamed, the settings following it.
    private EffectEditorFiles? renameLayout(string name)
    {
        if (gameplay.ActiveLayout is not { } from) return null;
        try
        {
            if (Waddamburo.App.Gameplay.PlayerLayouts.Rename(gameplay.LayoutFolder, from, name) is not { } renamed) return null;
            shell.Arcade = gameplay.WithActiveLayout(shell.Arcade, renamed);
            if (shell.Options.ArcadePath is { } config) ArcadeSettings.SaveMenuSettings(config, shell.Arcade);
            return gameplay.LayoutFiles();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Layout not renamed: {exception.Message}");
            return null;
        }
    }

    public void Dispose() => Close();
}
