using System;
using System.Runtime.InteropServices;

namespace SqlEngine.Core
{
    internal static partial class NativeMethods
    {
        private const string LibraryName = "sql_engine";

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sql_pipeline_create(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? dbPath);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sql_pipeline_set_strategy(
            IntPtr pipeline,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string strategy,
            int dim,
            double threshold);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sql_pipeline_set_embedding_path(
            IntPtr pipeline,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sql_pipeline_run(
            IntPtr pipeline,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string question,
            int maxNewTokens,
            out IntPtr outRawSql,
            out IntPtr outStopReason);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sql_pipeline_destroy(IntPtr pipeline);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void sql_free_string(IntPtr str);

        [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr sql_get_last_error();
    }
}
