using System.Numerics;
using ImGuiNET;
using Waddamburo.Platform.Sdl;

namespace Waddamburo.Editor;

/// <summary>Owns the effects editor UI around an existing practice session.</summary>
public sealed class EffectsEditorSession : IDisposable
{
    private readonly SdlApplication _application;
    private readonly ImGuiLayer _layer;
    private readonly (float Size, float X, float Y) _previousLetterbox;
    private bool _showDemo = true;
    private bool _disposed;

    public EffectsEditorSession(SdlApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        _application = application;
        _previousLetterbox = application.Letterbox;
        _layer = application.AttachImGui(Draw);
        application.Letterbox = (.65f, .5f, .25f);
    }

    public bool CloseRequested { get; private set; }

    private void Draw()
    {
        if (ImGui.IsKeyPressed(ImGuiKey.Escape)) CloseRequested = true;
        var size = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(Vector2.Zero);
        ImGui.SetNextWindowSize(new(size.X, 60));
        if (ImGui.Begin("Effects editor", ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse))
        {
            if (ImGui.Button("Back to practice (Esc)")) CloseRequested = true;
            ImGui.SameLine();
            ImGui.Checkbox("ImGui demo", ref _showDemo);
        }
        ImGui.End();
        if (_showDemo)
        {
            ImGui.SetNextWindowPos(new(20, Math.Max(65, size.Y * .55f)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowSize(new(Math.Min(600, size.X - 40), Math.Max(100, size.Y * .4f)), ImGuiCond.FirstUseEver);
            ImGui.ShowDemoWindow(ref _showDemo);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _layer.Dispose();
        _application.Letterbox = _previousLetterbox;
        _disposed = true;
    }
}
