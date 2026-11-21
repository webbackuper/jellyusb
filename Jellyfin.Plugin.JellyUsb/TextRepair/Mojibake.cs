using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JellyUsb.TextRepair;

/// <summary>
/// Detection and repair of badly decoded text.
/// <list type="bullet">
/// <item>Double encoding: UTF-8 bytes read as Windows-1252/Latin-1 ("Ã©" instead of "é"). Reversible.</item>
/// <item>Replacement characters: Windows-1252 bytes read as UTF-8 ("�" instead of "é"). The letter is lost;
/// it can only be recovered from another source (re-reading the file, or a matching original title).</item>
/// </list>
/// </summary>
public static class Mojibake
{
    /// <summary>The Unicode replacement character.</summary>
    public const char Replacement = '�';

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Windows-1252 bytes 0x80-0x9F. Undefined bytes (0x81, 0x8D, 0x8F, 0x90, 0x9D) map to the C1 control
    // with the same value, like the Latin-1 decoding most tools fall back to.
    private static readonly char[] HighTable =
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡',
        'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F',
        '\u0090', '‘', '’', '“', '”', '•', '–', '—',
        '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ'
    ];

    private static readonly Dictionary<char, byte> ReverseHigh = BuildReverseHigh();

    /// <summary>Decodes bytes as Windows-1252.</summary>
    public static string DecodeWindows1252(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i] = b is >= 0x80 and <= 0x9F ? HighTable[b - 0x80] : (char)b;
        }

        return new string(chars);
    }

    /// <summary>Returns true when <paramref name="bytes"/> is valid UTF-8.</summary>
    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            StrictUtf8.GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Returns true when the text contains a replacement character.</summary>
    public static bool HasReplacementChar(string? text) => text is not null && text.Contains(Replacement, StringComparison.Ordinal);

    /// <summary>
    /// Repairs double-encoded sequences ("Ã©" → "é", "â€™" → "’"). Characters that are not part of a
    /// valid UTF-8 sequence are kept as they are, so correct text is left untouched.
    /// Returns the input when nothing was repaired.
    /// </summary>
    public static string FixDoubleEncoding(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var current = text;
        // Twice at most, for text that was double-encoded twice ("ÃƒÂ©").
        for (var pass = 0; pass < 2; pass++)
        {
            var next = FixOnePass(current);
            if (ReferenceEquals(next, current) || next == current)
            {
                break;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    /// Fills the replacement characters of <paramref name="broken"/> with the letters found at the same
    /// positions in a candidate (an original title, for example), when the candidate matches the broken
    /// text exactly, ignoring case, everywhere else. Returns null when no candidate matches.
    /// </summary>
    public static string? FillReplacementChars(string broken, IEnumerable<string?> candidates)
    {
        if (!HasReplacementChar(broken))
        {
            return null;
        }

        var normalized = broken.Normalize(NormalizationForm.FormC);
        var pattern = new StringBuilder("^");
        foreach (var c in normalized)
        {
            pattern.Append(c == Replacement ? "([^\\x00-\\x7F])" : Regex.Escape(c.ToString()));
        }

        pattern.Append('$');
        var regex = new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate) || HasReplacementChar(candidate))
            {
                continue;
            }

            var match = regex.Match(candidate.Normalize(NormalizationForm.FormC));
            if (!match.Success)
            {
                continue;
            }

            var result = new StringBuilder(normalized.Length);
            var group = 1;
            foreach (var c in normalized)
            {
                result.Append(c == Replacement ? match.Groups[group++].Value : c);
            }

            return result.ToString();
        }

        return FillByWords(normalized, candidates);
    }

    /// <summary>
    /// Word by word: each word holding replacement characters must match, ignoring case, exactly one
    /// spelling among the candidates' words, and the letters found must be letters. Handles candidates
    /// that differ elsewhere ("Bridget Jones - L'�ge de raison" / "Bridget Jones : L'Âge de raison").
    /// All or nothing: returns null if any broken word stays unresolved.
    /// </summary>
    private static string? FillByWords(string broken, IEnumerable<string?> candidates)
    {
        var wordRegex = new Regex("[\\p{L}\\p{N}'’\\uFFFD]+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        var candidateWords = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate) || HasReplacementChar(candidate))
            {
                continue;
            }

            foreach (Match m in wordRegex.Matches(candidate.Normalize(NormalizationForm.FormC)))
            {
                candidateWords.Add(m.Value);
            }
        }

        if (candidateWords.Count == 0)
        {
            return null;
        }

        var failed = false;
        var result = wordRegex.Replace(broken, m =>
        {
            var word = m.Value;
            if (failed || !word.Contains(Replacement, StringComparison.Ordinal))
            {
                return word;
            }

            var pattern = new StringBuilder("^");
            foreach (var c in word)
            {
                pattern.Append(c == Replacement ? "((?=\\p{L})[^\\x00-\\x7F])" : Regex.Escape(c.ToString()));
            }

            pattern.Append('$');
            var regex = new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

            string? fill = null;
            foreach (var candidateWord in candidateWords)
            {
                var match = regex.Match(candidateWord);
                if (!match.Success)
                {
                    continue;
                }

                var filled = new StringBuilder(word.Length);
                var group = 1;
                foreach (var c in word)
                {
                    filled.Append(c == Replacement ? match.Groups[group++].Value : c);
                }

                var value = filled.ToString();
                if (fill is null)
                {
                    fill = value;
                }
                else if (!string.Equals(fill, value, StringComparison.OrdinalIgnoreCase))
                {
                    fill = null;
                    break; // two different spellings: ambiguous
                }
            }

            if (fill is null)
            {
                failed = true;
                return word;
            }

            return fill;
        });

        return failed || HasReplacementChar(result) ? null : result;
    }

    private static string FixOnePass(string text)
    {
        StringBuilder? output = null;
        var i = 0;
        while (i < text.Length)
        {
            if (!TryGetByte(text[i], out var lead) || lead < 0xC2 || lead > 0xF4)
            {
                output?.Append(text[i]);
                i++;
                continue;
            }

            var length = lead switch
            {
                < 0xE0 => 2,
                < 0xF0 => 3,
                _ => 4
            };

            if (i + length <= text.Length && TryDecodeSequence(text, i, length, out var decoded))
            {
                output ??= new StringBuilder(text, 0, i, text.Length);
                output.Append(decoded);
                i += length;
                continue;
            }

            output?.Append(text[i]);
            i++;
        }

        return output?.ToString() ?? text;
    }

    private static bool TryDecodeSequence(string text, int start, int length, out string decoded)
    {
        decoded = string.Empty;
        Span<byte> bytes = stackalloc byte[4];
        for (var k = 0; k < length; k++)
        {
            if (!TryGetByte(text[start + k], out var b))
            {
                return false;
            }

            if (k > 0 && (b < 0x80 || b > 0xBF))
            {
                return false;
            }

            bytes[k] = b;
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes[..length]);
            return decoded.Length > 0;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Maps a character back to the Windows-1252 byte it was decoded from.</summary>
    private static bool TryGetByte(char c, out byte value)
    {
        if (c <= 'ÿ')
        {
            value = (byte)c;
            return true;
        }

        return ReverseHigh.TryGetValue(c, out value);
    }

    private static Dictionary<char, byte> BuildReverseHigh()
    {
        var map = new Dictionary<char, byte>();
        for (var i = 0; i < HighTable.Length; i++)
        {
            if (HighTable[i] > 'ÿ')
            {
                map[HighTable[i]] = (byte)(0x80 + i);
            }
        }

        return map;
    }
}
