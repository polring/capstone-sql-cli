using System.Text;

namespace SqlApp.Interop.SqlEngine;

internal readonly record struct BpeMerge(int Left, int Right, int Result);

internal sealed record BpeVocabulary(int VocabSize, int Context, int PadId, int BosId, int EosId, int SepId,
    int SeedBase, int MergeBase, string[] Seeds, BpeMerge[] Merges, int[]? SqlIds);

// Shared GGUF vocabulary, prompt framing and byte codec. Each model tokenizes its own input pieces.
internal abstract class BpeCodec
{
    protected BpeVocabulary Vocabulary { get; }
    protected IReadOnlyDictionary<string, int> SeedIds { get; }
    protected int[] SeedsByLength { get; }
    private readonly int[] _byteLengths;
    internal int VocabSize => Vocabulary.VocabSize;
    internal int Context => Vocabulary.Context;
    internal int EosId => Vocabulary.EosId;
    internal byte[] DecodableMask { get; }

    protected BpeCodec(BpeVocabulary vocabulary, int[]? outputIds = null, int minimumSeedLength = 2)
    {
        Vocabulary = vocabulary;
        var (vocabSize, context, padId, bosId, eosId, sepId, seedBase, mergeBase, seeds, merges, _) = vocabulary;
        if (vocabSize is < 256 or > GgufContract.MaxVocab || context is < 1 or > 4096 ||
            seedBase < 256 || mergeBase < seedBase || mergeBase > vocabSize ||
            seeds.Length > mergeBase - seedBase || merges.Length > vocabSize - mergeBase)
            throw new InvalidDataException("GGUF 토크나이저의 토큰 범위가 올바르지 않습니다.");

        int[] special = [padId, bosId, eosId, sepId];
        if (special.Distinct().Count() != special.Length ||
            special.Any(id => id < 256 || id >= mergeBase || (id >= seedBase && id - seedBase < seeds.Length)))
            throw new InvalidDataException("GGUF 특수 토큰 ID가 올바르지 않습니다.");

        _byteLengths = new int[vocabSize];
        Array.Fill(_byteLengths, 1, 0, 256);
        var seedIds = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < seeds.Length; i++)
        {
            string seed = seeds[i];
            if (seed.Length < minimumSeedLength || char.IsWhiteSpace(seed[0]) || char.IsWhiteSpace(seed[^1]) ||
                seed.Any(c => c is '\0' or > '\x7f') || !seedIds.TryAdd(seed, seedBase + i))
                throw new InvalidDataException("GGUF seed 토큰이 올바르지 않습니다.");
            _byteLengths[seedBase + i] = seed.Length;
        }
        SeedIds = seedIds;
        SeedsByLength = Enumerable.Range(0, seeds.Length).OrderByDescending(i => seeds[i].Length).ToArray();

        // Keep a merge tree and its decoded lengths. Expand only tokens actually generated.
        for (int i = 0; i < merges.Length; i++)
        {
            var (left, right, result) = merges[i];
            if (result != mergeBase + i || (uint)left >= (uint)result || (uint)right >= (uint)result ||
                _byteLengths[left] == 0 || _byteLengths[right] == 0)
                throw new InvalidDataException("GGUF merge 토큰 참조가 올바르지 않습니다.");
            int length = _byteLengths[left] + _byteLengths[right];
            if (length > GgufContract.MaxTokenBytes) throw new InvalidDataException("GGUF merge 토큰이 너무 큽니다.");
            _byteLengths[result] = length;
        }

        if (outputIds == null)
            DecodableMask = Array.ConvertAll(_byteLengths, length => length > 0 ? (byte)1 : (byte)0);
        else
        {
            // The model's validated list starts with EOS, which the native generator handles separately.
            DecodableMask = new byte[vocabSize];
            foreach (int id in outputIds.AsSpan(1)) DecodableMask[id] = 1;
        }
    }

    protected virtual bool AllowNul => false;
    protected abstract List<int> EncodePieces(string text, int capacity);

    internal int[] Encode(string text)
    {
        ValidateInput(text);
        return EncodePieces(text, Context).ToArray();
    }

    internal int[] EncodePrompt(string question)
    {
        ValidateInput(question);
        if (Context < 2) throw ContextExceeded();
        var content = EncodePieces(question, Context - 2);
        return [Vocabulary.BosId, .. content, Vocabulary.SepId];
    }

    protected virtual void ValidateInput(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c == '\0' && !AllowNul) throw new ArgumentException("질문에는 NUL 문자를 사용할 수 없습니다.", nameof(text));
            if (c > '\x7f') throw new ArgumentException("현재 모델은 ASCII 영문 입력만 지원합니다.", nameof(text));
        }
    }

    protected void EncodeMergedPiece(ReadOnlySpan<char> piece, List<int> ids, int capacity)
    {
        int count = piece.Length;
        var tokens = new int[count];
        for (int i = 0; i < count; i++) tokens[i] = piece[i];
        foreach (var merge in Vocabulary.Merges)
        {
            if (count == 1) break;
            int written = 0;
            for (int i = 0; i < count; i++)
            {
                if (i + 1 < count && tokens[i] == merge.Left && tokens[i + 1] == merge.Right)
                {
                    tokens[written++] = merge.Result;
                    i++;
                }
                else tokens[written++] = tokens[i];
            }
            count = written;
        }
        if (count > capacity - ids.Count) throw ContextExceeded();
        for (int i = 0; i < count; i++) ids.Add(tokens[i]);
    }

    protected static InvalidOperationException ContextExceeded() => new("질문이 모델의 문맥 길이를 초과합니다.");

    internal byte[] Decode(ReadOnlySpan<int> tokenIds)
    {
        int length = 0;
        foreach (int id in tokenIds)
        {
            if ((uint)id >= (uint)VocabSize || DecodableMask[id] == 0)
                throw new ArgumentException("디코딩할 수 없는 토큰 ID입니다.", nameof(tokenIds));
            if (_byteLengths[id] > (1 << 30) - length) throw new InvalidOperationException("생성 결과가 너무 큽니다.");
            length += _byteLengths[id];
        }

        var bytes = new byte[length];
        var pending = new Stack<int>();
        int position = 0;
        foreach (int tokenId in tokenIds)
        {
            pending.Push(tokenId);
            while (pending.TryPop(out int id))
            {
                if (id < 256) bytes[position++] = (byte)id;
                else if (id < Vocabulary.MergeBase)
                    position += Encoding.ASCII.GetBytes(Vocabulary.Seeds[id - Vocabulary.SeedBase].AsSpan(), bytes.AsSpan(position));
                else
                {
                    var merge = Vocabulary.Merges[id - Vocabulary.MergeBase];
                    pending.Push(merge.Right);
                    pending.Push(merge.Left);
                }
            }
        }
        return bytes;
    }
}
