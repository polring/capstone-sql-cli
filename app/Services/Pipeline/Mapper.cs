using System.Text;
using System.Text.RegularExpressions;
using SqlApp.Models;

namespace SqlApp.Services.Pipeline;

public static partial class Mapper
{
    private static readonly Regex MalformedTag = new(@"[\[\]{}]|<[A-Za-z_]", RegexOptions.CultureInvariant);

    internal static bool IsTaggedSentence(string text) =>
        !string.IsNullOrWhiteSpace(text) && HasValidLiterals(SchemaTags.Marks.Split(text));

    private static bool HasValidLiterals(string[] literals) =>
        literals.Length > 1 && !literals.Any(literal => MalformedTag.IsMatch(literal));

    public static (string GeneralizedQuestion, List<MappingRecord> Candidates) MapCandidates(
        string question, string frontOutput, IReadOnlyList<TableSchemaInfo> schema)
    {
        if (schema.Count == 0) throw new FormatException("매퍼를 실행할 DB 스키마가 없습니다.");
        var literals = SchemaTags.Marks.Split(frontOutput);
        if (!HasValidLiterals(literals))
            throw new FormatException("앞모델 출력에는 DB 스키마 태그가 들어간 자연어 문장이 필요합니다.");
        var latest = FindLatestLiterals(question, literals);
        var marks = SchemaTags.Marks.Matches(frontOutput);
        var record = new MappingRecord();
        var output = new StringBuilder();
        var valueMarks = new HashSet<string>(StringComparer.Ordinal);
        var valueTags = new string?[marks.Count];
        for (int i = 0; i < marks.Count; i++)
        {
            string mark = Generalize(marks[i].Value, record, schema, valueMarks);
            if (mark[0] == '[')
            {
                valueMarks.Add(mark);
                valueTags[i] = mark;
            }
            output.Append(literals[i]);
            output.Append(mark);
        }
        output.Append(literals[^1]);
        if (record.Tables.Count == 0) throw new FormatException("대상 테이블을 식별할 DB 태그가 필요합니다.");
        string generalizedQuestion = output.ToString();
        if (valueMarks.Count == 0) return (generalizedQuestion, [record]);

        var candidates = new List<MappingRecord>();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var tagOrder = valueMarks.ToArray();
        var seenBindings = new HashSet<string>(StringComparer.Ordinal);
        int lastValueTag = Array.FindLastIndex(valueTags, mark => mark != null);
        Visit(0, literals[0].Length);
        if (candidates.Count == 0)
            throw new FormatException("유효한 값 대응을 찾을 수 없습니다. 값은 비어 있을 수 없고 같은 값 태그에는 동일한 원문이 필요합니다.");
        return (generalizedQuestion, candidates);

        void Visit(int tag, int start)
        {
            string? mark = valueTags[tag];
            int following = tag + 1;
            bool betweenIdentifiers = tag < lastValueTag && mark == null && valueTags[following] == null;
            foreach (int end in FollowingLiteralPositions(question, start, literals[following], latest[following],
                                                         following == literals.Length - 1))
            {
                string? previous = null;
                if (mark != null)
                {
                    string value = question[start..end];
                    values.TryGetValue(mark, out previous);
                    if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') ||
                        previous != null && previous != value) continue;
                    values[mark] = value;
                }

                if (tag == lastValueTag)
                {
                    if (seenBindings.Add(SchemaTags.ValueTupleKey(tagOrder, values)))
                    {
                        candidates.Add(new MappingRecord
                        {
                            Tables = new(record.Tables),
                            Columns = new(record.Columns), Values = new(values)
                        });
                    }
                }
                else Visit(following, end + literals[following].Length);
                if (mark != null && previous == null) values.Remove(mark);

                // Shifting a boundary between two identifiers only shortens the following identifier.
                // Its earliest position already permits every subsequent value assignment.
                if (betweenIdentifiers) break;
            }
        }
    }

    private static int[] FindLatestLiterals(string question, string[] literals)
    {
        const string mismatch = "태그 바깥의 문구와 원문이 일치해야 하며, 각 태그는 원문에서 한 글자 이상을 대체해야 합니다.";
        if (!question.StartsWith(literals[0], StringComparison.Ordinal) ||
            !question.EndsWith(literals[^1], StringComparison.Ordinal))
            throw new FormatException(mismatch);

        // A literal at or before its latest position leaves room for the entire suffix.
        var latest = new int[literals.Length];
        latest[^1] = question.Length - literals[^1].Length;
        for (int i = literals.Length - 2; i > 0; i--)
        {
            int next = latest[i + 1];
            int end = next - (next >= 2 && char.IsSurrogatePair(question, next - 2) ? 2 : 1);
            if (end < 0) throw new FormatException(mismatch);
            latest[i] = question.AsSpan(0, end).LastIndexOf(literals[i], StringComparison.Ordinal);
            if (latest[i] < 0) throw new FormatException(mismatch);
        }
        if (literals[0].Length >= latest[1]) throw new FormatException(mismatch);
        return latest;
    }

    private static IEnumerable<int> FollowingLiteralPositions(string question, int start, string literal, int latest, bool isSuffix)
    {
        if (start >= latest) yield break;
        if (isSuffix)
        {
            yield return latest;
            yield break;
        }
        int search = start;
        while (search < latest)
        {
            search += char.IsSurrogatePair(question, search) ? 2 : 1;
            if (search > latest) yield break;
            int position = question.IndexOf(literal, search, latest - search + literal.Length, StringComparison.Ordinal);
            if (position < 0) yield break;
            yield return position;
            search = position;
        }
    }

    private static string Generalize(string mark, MappingRecord record, IReadOnlyList<TableSchemaInfo> schema,
                                     HashSet<string> valueMarks)
    {
        if (mark == "{*}") return mark;
        var parts = mark[1..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts[0] == "AGG_VALUE" && mark.StartsWith('['))
        {
            if (parts.Length != 3 || parts[1] != "numeric" || parts[2] != "#1")
                throw new FormatException($"잘못된 집계 값 태그입니다: {mark}");
            return "[AGG_VALUE, numeric, #1]";
        }

        var names = parts[0].Split('.');
        var actualTable = SchemaTags.Table(schema, names[0]);
        var tableId = record.Tables.FirstOrDefault(p => p.Value == actualTable.TableName).Key;
        if (tableId == null)
        {
            if (record.Tables.Count >= 3) throw new FormatException("한 질문에 최대 3개 테이블을 지원합니다.");
            tableId = $"TABLE{record.Tables.Count + 1}";
            record.Tables.Add(tableId, actualTable.TableName);
        }
        if (names.Length == 1) return $"<{tableId}>";
        string type = parts[1];
        string columnName = SchemaTags.Column(actualTable, names[1], type);
        var existing = record.Columns.Where(p => p.Key.StartsWith(tableId + ".", StringComparison.Ordinal)).ToArray();
        string? columnId = existing.FirstOrDefault(p => p.Value.Name == columnName).Key;
        if (columnId == null)
        {
            if (existing.Length >= 6) throw new FormatException("테이블당 최대 6개 컬럼을 지원합니다.");
            columnId = $"{tableId}.COL{existing.Length + 1}";
            record.Columns.Add(columnId, new MappedColumn(columnName, type));
        }
        else if (record.Columns[columnId].Type != type)
            throw new FormatException($"같은 컬럼에 다른 유형이 있습니다: {mark}");
        if (mark[0] != '[') return $"{mark[0]}{columnId}, {type}{mark[^1]}";

        string valueMark = $"[{columnId}, {type}, {parts[2]}]";
        int count = valueMarks.Count(k => k.StartsWith("[" + columnId + ",", StringComparison.Ordinal));
        if (!valueMarks.Contains(valueMark) && parts[2] != $"#{count + 1}")
            throw new FormatException($"값 번호는 질문 순서대로 #1부터 시작해야 합니다: {mark}");
        return valueMark;
    }
}
