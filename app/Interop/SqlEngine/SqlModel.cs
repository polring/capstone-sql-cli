using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SqlApp.Interop.SqlEngine;

internal sealed class SqlModel : SafeHandleZeroOrMinusOneIsInvalid
{
    private SqlModel() : base(true) { }

    internal static SqlModel Open(string path)
    {
        var model = Native.Open(path);
        if (model.IsInvalid)
        {
            model.Dispose();
            throw EngineError();
        }
        return model;
    }

    internal (int Count, string StopReason, double ComputeTimeMs) GenerateIds(
        int[] prompt, byte[] decodableMask, int eosId, int limit, int[] output)
    {
        if (Native.GenerateIds(this, prompt, prompt.Length, decodableMask, decodableMask.Length,
            eosId, limit, output, output.Length, out int count, out var stop, out double computeTimeMs) == 0)
            throw EngineError();
        string stopReason = stop switch
        {
            StopReason.Eos => "eos",
            StopReason.MaxNewTokens => "max_new_tokens",
            StopReason.Context => "context",
            _ => throw new InvalidOperationException($"알 수 없는 추론 종료 코드입니다 ({(int)stop}).")
        };
        return (count, stopReason, computeTimeMs);
    }

    // Full-prefix reference for checking native logits against the contract fixtures.
    internal double Forward(int[] tokenIds, int tokenCount, float[] logits)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tokenCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tokenCount, tokenIds.Length);
        if (Native.Forward(this, tokenIds, tokenCount, logits, logits.Length, out double elapsedMs) == 0)
            throw EngineError();
        return elapsedMs;
    }

    private static InvalidOperationException EngineError() =>
        new(Marshal.PtrToStringUTF8(Native.LastError()) ?? "GGUF 추론 오류");

    protected override bool ReleaseHandle()
    {
        Native.Close(handle);
        return true;
    }

    private enum StopReason { Eos = 0, MaxNewTokens = 1, Context = 2 }

    private static class Native
    {
        private const string Library = "sql_engine";

        [DllImport(Library, EntryPoint = "sql_model_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern SqlModel Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Library, EntryPoint = "sql_model_forward", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Forward(SqlModel model, [In] int[] tokenIds, int tokenCount,
            [Out] float[] logits, int logitsCapacity, out double elapsedMs);

        [DllImport(Library, EntryPoint = "sql_model_generate_ids", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GenerateIds(SqlModel model, [In] int[] prompt, int promptCount,
            [In] byte[] decodableMask, int maskCount, int eosId, int limit, [Out] int[] output,
            int outputCapacity, out int outputCount, out StopReason stop, out double computeTimeMs);

        [DllImport(Library, EntryPoint = "sql_model_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Close(IntPtr model);

        [DllImport(Library, EntryPoint = "sql_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr LastError();
    }
}
