using Waddamburo.Catalog;

namespace Waddamburo.Editor;

public sealed record EffectEditorSource(PlayableChart Chart, IEffectsEditorTransport Transport, EffectEditorFiles Files,
    string? TjaPath = null, IReadOnlyList<TaikoCourse>? Courses = null);
