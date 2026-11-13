using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlApp.Models;
using SQLitePCL;

namespace SqlApp.Services
{
    public class TableSchemaInfo
    {
        public string TableName { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
    }

    public class SqlExecutionService
    {
        internal async Task ValidateMessageAsync(ChatMessage message)
        {
            var candidates = message.SqlCandidates;
            var queries = candidates.Where(c => c.RestorationError.Length == 0).ToArray();
            if (queries.Length == 0) return;
            var errors = await ValidateQueriesAsync(message.DbPath, queries.Select(c => c.Sql).ToArray());
            if (!ReferenceEquals(candidates, message.SqlCandidates)) return;
            for (int i = 0; i < queries.Length; i++)
                queries[i].ValidationError = errors[i] ?? string.Empty;
            message.NotifySqlValidation();
        }

        public async Task<string?> ValidateQueryAsync(string dbPath, string sql) =>
            (await ValidateQueriesAsync(dbPath, [sql]))[0];

        private static Task<string?[]> ValidateQueriesAsync(string dbPath, string[] sql) => Task.Run(() =>
        {
            var errors = new string?[sql.Length];
            try
            {
                using var connection = CreateQueryConnection(dbPath);
                connection.Open();
                for (int i = 0; i < sql.Length; i++)
                    errors[i] = CheckQuery(connection, sql[i]);
            }
            catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                Array.Fill(errors, $"SQL 검사 오류: {ex.Message}");
            }
            return errors;
        });

        public async Task<QueryResultData> ExecuteQueryAsync(string dbPath, string sql, int maxRows = 100)
        {
            var result = new QueryResultData();
            var sw = Stopwatch.StartNew();

            try
            {
                await using var connection = CreateQueryConnection(dbPath);
                await connection.OpenAsync();
                result.ErrorMessage = CheckQuery(connection, sql);
                if (result.ErrorMessage != null) return result;

                await using var command = connection.CreateCommand();
                command.CommandText = sql;

                await using var reader = await command.ExecuteReaderAsync();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    result.Columns.Add(reader.GetName(i));
                }

                while (result.Rows.Count < maxRows && await reader.ReadAsync())
                {
                    var row = new List<string>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row.Add(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? string.Empty);
                    }
                    result.Rows.Add(row);
                }
            }
            catch (Exception ex)
            {
                result.ErrorMessage = $"SQL 실행 오류: {ex.Message}";
            }
            finally
            {
                result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            }

            return result;
        }

        private static SqliteConnection CreateQueryConnection(string dbPath)
        {
            if (!File.Exists(dbPath))
                throw new FileNotFoundException($"데이터베이스 파일을 찾을 수 없습니다: {dbPath}");
            return new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false // The authorizer belongs only to this query connection.
            }.ToString());
        }

        private static string? CheckQuery(SqliteConnection connection, string sql)
        {
            const string readOnlyError = "읽기 전용 조회 SQL만 실행할 수 있습니다.";
            if (string.IsNullOrWhiteSpace(sql)) return "실행할 SQL이 비어 있습니다.";
            if (sql.Contains('\0')) return "SQL에 NUL 문자가 있습니다.";

            var db = connection.Handle!;
            SqliteException.ThrowExceptionForRC(raw.sqlite3_set_authorizer(db,
                (strdelegate_authorizer)AuthorizeQuery, null), db);
            bool found = false;
            string remaining = sql;
            while (!string.IsNullOrEmpty(remaining))
            {
                // SQLite parses quotes, comments and statement boundaries. Never step a
                // statement here: runtime expression evaluation belongs to the Run button.
                int rc = raw.sqlite3_prepare_v2(db, remaining, out var statement, out string tail);
                using (statement)
                {
                    if (rc == raw.SQLITE_AUTH) return readOnlyError;
                    if (rc != raw.SQLITE_OK) return $"SQLite 검사 오류: {raw.sqlite3_errmsg(db).utf8_to_string()}";
                    if (statement != null && !statement.IsInvalid)
                    {
                        if (found) return "한 번에 하나의 SQL문만 실행할 수 있습니다.";
                        if (raw.sqlite3_column_count(statement) == 0 ||
                            (raw.sqlite3_stmt_isexplain(statement) == 0 && raw.sqlite3_stmt_readonly(statement) == 0))
                            return readOnlyError;
                        if (raw.sqlite3_bind_parameter_count(statement) != 0)
                            return "값이 지정되지 않은 SQL 매개변수가 있습니다.";
                        found = true;
                    }
                }
                remaining = tail;
            }
            return found ? null : "실행할 SQL이 비어 있습니다.";
        }

        private static readonly HashSet<string> InspectionPragmas = new(StringComparer.OrdinalIgnoreCase)
        {
            "table_info", "table_xinfo", "table_list", "index_info", "index_xinfo", "index_list",
            "foreign_key_list", "foreign_key_check", "integrity_check", "quick_check"
        };
        private static readonly HashSet<string> MaintenancePragmas = new(StringComparer.OrdinalIgnoreCase)
        {
            "optimize", "wal_checkpoint", "incremental_vacuum", "shrink_memory"
        };

        private static int AuthorizeQuery(object? _, int action, string? name, string? argument,
            string? database, string? source)
        {
            if (action is raw.SQLITE_ATTACH or raw.SQLITE_DETACH) return raw.SQLITE_DENY;
            if (action != raw.SQLITE_PRAGMA) return raw.SQLITE_OK;
            // Some PRAGMAs take effect during prepare, before stmt_readonly is available.
            // Allow inspection with an object name (e.g. table_info), and scalar getters.
            if (name != null && InspectionPragmas.Contains(name)) return raw.SQLITE_OK;
            if (name != null && MaintenancePragmas.Contains(name)) return raw.SQLITE_DENY;
            return argument == null ? raw.SQLITE_OK : raw.SQLITE_DENY;
        }

        public async Task<List<TableSchemaInfo>> GetDatabaseSchemaAsync(string dbPath)
        {
            var schemas = new List<TableSchemaInfo>();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                return schemas;
            }

            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                // Get table names
                var tableNames = new List<string>();
                await using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name;";
                    await using var reader = await cmd.ExecuteReaderAsync();
                    while (await reader.ReadAsync())
                    {
                        tableNames.Add(reader.GetString(0));
                    }
                }

                // Get columns for each table
                foreach (var table in tableNames)
                {
                    var info = new TableSchemaInfo { TableName = table };
                    await using var colCmd = connection.CreateCommand();
                    colCmd.CommandText = "SELECT name, type FROM pragma_table_info($table) ORDER BY cid";
                    colCmd.Parameters.AddWithValue("$table", table);
                    await using var colReader = await colCmd.ExecuteReaderAsync();
                    while (await colReader.ReadAsync())
                    {
                        string colName = colReader.GetString(0);
                        string colType = colReader.GetString(1);
                        info.Columns.Add(string.IsNullOrWhiteSpace(colType) ? colName : $"{colName} ({colType})");
                    }
                    schemas.Add(info);
                }
            }
            catch { }

            return schemas;
        }
    }
}
