namespace SqlApp.Interop.SqlEngine;

internal sealed class SingleModelTokenizer(BpeVocabulary vocabulary) : BpeCodec(vocabulary)
{
    protected override List<int> EncodePieces(string text, int capacity)
    {
        var ids = new List<int>();
        for (int start = 0; start < text.Length;)
        {
            if (ids.Count == capacity) throw ContextExceeded();
            int seed = MatchSeed(text, start);
            if (seed >= 0)
            {
                ids.Add(Vocabulary.SeedBase + seed);
                start += Vocabulary.Seeds[seed].Length;
                continue;
            }
            int end = start + 1;
            if (char.IsAsciiLetter(text[start]))
                while (end < text.Length && char.IsAsciiLetter(text[end])) end++;

            if (end == start + 1)
                ids.Add(text[start]);
            else if (SeedIds.TryGetValue(text[start..end], out int id))
                ids.Add(id); // A whole letter piece can match even in "stock_".
            else
                EncodeMergedPiece(text.AsSpan(start, end - start), ids, capacity);
            start = end;
        }
        return ids;
    }

    private static bool IsWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private int MatchSeed(string text, int start)
    {
        if (start > 0 && IsWord(text[start - 1])) return -1;
        var remaining = text.AsSpan(start);
        foreach (int index in SeedsByLength)
        {
            string seed = Vocabulary.Seeds[index];
            if (remaining.StartsWith(seed, StringComparison.Ordinal) &&
                (remaining.Length == seed.Length || !IsWord(remaining[seed.Length])))
                return index;
        }
        return -1;
    }
}
