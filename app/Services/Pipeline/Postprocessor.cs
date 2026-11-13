using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlApp.Models;

namespace SqlApp.Services.Pipeline;

public static class Postprocessor
{
    private const string ColumnId = @"TABLE[1-3]\.COL[1-6]";
    private const string Field = ColumnId + @",\s*" + SchemaTags.Type;
    private const string Aggregate = @"(?:COUNT\(\*\)|(?:AVG|SUM|MIN|MAX)\(<" + Field + @">\))";
    private const string TokenPattern = @"\G(?<space>\s*)(?<token>" +
        @"\{(?:\*|" + Field + "|" + Aggregate + @")\}|<TABLE[1-3]>|<" + Field + @">|" +
        @"'%?" + SchemaTags.Value + @"%?'|" + SchemaTags.Value + @"|[A-Z_][A-Z_0-9]*|" +
        @"[0-9]+(?:\.[0-9]+)?|!=|<=|>=|[=<>(),*;])";
    private static readonly Regex Tokens = new(TokenPattern, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "BETWEEN", "IN", "LIKE", "ORDER", "BY",
        "ASC", "DESC", "LIMIT", "GROUP", "HAVING", "AVG", "COUNT", "MAX", "MIN", "SUM"
    };

    private sealed class Token(string raw, string space, int position)
    {
        public string Raw { get; } = raw;
        public string Space { get; } = space;
        public int Position { get; } = position;
        public string Sql { get; set; } = raw;
        public string? ValueColumn { get; set; }
        public decimal? OrderValue { get; set; }
    }

    public static List<RestoredSqlCandidate> RestoreCandidates(string taggedSql,
        IReadOnlyList<MappingRecord> mappings, IReadOnlyList<TableSchemaInfo> schema)
    {
        var results = new List<RestoredSqlCandidate>();
        var bySql = new Dictionary<string, RestoredSqlCandidate>(StringComparer.Ordinal);
        for (int i = 0; i < mappings.Count; i++)
        {
            try
            {
                string sql = Restore(taggedSql, mappings[i], schema);
                if (!bySql.TryGetValue(sql, out var candidate))
                {
                    candidate = new RestoredSqlCandidate { Sql = sql };
                    bySql.Add(sql, candidate);
                    results.Add(candidate);
                }
                candidate.MappingIndices.Add(i);
            }
            catch (FormatException ex)
            {
                results.Add(new RestoredSqlCandidate { Error = ex.Message, MappingIndices = [i] });
            }
        }
        return results;
    }

    public static string Restore(string taggedSql, MappingRecord record, IReadOnlyList<TableSchemaInfo> schema)
    {
        SchemaTags.ValidateRecord(record, schema);
        string text = taggedSql.Trim();
        var tokens = new List<Token>();
        for (int index = 0; index < text.Length;)
        {
            var match = Tokens.Match(text, index);
            if (!match.Success) throw new FormatException($"지원하지 않는 SQL 또는 태그입니다 (위치 {index}).");
            var word = match.Groups["token"];
            tokens.Add(new Token(word.Value, match.Groups["space"].Value, word.Index));
            index += match.Length;
        }
        if (tokens.Count == 0 || !Is(tokens[0], "SELECT"))
            throw new FormatException("태그 SQL은 SELECT로 시작해야 합니다.");
        bool hasTable = false;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token token = tokens[i];
            string raw = token.Raw;
            if (i > 0 && Is(tokens[i - 1], "FROM"))
            {
                if (!Regex.IsMatch(raw, @"\A<TABLE[1-3]>\z"))
                    throw new FormatException($"FROM 다음에 테이블 태그가 필요합니다 (위치 {token.Position}).");
                hasTable = true;
            }
            if (raw.StartsWith('\'') || raw.StartsWith('['))
                RestoreValue(token, i > 0 && Is(tokens[i - 1], "LIKE"), record, schema);
            else if (raw.StartsWith('{') || raw.StartsWith('<') && raw.EndsWith('>'))
                token.Sql = RestoreIdentifier(raw, record);
            else if (char.IsLetter(raw[0]) || raw[0] == '_')
            {
                if (!Keywords.Contains(raw)) throw new FormatException($"지원하지 않는 SQL 키워드입니다 (위치 {token.Position}): {raw}");
            }
            else if (raw == ";" && i != tokens.Count - 1)
                throw new FormatException("한 번에 하나의 SELECT 문장만 복원할 수 있습니다.");
        }
        if (!hasTable) throw new FormatException("FROM 테이블 태그가 필요합니다.");

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!Is(tokens[i], "BETWEEN")) continue;
            if (i + 3 >= tokens.Count || !Is(tokens[i + 2], "AND"))
                throw new FormatException("BETWEEN의 두 경계 태그가 필요합니다.");
            Token first = tokens[i + 1], last = tokens[i + 3];
            if (first.OrderValue == null || last.OrderValue == null || first.ValueColumn != last.ValueColumn)
                throw new FormatException("BETWEEN에는 같은 컬럼의 숫자 또는 날짜 값 태그 두 개가 필요합니다.");
            if (first.OrderValue > last.OrderValue)
                (first.Sql, last.Sql) = (last.Sql, first.Sql);
        }
        var sql = new StringBuilder();
        foreach (var token in tokens) sql.Append(token.Space).Append(token.Sql);
        return sql.ToString();
    }

    private static bool Is(Token token, string word) => token.Raw.Equals(word, StringComparison.OrdinalIgnoreCase);

    private static string RestoreIdentifier(string tag, MappingRecord record)
    {
        string body = tag[1..^1];
        if (body == "*" || body == "COUNT(*)") return body;
        int inner = body.IndexOf('<');
        if (inner > 0)
        {
            int close = body.IndexOf('>', inner);
            return body[..inner] + RestoreIdentifier(body[inner..(close + 1)], record) + body[(close + 1)..];
        }
        var parts = body.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 1 && record.Tables.TryGetValue(body, out var table)) return Identifier(table);
        if (parts.Length == 2 && record.Columns.TryGetValue(parts[0], out var column) && column.Type == parts[1])
            return Identifier(column.Name);
        throw new FormatException($"복원 기록에 일치하는 테이블·컬럼 태그가 없습니다: {tag}");
    }

    private static string Identifier(string name)
    {
        // Identifiers were restricted to ASCII names and checked against the DB schema.
        // Quote non-shop names, including reserved SQLite words, when integrating another schema.
        return name is "items" or "customers" or "orders" or "item_id" or "item_name" or
            "price" or "category" or "stock" or "customer_id" or "name" or "city" or "membership" or
            "order_id" or "quantity" or "status" or "order_date" ? name : "\"" + name + "\"";
    }

    private static void RestoreValue(Token token, bool like, MappingRecord record, IReadOnlyList<TableSchemaInfo> schema)
    {
        bool quoted = token.Raw[0] == '\'';
        string pattern = quoted ? token.Raw[1..^1] : token.Raw;
        bool prefix = pattern.StartsWith('%'), suffix = pattern.EndsWith('%');
        string[] parts = pattern.Trim('%')[1..^1].Split(',', StringSplitOptions.TrimEntries);
        string mark = $"[{parts[0]}, {parts[1]}, {parts[2]}]";
        if (!record.Values.TryGetValue(mark, out var value))
            throw new FormatException($"위치 {token.Position}의 값을 복원할 수 없습니다: {mark}");
        string type = parts[1];
        if ((type == "numeric") == quoted || ((prefix || suffix) && !like) || (like && type != "string"))
            throw new FormatException($"위치 {token.Position}의 따옴표 또는 LIKE 패턴이 유형과 다릅니다: {mark}");
        token.ValueColumn = parts[0];
        switch (type)
        {
            case "numeric":
                MappedColumn? col = record.Columns.GetValueOrDefault(parts[0]);
                string? table = col == null ? null : record.Tables[parts[0].Split('.')[0]];
                bool integer = col != null && SchemaTags.Table(schema, table!).Columns.Any(c =>
                    c.StartsWith(col.Name + " (", StringComparison.OrdinalIgnoreCase) &&
                    c[(col.Name.Length + 2)..].Contains("INT", StringComparison.OrdinalIgnoreCase));
                decimal number = ParseNumber(value, table, col?.Name, integer);
                token.Sql = number.ToString(CultureInfo.InvariantCulture);
                token.OrderValue = number;
                break;
            case "date":
                if (!DateOnly.TryParseExact(value.Trim(), new[] { "yyyy-MM-dd", "yyyy/M/d", "MMMM d, yyyy", "MMM d, yyyy" },
                    CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
                    throw new FormatException($"날짜를 해석할 수 없습니다: {value}");
                token.Sql = "'" + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "'";
                token.OrderValue = date.DayNumber;
                break;
            default:
                string literal = value.Replace("'", "''");
                bool escape = like && literal.IndexOfAny(['!', '%', '_']) >= 0;
                if (escape) literal = literal.Replace("!", "!!").Replace("%", "!%").Replace("_", "!_");
                token.Sql = "'" + (prefix ? "%" : "") + literal + (suffix ? "%" : "") + "'" +
                            (escape ? " ESCAPE '!'" : "");
                break;
        }
    }

    private static decimal ParseNumber(string value, string? table, string? column, bool integer)
    {
        string input = value.Trim();
        if (table == "items" && column == "price")
            input = Regex.Replace(input, @"^\$\s*|\s*(?:dollars?|USD)$", "", RegexOptions.IgnoreCase).Trim();
        else if (table == "items" && column == "stock" || table == "orders" && column == "quantity")
            input = Regex.Replace(input, @"\s*(?:units?|pieces?|items?|개)$", "", RegexOptions.IgnoreCase).Trim();
        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands;
        if (!Regex.IsMatch(input, @"\A[+-]?(?:[0-9]+|[0-9]{1,3}(?:,[0-9]{3})+)(?:\.[0-9]+)?\z") ||
            !decimal.TryParse(input, styles, CultureInfo.InvariantCulture, out var number) ||
            integer && decimal.Truncate(number) != number)
            throw new FormatException($"숫자 또는 단위를 해석할 수 없습니다: {value}");
        return integer ? decimal.Truncate(number) : number;
    }
}
