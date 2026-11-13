using SqlApp.Interop.SqlEngine;

namespace SqlApp.Services;

internal static class TokenGeneration
{
    internal static (byte[] Bytes, string StopReason, double ComputeTimeMs) Generate(
        SqlModel model, SqlTokenizer tokenizer, string input, int limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        int[] prompt = tokenizer.EncodePrompt(input);
        var generated = new int[Math.Min(limit, tokenizer.Context - prompt.Length)];
        var result = model.GenerateIds(prompt, tokenizer.DecodableMask, tokenizer.EosId, limit, generated);
        return (tokenizer.Decode(generated.AsSpan(0, result.Count)), result.StopReason, result.ComputeTimeMs);
    }
}
