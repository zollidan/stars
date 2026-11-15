using System.Globalization;
using System.Text;

namespace Stars.Application.Normalization;

/// <summary>
/// Deterministic provider-independent team-name normalisation.
///
/// The same normal form is used for storage and search. It keeps age, gender and
/// reserve markers because they change identity, and removes only generic club noise.
/// The alias table is intentionally small and explicit: it converges common
/// transliteration variants (Moscow/Moskva, Dynamo/Dinamo) without guessing.
/// </summary>
public static class NameNormalizer
{
    private static readonly Dictionary<char, string> Transliteration = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d",
        ['е'] = "e", ['ё'] = "e", ['ж'] = "zh", ['з'] = "z", ['и'] = "i",
        ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n",
        ['о'] = "o", ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t",
        ['у'] = "u", ['ф'] = "f", ['х'] = "kh", ['ц'] = "ts", ['ч'] = "ch",
        ['ш'] = "sh", ['щ'] = "shch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "",
        ['э'] = "e", ['ю'] = "yu", ['я'] = "ya", ['й'] = "y",
        // Ukrainian and Belarusian letters commonly found in provider names.
        ['і'] = "i", ['ї'] = "yi", ['є'] = "ye", ['ґ'] = "g", ['ў'] = "u",
        // Latin letters that do not decompose into ASCII under FormD.
        ['ı'] = "i", ['ş'] = "s", ['ğ'] = "g", ['ø'] = "o", ['æ'] = "ae",
        ['ł'] = "l", ['đ'] = "d", ['ß'] = "ss", ['œ'] = "oe", ['å'] = "a",
        ['ä'] = "a", ['ë'] = "e", ['ï'] = "i", ['ô'] = "o", ['û'] = "u",
        ['ÿ'] = "y", ['þ'] = "th", ['ð'] = "d", ['ħ'] = "h", ['ŧ'] = "t",
    };

    private static readonly Dictionary<string, string> TokenAliases = new(StringComparer.Ordinal)
    {
        ["moscow"] = "moskva",
        ["moskva"] = "moskva",
        ["dynamo"] = "dinamo",
        ["dinamo"] = "dinamo",
        ["kyiv"] = "kyiv",
        ["kiiv"] = "kyiv",
        ["kiev"] = "kyiv",
        ["tsska"] = "cska",
        ["cska"] = "cska",
        ["petersburg"] = "peterburg",
        ["sankt"] = "st",
        ["grei"] = "grey",
    };

    private static readonly HashSet<string> MarkerWords = new(StringComparer.Ordinal)
    {
        "w", "women", "womens", "ladies", "zh", "zhen",
        "res", "reserve", "reserves",
        "ii", "iii", "2", "3",
    };

    private static readonly HashSet<string> NoiseWords = new(StringComparer.Ordinal)
    {
        "fc", "fk", "cf", "sc", "ac", "afc", "cd", "bk", "club",
    };

    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        // These letters decompose into a base letter plus a combining mark under FormD;
        // map them before decomposition so the intended transliteration survives.
        var prepared = name.ToLowerInvariant()
            .Replace("ё", "е")
            .Replace("й", "y")
            .Replace("ї", "yi")
            .Replace("є", "ye")
            .Replace("ў", "u")
            .Replace("İ", "i");
        var decomposed = prepared.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length + 8);
        foreach (var character in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (Transliteration.TryGetValue(character, out var replacement))
            {
                builder.Append(replacement);
            }
            else if (char.IsAsciiLetterOrDigit(character))
            {
                builder.Append(character);
            }
            else
            {
                builder.Append(' ');
            }
        }

        var tokens = builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var normalized = new List<string>(tokens.Length);
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];

            if (token is "u" or "under" or "do" && index + 1 < tokens.Length
                && tokens[index + 1].Length is 1 or 2
                && tokens[index + 1].All(char.IsAsciiDigit))
            {
                normalized.Add($"u{tokens[index + 1]}");
                index++;
                continue;
            }

            token = token switch
            {
                "women" or "womens" or "ladies" or "zh" or "zhen" => "w",
                "reserve" or "reserves" => "res",
                _ => token,
            };

            if (TokenAliases.TryGetValue(token, out var alias))
            {
                token = alias;
            }

            if (NoiseWords.Contains(token))
            {
                continue;
            }

            normalized.Add(token);
        }

        // A name made only of noise ("FC") is still a token. Losing it would make
        // unrelated clubs look identical after normalisation.
        var result = normalized.Count > 0 ? string.Join(' ', normalized) : string.Join(' ', tokens);
        return result;
    }

    /// <summary>
    /// Squad markers that must be equal before two teams may be linked automatically.
    /// </summary>
    public static string Markers(string? normalizedName)
    {
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            return string.Empty;
        }

        var markers = normalizedName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => MarkerWords.Contains(token)
                || (token.Length > 1 && token[0] == 'u' && token[1..].All(char.IsAsciiDigit)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        return string.Join(' ', markers);
    }

    public static int TokenCount(string? normalizedName) =>
        string.IsNullOrWhiteSpace(normalizedName)
            ? 0
            : normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
}
