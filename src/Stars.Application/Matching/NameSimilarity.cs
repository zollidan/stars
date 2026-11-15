namespace Stars.Application.Matching;

/// <summary>
/// Similarity functions on ASCII normal forms. Values are 0..1, where 1 is identical.
/// The implementation follows the two rapidfuzz-compatible measures used by the
/// previous STARS implementation: plain indel ratio and token-set ratio.
/// </summary>
public static class NameSimilarity
{
    public static double Compare(string? left, string? right)
    {
        var a = left ?? string.Empty;
        var b = right ?? string.Empty;
        if (a == b)
        {
            return 1.0;
        }

        if (a.Length == 0 || b.Length == 0)
        {
            return 0.0;
        }

        return Math.Max(Ratio(a, b), TokenSetRatio(a, b));
    }

    public static double Ratio(string left, string right)
    {
        var distance = Levenshtein(left, right);
        var total = left.Length + right.Length;
        return total == 0 ? 1.0 : 1.0 - (double)distance / total;
    }

    public static double TokenSetRatio(string left, string right)
    {
        var a = left.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var b = right.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (a.Count == 0 || b.Count == 0)
        {
            return 0.0;
        }

        var shared = a.Intersect(b).Order(StringComparer.Ordinal).ToArray();
        var onlyA = a.Except(b).Order(StringComparer.Ordinal).ToArray();
        var onlyB = b.Except(a).Order(StringComparer.Ordinal).ToArray();

        var sharedText = string.Join(' ', shared);
        if (shared.Length > 0 && (onlyA.Length == 0 || onlyB.Length == 0))
        {
            return 1.0;
        }

        var onlyAText = string.Join(' ', onlyA);
        var onlyBText = string.Join(' ', onlyB);
        var separator = shared.Length > 0 ? 1 : 0;
        var sharedALength = sharedText.Length + separator + onlyAText.Length;
        var sharedBLength = sharedText.Length + separator + onlyBText.Length;
        var difference = Normalize(Levenshtein(onlyAText, onlyBText), sharedALength + sharedBLength);

        if (shared.Length == 0)
        {
            return difference;
        }

        var sharedA = Normalize(separator + onlyAText.Length, sharedText.Length + sharedALength);
        var sharedB = Normalize(separator + onlyBText.Length, sharedText.Length + sharedBLength);
        return Math.Max(difference, Math.Max(sharedA, sharedB));
    }

    private static double Normalize(int distance, int lengthSum) =>
        lengthSum == 0 ? 1.0 : 1.0 - (double)distance / lengthSum;

    private static int Levenshtein(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }
}
