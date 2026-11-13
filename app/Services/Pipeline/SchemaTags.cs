using System.Text.RegularExpressions;
using SqlApp.Models;

namespace SqlApp.Services.Pipeline;

internal static class SchemaTags
{
    internal const string Name = @"[A-Za-z_][A-Za-z_0-9]*";
    internal const string Type = "(?:string|numeric|date)";
    internal const string NumericSqlType = "INT|REAL|FLOA|DOUB|NUMERIC|DECIMAL";
    internal const string Field = Name + @"\." + Name + @",\s*" + Type;
    internal const string Value = @"\[(?:" + Name + @"\." + Name + @"|AGG_VALUE),\s*" + Type + @",\s*#[1-3]\]";
    internal const string Mark = @"(?:<" + Name + @">|<" + Field + @">|\{" + Field + @"\}|\{\*\}|" + Value + ")";
    internal static readonly Regex Marks = new(Mark, RegexOptions.CultureInvariant);
    private static readonly Regex ValidName = new("^" + Name + "$", RegexOptions.CultureInvariant);

    internal static TableSchemaInfo Table(IReadOnlyList<TableSchemaInfo> schema, string name)
    {
        var table = schema.FirstOrDefault(t => string.Equals(t.TableName, name, StringComparison.OrdinalIgnoreCase));
        if (!ValidName.IsMatch(name) || table == null)
            throw new FormatException($"DB 스키마에서 테이블 '{name}'을 찾을 수 없습니다.");
        return table;
    }

    internal static string Column(TableSchemaInfo table, string name, string type)
    {
        if (!ValidName.IsMatch(name) || type is not ("numeric" or "string" or "date"))
            throw new FormatException($"지원하지 않는 컬럼 태그입니다: {name}, {type}");
        foreach (string descriptor in table.Columns)
        {
            int separator = descriptor.IndexOf(" (", StringComparison.Ordinal);
            string columnName = separator < 0 ? descriptor : descriptor[..separator];
            if (!name.Equals(columnName, StringComparison.OrdinalIgnoreCase)) continue;
            if (separator >= 0)
            {
                string declared = descriptor[(separator + 2)..].TrimEnd(')');
                string pattern = type switch
                {
                    "numeric" => NumericSqlType,
                    "string" => "CHAR|TEXT|CLOB",
                    _ => "DATE|CHAR|TEXT|CLOB"
                };
                if (!Regex.IsMatch(declared, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                    throw new FormatException($"컬럼 자료형이 일치하지 않습니다: {name} ({declared}), {type}");
            }
            return columnName;
        }
        throw new FormatException($"DB 스키마에서 컬럼 '{table.TableName}.{name}'을 찾을 수 없습니다.");
    }

    // All candidates in a run use the same tag order. A value cannot contain NUL.
    internal static string ValueTupleKey(IEnumerable<string> tagOrder, IReadOnlyDictionary<string, string> values) =>
        string.Join('\0', tagOrder.Select(tag => values[tag]));

    internal static void ValidateRecord(MappingRecord record, IReadOnlyList<TableSchemaInfo> schema)
    {
        if (record.Tables == null || record.Tables.Count == 0 || record.Columns == null || record.Values == null)
            throw new FormatException("복원 후보의 테이블, 컬럼, 값 대응표를 확인하세요.");
        foreach (var (alias, table) in record.Tables)
        {
            if (!Regex.IsMatch(alias, @"\ATABLE[1-3]\z") || string.IsNullOrEmpty(table))
                throw new FormatException($"잘못된 테이블 대응입니다: {alias}");
            Table(schema, table);
        }
        foreach (var (alias, column) in record.Columns)
        {
            if (!Regex.IsMatch(alias, @"\ATABLE[1-3]\.COL[1-6]\z") || column == null ||
                !record.Tables.TryGetValue(alias.Split('.')[0], out var table) || string.IsNullOrEmpty(column.Name))
                throw new FormatException($"잘못된 컬럼 대응입니다: {alias}");
            Column(Table(schema, table), column.Name, column.Type);
        }
        foreach (var (mark, rawValue) in record.Values)
        {
            if (!Regex.IsMatch(mark, @"\A" + Value + @"\z") || string.IsNullOrWhiteSpace(rawValue) ||
                rawValue.Contains('\0'))
                throw new FormatException($"잘못된 값 대응입니다: {mark}");
            var parts = mark[1..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts[0] == "AGG_VALUE")
            {
                if (mark != "[AGG_VALUE, numeric, #1]") throw new FormatException($"잘못된 집계 값 태그입니다: {mark}");
            }
            else if (!record.Columns.TryGetValue(parts[0], out var column) || column.Type != parts[1])
                throw new FormatException($"대응하는 컬럼이나 유형이 없습니다: {mark}");
        }
    }
}
