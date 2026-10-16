using System;
using System.Runtime.InteropServices;

namespace SqlEngine.Core
{
    public sealed class SqlPipeline : IDisposable
    {
        private IntPtr _handle;
        private bool _disposed;

        private SqlPipeline(IntPtr handle)
        {
            _handle = handle;
        }

        public static SqlPipeline Create(string modelPath, string? dbPath = null)
        {
            if (string.IsNullOrWhiteSpace(modelPath))
            {
                throw new ArgumentException("Model path cannot be null or empty", nameof(modelPath));
            }

            IntPtr handle = NativeMethods.sql_pipeline_create(modelPath, dbPath);
            if (handle == IntPtr.Zero)
            {
                IntPtr errPtr = NativeMethods.sql_get_last_error();
                string err = errPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(errPtr) ?? "Unknown error" : "Unknown error";
                throw new InvalidOperationException($"Failed to create SqlPipeline: {err}");
            }

            return new SqlPipeline(handle);
        }

        public void SetStrategy(string strategy, int dim = 0, double threshold = 0.0)
        {
            ThrowIfDisposed();
            NativeMethods.sql_pipeline_set_strategy(_handle, strategy, dim, threshold);
        }

        public void SetEmbeddingPath(string path)
        {
            ThrowIfDisposed();
            NativeMethods.sql_pipeline_set_embedding_path(_handle, path);
        }

        public SqlResult Run(string question, int maxNewTokens = 32)
        {
            ThrowIfDisposed();
            if (question == null)
            {
                throw new ArgumentNullException(nameof(question));
            }

            IntPtr rawSqlPtr = IntPtr.Zero;
            IntPtr stopReasonPtr = IntPtr.Zero;

            IntPtr sqlPtr = NativeMethods.sql_pipeline_run(_handle, question, maxNewTokens, out rawSqlPtr, out stopReasonPtr);
            if (sqlPtr == IntPtr.Zero)
            {
                IntPtr errPtr = NativeMethods.sql_get_last_error();
                string err = errPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(errPtr) ?? "Inference failed" : "Inference failed";
                throw new InvalidOperationException($"SqlPipeline execution failed: {err}");
            }

            try
            {
                string sql = Marshal.PtrToStringUTF8(sqlPtr) ?? string.Empty;
                string? rawSql = rawSqlPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(rawSqlPtr) : null;
                string? stopReason = stopReasonPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(stopReasonPtr) : null;

                return new SqlResult(sql, rawSql, stopReason);
            }
            finally
            {
                if (sqlPtr != IntPtr.Zero)
                {
                    NativeMethods.sql_free_string(sqlPtr);
                }
                if (rawSqlPtr != IntPtr.Zero)
                {
                    NativeMethods.sql_free_string(rawSqlPtr);
                }
            }
        }

        public static string Postprocess(string sql, string strategy, string? dbPath = null, int dim = 0, double threshold = 0.0)
        {
            if (sql == null)
            {
                throw new ArgumentNullException(nameof(sql));
            }

            IntPtr resPtr = NativeMethods.sql_postprocess(sql, strategy, dbPath, dim, threshold);
            if (resPtr == IntPtr.Zero)
            {
                IntPtr errPtr = NativeMethods.sql_get_last_error();
                string err = errPtr != IntPtr.Zero ? Marshal.PtrToStringUTF8(errPtr) ?? "Postprocessing failed" : "Postprocessing failed";
                throw new InvalidOperationException($"Postprocessing failed: {err}");
            }

            try
            {
                return Marshal.PtrToStringUTF8(resPtr) ?? string.Empty;
            }
            finally
            {
                NativeMethods.sql_free_string(resPtr);
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SqlPipeline));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_handle != IntPtr.Zero)
                {
                    NativeMethods.sql_pipeline_destroy(_handle);
                    _handle = IntPtr.Zero;
                }
                _disposed = true;
            }
        }
    }
}
