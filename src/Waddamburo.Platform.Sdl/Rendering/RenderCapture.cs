using System.Collections.Immutable;

namespace Waddamburo.Platform.Sdl.Rendering;

/// <summary>A tightly packed, top-down RGBA8 copy of a presented frame.</summary>
public sealed record RenderCapture(uint Width, uint Height, ImmutableArray<byte> Rgba8);
