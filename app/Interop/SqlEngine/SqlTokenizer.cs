using System.Text;

namespace SqlApp.Interop.SqlEngine;

internal readonly record struct BpeMerge(int Left, int Right, int Result);

// capstone_sql v2 byte-level BPE. Each instance uses its own model's vocabulary.
internal sealed class SqlTokenizer
{
    private readonly int _bosId;
    private readonly int _sepId;
    private readonly int _seedBase;
    private readonly int _mergeBase;
    private readonly string[] _seeds;
    private readonly BpeMerge[] _merges;
    private readonly Dictionary<string, int> _seedIds = new(StringComparer.Ordinal);
    private readonly int[] _seedsByLength;
    private readonly int[] _byteLengths;

    internal int VocabSize => _byteLengths.Length;
    internal int Context { get; }
    internal int EosId { get; }
    // The native generator also considers EOS separately, including on ties.
    internal byte[] DecodableMask { get; }

    internal SqlTokenizer(int vocabSize, int context, int padId, int bosId, int eosId, int sepId,
        int seedBase, int mergeBase, string[] seeds, BpeMerge[] merges)
    {
        if (vocabSize is < 256 or > GgufContract.MaxVocab || context is < 1 or > 4096 ||
            seedBase < 256 || mergeBase < seedBase || mergeBase > vocabSize ||
            seeds.Length > mergeBase - seedBase || merges.Length > vocabSize - mergeBase)
            throw new InvalidDataException("GGUF 토크나이저의 토큰 범위가 올바르지 않습니다.");

        int[] special = [padId, bosId, eosId, sepId];
        if (special.Distinct().Count() != special.Length ||
            special.Any(id => id < 256 || id >= mergeBase || (id >= seedBase && id - seedBase < seeds.Length)))
            throw new InvalidDataException("GGUF 특수 토큰 ID가 올바르지 않습니다.");

        (Context, _bosId, EosId, _sepId) = (context, bosId, eosId, sepId);
        (_seedBase, _mergeBase, _seeds, _merges) = (seedBase, mergeBase, seeds, merges);
        _byteLengths = new int[vocabSize];
        Array.Fill(_byteLengths, 1, 0, 256);
        for (int i = 0; i < seeds.Length; i++)
        {
            string seed = seeds[i];
            if (seed.Length < 2 || char.IsWhiteSpace(seed[0]) || char.IsWhiteSpace(seed[^1]) ||
                seed.Any(c => c is '\0' or > '\x7f') || !_seedIds.TryAdd(seed, seedBase + i))
                throw new InvalidDataException("GGUF seed 토큰이 올바르지 않습니다.");
            _byteLengths[seedBase + i] = seed.Length;
        }
        _seedsByLength = Enumerable.Range(0, seeds.Length).OrderByDescending(i => seeds[i].Length).ToArray();

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
        DecodableMask = Array.ConvertAll(_byteLengths, length => length > 0 ? (byte)1 : (byte)0);
    }

    internal int[] Encode(string text)
    {
        ValidateAscii(text);
        return EncodePieces(text, Context).ToArray();
    }

    internal int[] EncodePrompt(string question)
    {
        ValidateAscii(question);
        if (Context < 2) throw ContextExceeded();
        var content = EncodePieces(question, Context - 2);
        return [_bosId, .. content, _sepId];
    }

    private static void ValidateAscii(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        foreach (char c in text)
        {
            if (c == '\0') throw new ArgumentException("질문에는 NUL 문자를 사용할 수 없습니다.", nameof(text));
            if (c > '\x7f') throw new ArgumentException("현재 모델은 ASCII 영문 입력만 지원합니다.", nameof(text));
        }
    }

    private List<int> EncodePieces(string text, int capacity)
    {
        var ids = new List<int>();
        for (int start = 0; start < text.Length;)
        {
            if (ids.Count == capacity) throw ContextExceeded();
            int seed = MatchSeed(text, start);
            if (seed >= 0)
            {
                ids.Add(_seedBase + seed);
                start += _seeds[seed].Length;
                continue;
            }
            int end = start + 1;
            if (char.IsAsciiLetter(text[start]))
                while (end < text.Length && char.IsAsciiLetter(text[end])) end++;

            if (end == start + 1)
                ids.Add(text[start]);
            else if (_seedIds.TryGetValue(text[start..end], out int id))
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
        foreach (int index in _seedsByLength)
        {
            string seed = _seeds[index];
            if (remaining.StartsWith(seed, StringComparison.Ordinal) &&
                (remaining.Length == seed.Length || !IsWord(remaining[seed.Length])))
                return index;
        }
        return -1;
    }

    private void EncodeMergedPiece(ReadOnlySpan<char> piece, List<int> ids, int capacity)
    {
        int count = piece.Length;
        var tokens = new int[count];
        for (int i = 0; i < count; i++) tokens[i] = piece[i];
        foreach (var merge in _merges)
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

    private static InvalidOperationException ContextExceeded() => new("질문이 모델의 문맥 길이를 초과합니다.");

    internal byte[] Decode(ReadOnlySpan<int> tokenIds)
    {
        int length = 0;
        foreach (int id in tokenIds)
        {
            if ((uint)id >= (uint)VocabSize || _byteLengths[id] == 0)
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
                else if (id < _mergeBase)
                    position += Encoding.ASCII.GetBytes(_seeds[id - _seedBase].AsSpan(), bytes.AsSpan(position));
                else
                {
                    var merge = _merges[id - _mergeBase];
                    pending.Push(merge.Right);
                    pending.Push(merge.Left);
                }
            }
        }
        return bytes;
    }
}
