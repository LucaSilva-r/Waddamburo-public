using System.Text;

namespace Waddamburo.Game.SongSelect;

/// <summary>
/// Kana to romaji for searching without a Japanese keyboard. Both sides end up in one spelling (kunrei-like:
/// si, ti, tu, hu, zi, sya), so "shi" and "si" both find し. Kanji are left as they are (no reading data).
/// </summary>
public static class Romaji
{
    private static readonly string[] Syllables =
    [
        // ぁ-ゖ, in code point order (U+3041..U+3096); small vowels/ya/yu/yo/wa are handled after.
        "a", "a", "i", "i", "u", "u", "e", "e", "o", "o",
        "ka", "ga", "ki", "gi", "ku", "gu", "ke", "ge", "ko", "go",
        "sa", "za", "si", "zi", "su", "zu", "se", "ze", "so", "zo",
        "ta", "da", "ti", "zi", "", "tu", "zu", "te", "de", "to", "do",
        "na", "ni", "nu", "ne", "no",
        "ha", "ba", "pa", "hi", "bi", "pi", "hu", "bu", "pu", "he", "be", "pe", "ho", "bo", "po",
        "ma", "mi", "mu", "me", "mo",
        "ya", "ya", "yu", "yu", "yo", "yo",
        "ra", "ri", "ru", "re", "ro",
        "wa", "wa", "i", "e", "o", "n", "vu", "ka", "ke",
    ];

    /// <summary>The text with its kana in romaji (lower case, full-width letters and digits folded).</summary>
    public static string FromKana(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var builder = new StringBuilder(text.Length * 2);
        var doubleNext = false;
        foreach (var raw in text.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            // Katakana to hiragana (ァ-ヶ → ぁ-ゖ).
            var c = raw is >= 'ァ' and <= 'ヶ' ? (char)(raw - 0x60) : raw;
            if (c is < 'ぁ' or > 'ゖ')
            {
                if (c != 'ー') builder.Append(c); // ponytail: long vowels dropped, as people rarely type them
                continue;
            }
            if (c == 'っ') { doubleNext = true; continue; }
            var syllable = Syllables[c - 'ぁ'];
            var small = c is 'ぁ' or 'ぃ' or 'ぅ' or 'ぇ' or 'ぉ' or 'ゃ' or 'ゅ' or 'ょ' or 'ゎ';
            if (small && builder.Length > 0 && builder[^1] is 'a' or 'i' or 'u' or 'e' or 'o')
            {
                // The small kana replaces the vowel before it: きゃ → kya, しゃ → sya, ふぁ → ha (typed fa), てぃ → ti.
                builder.Length--;
                if (c is 'ゃ' or 'ゅ' or 'ょ') builder.Append('y');
                builder.Append(syllable[^1]);
                continue;
            }
            if (doubleNext && syllable.Length > 1) builder.Append(syllable[0]);
            doubleNext = false;
            builder.Append(syllable);
        }
        return builder.ToString();
    }

    /// <summary>Romaji as people type it, in <see cref="FromKana"/>'s spelling.</summary>
    public static string Fold(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var text = query.Normalize(NormalizationForm.FormKC).ToLowerInvariant()
            .Replace('ā', 'a').Replace('ī', 'i').Replace('ū', 'u').Replace('ē', 'e').Replace('ō', 'o');
        foreach (var (from, to) in Spellings)
            text = text.Replace(from, to, StringComparison.Ordinal);
        return text;
    }

    // Order matters: the longer spellings first.
    private static readonly (string From, string To)[] Spellings =
    [
        ("tch", "tty"), ("shi", "si"), ("sh", "sy"), ("chi", "ti"), ("ch", "ty"), ("tsu", "tu"),
        ("ji", "zi"), ("j", "zy"), ("fu", "hu"), ("f", "h"), ("wo", "o"), ("-", ""),
    ];
}
