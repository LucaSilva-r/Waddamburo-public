using Waddamburo.Lumen.Rendering;

namespace Waddamburo.Game.SongSelect;

/// <summary>Finite category layout with scrolling sampled from the native timeline.</summary>
public sealed class VerticalSongBrowser
{
    // Focus at 40% of the 720px logical stage: 10% above its former centre.
    private const float FocusCenter = 288;
    // The gold template's native margins make a logical246px song aperture
    // 234px high on screen. Category bodies use screen coordinates directly.
    public const float PreviewBodyHeight = VerticalCategoryFrame.PreviewHeight;
    public const float CategoryFocusTop = FocusCenter - PreviewBodyHeight / 2;
    public const int TransitionTicks = 18;
    public const int PreviewDelayTicks = 24;
    public const float CompactRowHeight = 58.35f;
    public const float SongRowPitch = CompactRowHeight + (65 * 1.15f - CompactRowHeight) * .9f;
    public const float CategoryPitch = CompactRowHeight + (72 - CompactRowHeight) * .9f;
    private readonly float _songRowPitch, _categoryPitch;
    private readonly bool _overlayCategoryHeader;

    public VerticalSongBrowser(float gapScale = 1, bool overlayCategoryHeader = false, float? categoryGapScale = null)
    {
        gapScale = Math.Clamp(gapScale, 0, 1);
        _songRowPitch = CompactRowHeight + (SongRowPitch - CompactRowHeight) * gapScale;
        _categoryPitch = CompactRowHeight + (CategoryPitch - CompactRowHeight) * Math.Clamp(categoryGapScale ?? gapScale, -.5f, 1);
        _overlayCategoryHeader = overlayCategoryHeader;
    }

    private int _hoverGenre = -1, _hoverBoard = -2, _hoverAge;
    private float[] _tops = [], _heights = [], _reveal = [], _preview = [], _body = [];
    private float[][] _from = [], _to = [];
    private bool[] _opened = [];
    private int[] _counts = [];
    private int _genre = -1, _board = -2, _age;
    private int _songUnwrapped;
    private bool _categoryScroll, _songScroll;
    private float _focus, _fromCamera, _toCamera, _songCamera;
    private float _selectionTop, _selectionHeight, _fromSelectionTop, _toSelectionTop, _fromSelectionHeight, _toSelectionHeight;
    private readonly Dictionary<(int Genre, int Board), float> _expanded = [];
    private Dictionary<(int Genre, int Board), float> _fromExpanded = [], _toExpanded = [];
    public float Camera { get; private set; }
    public float Length { get; private set; }
    public bool SongScroll => _songScroll;
    public int SelectedGenre => _genre;
    public int SelectedBoard => _board;
    public int SelectedUnwrappedBoard => _songUnwrapped;
    public float Reveal(int genre) => _reveal[genre];
    public float Preview(int genre) => _preview[genre];
    public float Height(int genre) => _heights[genre];
    public float BodyHeight(int genre) => _body[genre];
    public float Top(int genre) => _tops[genre];
    public float Row(int genre, int board) => Top(genre) + 28 + board * _songRowPitch;
    public float RowHeight(int genre, int board) => _songRowPitch + _expanded.GetValueOrDefault((genre, board));
    public float SelectionTop => _board >= 0 ? UnwrappedBoardTop(_genre, _songUnwrapped) : _selectionTop;
    public float SelectionHeight => _selectionHeight;

    public int CategoryZ(int genre, int copy = 0)
    {
        var offset = genre - _genre;
        if (offset > _counts.Length / 2f) offset -= _counts.Length;
        if (offset < -_counts.Length / 2f) offset += _counts.Length;
        return -Math.Abs(offset + copy * _counts.Length);
    }

    // Only the nearest occurrence of a row owns its expansion in the circular
    // list. Copies above/below it stay compact, including one-song folders.
    public float VisibleRowExpansion(int offset)
    {
        if (_genre < 0 || _board < 0) return 0;
        return _expanded.GetValueOrDefault((_genre, _songUnwrapped + offset));
    }

    public float VisibleRowTop(int offset)
    {
        var top = SelectionTop + offset * _songRowPitch;
        if (offset > 0)
            for (var row = 0; row < offset; row++) top += VisibleRowExpansion(row);
        else
            for (var row = offset; row < 0; row++) top -= VisibleRowExpansion(row);
        return top;
    }

    public IEnumerable<int> VisibleSongOffsets(float top = -240, float bottom = 960)
    {
        // Follow the viewport rather than the new cursor: a page skip can leave
        // its destination more than eight rows away while the camera travels.
        var first = (int)MathF.Floor((Camera + top - SelectionTop) / _songRowPitch) - 4;
        var last = (int)MathF.Ceiling((Camera + bottom - SelectionTop) / _songRowPitch) + 4;
        for (var offset = first; offset <= last; offset++)
        {
            var y = VisibleRowTop(offset) - Camera;
            if (y + CompactRowHeight + VisibleRowExpansion(offset) >= top && y <= bottom) yield return offset;
        }
    }
    public IEnumerable<(int Offset, float Expansion)> ExpandedSongRows => _expanded
        .Where(row => row.Key.Genre == _genre)
        .Select(row => (row.Key.Board - _songUnwrapped, row.Value));

    public void Advance(IReadOnlyList<bool> opened, IReadOnlyList<int> counts,
        int genre, int board, float selectedHeight, IReadOnlyList<float>? categoryHeights = null, int direction = 0, NativeSongMotion? motion = null)
    {
        if (opened.Count == 0) return;
        genre = Math.Clamp(genre, 0, opened.Count - 1);
        var initialize = _tops.Length != opened.Count;
        if (_hoverGenre != genre || _hoverBoard != board)
        {
            _hoverGenre = genre; _hoverBoard = board; _hoverAge = 0;
        }
        else _hoverAge = Math.Min(PreviewDelayTicks, _hoverAge + 1);
        if (!initialize && _board < 0 && board >= 0 && opened[genre]) _hoverAge = PreviewDelayTicks;
        var nativeSong = board >= 0 && motion is not null;
        var songScroll = nativeSong && !initialize && _board >= 0 && genre == _genre
            && (board != _board || _songScroll);
        var expanded = songScroll ? 181f : !nativeSong && board >= 0 && selectedHeight > 65 && _hoverAge >= PreviewDelayTicks ? 181f : 0;
        var changed = initialize || genre != _genre || board != _board
            || !_opened.SequenceEqual(opened) || !_counts.SequenceEqual(counts)
            || _toExpanded.GetValueOrDefault((genre, _songUnwrapped)) != expanded;
        if (changed)
        {
            // Both lists close the previous card and open the next on one native slide.
            // Folder opening/closing retains its independent transition clock.
            _categoryScroll = !initialize && genre != _genre && board < 0 && _board < 0
                && _opened.SequenceEqual(opened);
            _songScroll = songScroll;
            var oldFocus = _board >= 0 ? _focus : Camera + CategoryFocusTop;
            var newSelection = genre != _genre || board != _board;
            var loopSongs = !initialize && board >= 0 && _board >= 0 && genre == _genre;
            if (!loopSongs || counts[genre] != _counts[genre])
            {
                _songUnwrapped = board;
            }
            else if (board != _board)
            {
                var count = Math.Max(1, counts[genre]);
                var forward = (board - _board + count) % count;
                var backward = forward - count;
                var step = direction > 0 ? forward : direction < 0 ? backward
                    : forward <= -backward ? forward : backward;
                _songUnwrapped += step;
            }
            if (initialize)
            {
                _tops = new float[opened.Count]; _heights = new float[opened.Count];
                _reveal = new float[opened.Count]; _preview = new float[opened.Count]; _body = new float[opened.Count];
            }
            _genre = genre; _board = board;
            _opened = [.. opened]; _counts = [.. counts];
            _from = [[.. _tops], [.. _heights], [.. _reveal], [.. _preview], [.. _body]];
            _to = Enumerable.Range(0, 5).Select(_ => new float[opened.Count]).ToArray();
            _fromExpanded = new(_expanded); _toExpanded = [];
            if (expanded > 0) _toExpanded[(genre, _songUnwrapped)] = expanded;
            Length = 0;
            for (var i = 0; i < opened.Count; i++)
            {
                var preview = !opened[i] && i == genre && board < 0;
                // In the depth wheel the header floats above its body. Reserve
                // no extra row for it: upper and lower neighbours share one pitch.
                var overlayHeader = preview && _overlayCategoryHeader;
                var header = !overlayHeader && (opened[i] || preview) ? 46 : 0;
                var body = opened[i] ? 28 + Math.Max(1, counts[i]) * _songRowPitch + (i == genre ? expanded : 0)
                    : preview ? PreviewBodyHeight : CompactRowHeight;
                _to[0][i] = Length + header;
                _to[1][i] = overlayHeader ? body + _categoryPitch - CompactRowHeight
                    : opened[i] || preview ? body + 58 - (72 - _categoryPitch) : _categoryPitch;
                _to[2][i] = opened[i] ? 1 : 0;
                _to[3][i] = preview ? 1 : 0;
                _to[4][i] = body;
                Length += _to[1][i];
            }
            _focus = _to[0][genre] + (board >= 0 ? 28 + _songUnwrapped * _songRowPitch : 0);
            var cycle = initialize ? 0 : loopSongs
                ? MathF.Round((_tops[genre] - _to[0][genre]) / Length) * Length
                : MathF.Round((oldFocus - _focus) / Length) * Length;
            _focus += cycle;
            for (var i = 0; i < opened.Count; i++)
            {
                var offset = i - genre;
                _to[0][i] += cycle + (offset > opened.Count / 2f ? -Length : offset < -opened.Count / 2f ? Length : 0);
                // Moving a copy by a whole period is visually identical. Rebase
                // before tweening so an interrupted wrap never crosses the list.
                if (!initialize)
                    _from[0][i] += MathF.Round((_to[0][i] - _from[0][i]) / Length) * Length;
            }
            _fromCamera = nativeSong && loopSongs && !_songScroll ? _songCamera : Camera;
            // The native selection opens symmetrically. Its centre remains at
            // rest while the two surrounding groups make room for the aperture.
            var anchor = FocusCenter - 25.35f - expanded / 2;
            _toCamera = _focus - (board >= 0 ? anchor : CategoryFocusTop);
            _fromSelectionTop = _selectionTop; _toSelectionTop = _focus;
            _fromSelectionHeight = newSelection && board >= 0 ? 65 : _selectionHeight;
            _toSelectionHeight = board >= 0 ? 65 + expanded : 0;
            _age = 0;
            if (initialize)
            {
                _from = _to.Select(values => values.ToArray()).ToArray();
                _fromCamera = _toCamera;
                _fromSelectionTop = _toSelectionTop; _fromSelectionHeight = _toSelectionHeight;
                _fromExpanded = new(_toExpanded);
                _age = TransitionTicks;
            }
        }
        _age = Math.Min(TransitionTicks, _age + 1);
        var t = (_categoryScroll || _songScroll) && motion is { } listMotion
            ? Math.Clamp(listMotion.Slide, 0, 1)
            : Ease(_age / (float)TransitionTicks);
        float[][] current = [_tops, _heights, _reveal, _preview, _body];
        for (var part = 0; part < current.Length; part++)
            for (var i = 0; i < opened.Count; i++) current[part][i] = mix(_from[part][i], _to[part][i], t);
        Camera = mix(_fromCamera, _toCamera, t);
        _selectionTop = mix(_fromSelectionTop, _toSelectionTop, t);
        _selectionHeight = mix(_fromSelectionHeight, _toSelectionHeight, t);
        _expanded.Clear();
        foreach (var key in _fromExpanded.Keys.Concat(_toExpanded.Keys).Distinct())
        {
            var height = mix(_fromExpanded.GetValueOrDefault(key), _toExpanded.GetValueOrDefault(key), t);
            if (height > 0) _expanded[key] = height;
        }
        if (_songScroll)
        {
            // Reuse the category interpolation for camera and both card heights.
            // In particular, retain the outgoing aperture at slide zero and on
            // reversals; the native selection's later open must not run a second tween.
            _selectionHeight = 65 + _expanded.GetValueOrDefault((_genre, _songUnwrapped));
        }
        else if (nativeSong && motion is { } native)
        {
            // Read the movie's slide and opening separately. Restarting a host
            // tween on each native frame would delay both and move the list twice.
            _songCamera = mix(_fromCamera, _toCamera, native.Slide);
            var extra = 181 * Math.Clamp(native.Expansion, 0, 1);
            _selectionHeight = 65 + extra;
            _expanded.Clear();
            if (extra > 0) _expanded[(_genre, _songUnwrapped)] = extra;
            // Opening a circular folder can rebase its period while the category
            // tops still tween. Follow that same rebase instead of aiming the
            // camera at a future row and letting the song fly back into view.
            var rebase = UnwrappedBoardTop(_genre, _songUnwrapped) - _focus;
            Camera = _songCamera + extra / 2 + rebase;
        }

    }

    public float SongPeriod(int genre) => Math.Max(1, _counts[genre]) * _songRowPitch
        + _expanded.Where(pair => pair.Key.Genre == genre).Sum(pair => pair.Value);

    public float UnwrappedBoardTop(int genre, int board)
    {
        if (board < 0 && !_opened[genre]) return Top(genre);
        return Row(genre, board)
            + _expanded.Where(pair => pair.Key.Genre == genre && pair.Key.Board < board).Sum(pair => pair.Value);
    }

    public float BoardTop(int genre, int board)
    {
        if (board < 0) return Top(genre);
        if (_board < 0 || genre != _genre || !_opened[genre]) return UnwrappedBoardTop(genre, board);
        var count = Math.Max(1, _counts[genre]);
        var offset = board - _board;
        if (offset > count / 2f) offset -= count;
        if (offset < -count / 2f) offset += count;
        return VisibleRowTop(offset);
    }

    public static float Ease(float progress)
    {
        var t = Math.Clamp(progress, 0, 1);
        return t * t * (3 - 2 * t);
    }
    private static float mix(float from, float to, float t) => from + (to - from) * t;
}

/// <summary>Independent phases sampled from the running Lumen list selector.</summary>
public readonly record struct NativeSongMotion(float Slide, float Expansion);
