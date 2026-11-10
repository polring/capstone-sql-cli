using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SqlApp.Models;

namespace SqlApp.Services
{
    public class TableSchemaInfo
    {
        public string TableName { get; set; } = string.Empty;
        public List<string> Columns { get; set; } = new();
    }

    public class SqlExecutionService
    {
        public async Task<QueryResultData> ExecuteQueryAsync(string dbPath, string sql, int maxRows = 100)
        {
            var result = new QueryResultData();
            if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
            {
                result.ErrorMessage = $"데이터베이스 파일을 찾을 수 없습니다: {dbPath}";
                return result;
            }

            if (string.IsNullOrWhiteSpace(sql))
            {
                result.ErrorMessage = "실행할 SQL 쿼리가 비어 있습니다.";
                return result;
            }

            var sw = Stopwatch.StartNew();

            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = dbPath,
                    Mode = SqliteOpenMode.ReadOnly
                }.ToString();

                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();

                await using var command = connection.CreateCommand();
                command.CommandText = sql;

                await using var reader = await command.ExecuteReaderAsync();

                for (int i = 0; i < reader.FieldCount; i++)
                {
                    result.Columns.Add(reader.GetName(i));
                }

                while (await reader.ReadAsync() && result.Rows.Count < maxRows)
                {
                    var row = new List<string>(reader.FieldCount);
                    for (int i = 0; i < reader.FieldCount; i++)
                    {
                        row.Add(reader.IsDBNull(i) ? "NULL" : reader.GetValue(i)?.ToString() ?? string.Empty);
                    }
                    result.Rows.Add(row);
                }

                sw.Stop();
                result.ExecutionTimeMs = sw.ElapsedMilliseconds;
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ExecutionTimeMs = sw.ElapsedMilliseconds;
                result.ErrorMessage = $"SQL 실행 오류: {ex.Message}";
            }

            return result;
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
                    colCmd.CommandText = $"PRAGMA table_info({table});";
                    await using var colReader = await colCmd.ExecuteReaderAsync();
                    while (await colReader.ReadAsync())
                    {
                        string colName = colReader.GetString(1);
                        string colType = colReader.GetString(2);
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
