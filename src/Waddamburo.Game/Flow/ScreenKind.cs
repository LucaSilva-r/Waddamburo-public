namespace Waddamburo.Game.Flow;

/// <summary>Which kind of screen is up: told to movies as <c>screen.name</c> and to a skin's always-on indicators.</summary>
public enum ScreenKind
{
    Boot,
    Attract,
    Title, // the attract's "hit the drum to start" screens (caution, title)
    Demo, // the attract's demo play
    Entry,
    Select,
    Tutorial,
    DanSelect,
    Play,
    Result,
    Retry,
    GameOver,
}
