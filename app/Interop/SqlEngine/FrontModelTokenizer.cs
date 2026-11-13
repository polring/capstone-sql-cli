using System.Text;

namespace SqlApp.Interop.SqlEngine;

internal sealed class FrontModelTokenizer(BpeVocabulary vocabulary, string[] names, int[] outputIds)
    : BpeCodec(PrepareVocabulary(vocabulary, names, outputIds), outputIds, minimumSeedLength: 1)
{
    private static readonly string[] TagAtoms = ["string", "numeric", "date", "AGG_VALUE", "#1", "#2", "#3"];
    private const string OutputCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789.,?'-<>{}[]*";
    private static readonly int[] OutputBytes = OutputCharacters.Select(c => (int)c).Order().ToArray();

    private static BpeVocabulary PrepareVocabulary(BpeVocabulary vocabulary, string[] names, int[] outputIds)
    {
        if (vocabulary.PadId != 256 || vocabulary.BosId != 257 || vocabulary.EosId != 258 ||
            vocabulary.SepId != 259 || vocabulary.SeedBase != 260 || !vocabulary.Seeds.SequenceEqual(TagAtoms))
            throw new InvalidDataException("앞모델의 특수 토큰 또는 태그 어휘가 올바르지 않습니다.");

        var uniqueNames = new HashSet<string>(StringComparer.Ordinal);
        var seeds = new List<string>(TagAtoms);
        foreach (string name in names)
        {
            if (name.Length == 0 || char.IsAsciiDigit(name[0]) ||
                name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_') || !uniqueNames.Add(name))
                throw new InvalidDataException("앞모델의 스키마 이름이 올바르지 않습니다.");
            // A schema name such as "date" shares its existing tag atom ID.
            if (!TagAtoms.Contains(name)) seeds.Add(name);
        }

        if (vocabulary.MergeBase != vocabulary.SeedBase + seeds.Count ||
            vocabulary.VocabSize < vocabulary.MergeBase || vocabulary.VocabSize > GgufContract.MaxVocab ||
            vocabulary.Merges.Length > vocabulary.VocabSize - vocabulary.MergeBase)
            throw new InvalidDataException("앞모델의 스키마 토큰 또는 BPE 범위가 올바르지 않습니다.");

        var pairs = new HashSet<(int, int)>();
        foreach (var (left, right, _) in vocabulary.Merges)
        {
            if ((left >= 256 && left < vocabulary.MergeBase) || (right >= 256 && right < vocabulary.MergeBase) ||
                !pairs.Add((left, right)))
                throw new InvalidDataException("앞모델의 단어 BPE merge가 올바르지 않습니다.");
        }

        var expected = OutputBytes.Prepend(vocabulary.EosId)
            .Concat(Enumerable.Range(vocabulary.SeedBase, seeds.Count + vocabulary.Merges.Length));
        if (!outputIds.SequenceEqual(expected))
            throw new InvalidDataException("앞모델의 출력 토큰 목록이 올바르지 않습니다.");
        return vocabulary with { Seeds = seeds.ToArray() };
    }

    protected override void ValidateInput(string text) => ArgumentNullException.ThrowIfNull(text);

    protected override List<int> EncodePieces(string text, int capacity)
    {
        var ids = new List<int>();
        Span<byte> utf8 = stackalloc byte[4];
        for (int start = 0; start < text.Length;)
        {
            if (ids.Count == capacity) throw ContextExceeded();
            if (char.IsAsciiLetter(text[start]))
            {
                int end = start + 1;
                while (end < text.Length && char.IsAsciiLetter(text[end])) end++;
                EncodeMergedPiece(text.AsSpan(start, end - start), ids, capacity);
                start = end;
                continue;
            }

            // The mapper emits the UTF-8 bytes of every non-word character, including punctuation.
            if (!Rune.TryGetRuneAt(text, start, out Rune rune))
                throw new ArgumentException("질문에 올바르지 않은 유니코드 문자가 있습니다.", nameof(text));
            int count = rune.EncodeToUtf8(utf8);
            if (count > capacity - ids.Count) throw ContextExceeded();
            for (int i = 0; i < count; i++) ids.Add(utf8[i]);
            start += rune.Utf16SequenceLength;
        }
        return ids;
    }
}
