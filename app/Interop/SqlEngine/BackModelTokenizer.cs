using System.Text.RegularExpressions;

namespace SqlApp.Interop.SqlEngine;

internal sealed class BackModelTokenizer(BpeVocabulary vocabulary) : BpeCodec(vocabulary)
{
    private const string TaggedField = @"TABLE1\.COL[1-6], (?:string|numeric|date)";
    private static readonly Regex TaggedQuestionPieces = new(
        @"(?<tag><TABLE1>|<" + TaggedField + @">|\{" + TaggedField + @"\}|\{\*\}|\[(?:" +
        TaggedField + @"|AGG_VALUE, numeric), #[1-3]\])|[A-Za-z]+|.", RegexOptions.Singleline);
    private static readonly string[] TaggedSeeds =
        ("TABLE1 COL1 COL2 COL3 COL4 COL5 COL6 AGG_VALUE string numeric date #1 #2 #3 " +
         "SELECT FROM WHERE AND OR IN BETWEEN LIKE ORDER BY ASC DESC LIMIT COUNT AVG SUM MIN MAX GROUP HAVING <= >= !=").Split(' ');
    private static readonly string[] AtomicTaggedSeeds = BuildAtomicTaggedSeeds();
    private const string SqlCharacters = " 0123456789<>=.,{}()[]*'%";

    protected override bool AllowNul => Vocabulary.SqlIds != null;

    private static string[] BuildAtomicTaggedSeeds()
    {
        string[] types = ["string", "numeric", "date"];
        var fields = (from column in Enumerable.Range(1, 6) from type in types
                      select $"TABLE1.COL{column}, {type}").ToArray();
        int[] slots = [1, 2, 3];
        // Seed order fixes the token IDs used by the mapper's embeddings and SQL output head.
        return [.. TaggedSeeds, "<TABLE1>", .. fields.Select(f => $"<{f}>"),
            .. fields.Select(f => "{" + f + "}"), "{*}",
            .. from f in fields from slot in slots select $"[{f}, #{slot}]",
            .. slots.Select(slot => $"[AGG_VALUE, numeric, #{slot}]")];
    }

    internal static void ValidateVocabulary(BpeVocabulary vocabulary, ModelRole role)
    {
        bool hasBackVocabulary = vocabulary.Seeds.SequenceEqual(AtomicTaggedSeeds);
        if (vocabulary.SqlIds == null)
        {
            if (hasBackVocabulary)
                throw new InvalidDataException("뒷모델의 SQL 출력 토큰 목록이 없습니다.");
            return;
        }

        if (!hasBackVocabulary || vocabulary.PadId != 256 || vocabulary.BosId != 257 ||
            vocabulary.EosId != 258 || vocabulary.SepId != 259 || vocabulary.SeedBase != 260 ||
            vocabulary.MergeBase != vocabulary.SeedBase + AtomicTaggedSeeds.Length)
            throw new InvalidDataException("완전한 태그 95개를 사용하는 뒷모델이 필요합니다. 새 학습 코드로 학습한 back.gguf를 선택하세요.");

        var pairs = new HashSet<(int, int)>();
        foreach (var (left, right, _) in vocabulary.Merges)
        {
            if ((left >= 256 && left < vocabulary.MergeBase) || (right >= 256 && right < vocabulary.MergeBase) ||
                !pairs.Add((left, right)))
                throw new InvalidDataException("뒷모델의 질문 BPE merge가 올바르지 않습니다.");
        }
        var sqlIds = SqlCharacters.Select(c => (int)c).Order()
            .Concat(Enumerable.Range(vocabulary.SeedBase, vocabulary.Seeds.Length)).Prepend(vocabulary.EosId);
        if (!vocabulary.SqlIds.SequenceEqual(sqlIds))
            throw new InvalidDataException("뒷모델의 SQL 출력 토큰 목록이 올바르지 않습니다.");
        if (role != ModelRole.Back)
            throw new InvalidDataException("태그 SQL 뒷모델은 뒷모델 단계에서 사용하세요.");
    }

    protected override List<int> EncodePieces(string text, int capacity)
    {
        if (Vocabulary.SqlIds != null) return EncodeTaggedQuestion(text, capacity);

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

    private List<int> EncodeTaggedQuestion(string text, int capacity)
    {
        var ids = new List<int>();
        foreach (Match piece in TaggedQuestionPieces.Matches(text))
        {
            if (ids.Count == capacity) throw ContextExceeded();
            if (piece.Groups["tag"].Success)
                ids.Add(SeedIds[piece.Value]);
            else if (piece.Length == 1)
                ids.Add(piece.ValueSpan[0]);
            else
                EncodeMergedPiece(piece.ValueSpan, ids, capacity);
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
