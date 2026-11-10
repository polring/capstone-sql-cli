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

    internal (string Sql, string StopReason) Generate(string question, int limit)
    {
        if (question.Contains('\0'))
            throw new ArgumentException("질문에는 NUL 문자를 사용할 수 없습니다.", nameof(question));
        if (Native.Generate(this, question, limit, out var result) == 0)
            throw EngineError();
        try
        {
            string sql = Marshal.PtrToStringUTF8(result.Data, checked((int)result.Length))!;
            return (sql, Marshal.PtrToStringUTF8(result.StopReason)!);
        }
        finally
        {
            Native.Free(ref result);
        }
    }

    private static InvalidOperationException EngineError() =>
        new(Marshal.PtrToStringUTF8(Native.LastError()) ?? "GGUF 추론 오류");

    protected override bool ReleaseHandle()
    {
        Native.Close(handle);
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Generation
    {
        public IntPtr Data;
        public nuint Length;
        public IntPtr StopReason;
    }

    private static class Native
    {
        private const string Library = "sql_engine";

        [DllImport(Library, EntryPoint = "sql_model_open", CallingConvention = CallingConvention.Cdecl)]
        internal static extern SqlModel Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(Library, EntryPoint = "sql_model_generate", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Generate(SqlModel model, [MarshalAs(UnmanagedType.LPUTF8Str)] string question,
            int limit, out Generation result);

        [DllImport(Library, EntryPoint = "sql_generation_free", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Free(ref Generation result);

        [DllImport(Library, EntryPoint = "sql_model_close", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void Close(IntPtr model);

        [DllImport(Library, EntryPoint = "sql_get_last_error", CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr LastError();
    }
}
