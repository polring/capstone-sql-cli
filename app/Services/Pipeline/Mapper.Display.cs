using System.Text;
using SqlApp.Models;

namespace SqlApp.Services.Pipeline;

public static partial class Mapper
{
    internal static string FormatMappings(PipelineRun run, PipelineStepResult? mapperStep)
    {
        if (run.MappingCandidates.Count == 0) return "";
        string question = run.MapperSourceQuestion ?? run.Input;
        string tagged = mapperStep?.Input is { Length: > 0 } input ? input : run.FrontOutput;
        string generalized = mapperStep?.Output is { Length: > 0 } output ? output : run.GeneralizedQuestion;
        var literals = SchemaTags.Marks.Split(tagged);
        var tags = SchemaTags.Marks.Matches(tagged).Select(m => m.Value).ToArray();
        var generalizedTags = SchemaTags.Marks.Matches(generalized).Select(m => m.Value).ToArray();
        var sections = new List<string>();
        foreach (var record in run.MappingCandidates)
        {
            string? pairs = FormatSourcePairs(question, literals, tags, generalizedTags, record);
            // Some older sessions retain the restoration record without the tagged sentences.
            pairs ??= string.Join("\n", record.Tables.Select(p => $"{p.Value} = <{p.Key}>")
                .Concat(record.Columns.Select(p => $"{p.Value.Name} = <{p.Key}, {p.Value.Type}>"))
                .Concat(record.Values.Select(p => $"{p.Value} = {p.Key}")));
            sections.Add(run.MappingCandidates.Count == 1 ? pairs : $"후보 {sections.Count + 1}\n{pairs}");
        }
        return string.Join("\n\n", sections);
    }

    private static string? FormatSourcePairs(string question, string[] literals, string[] tags,
        string[] generalizedTags, MappingRecord record)
    {
        if (tags.Length == 0 || tags.Length != generalizedTags.Length) return null;
        var sources = new string[tags.Length];
        var identifierIndices = new List<int>();
        var anchors = new List<string>();
        var anchor = new StringBuilder(literals[0]);
        for (int i = 0; i < tags.Length; i++)
        {
            if (tags[i][0] == '[')
            {
                // This candidate fixes each value span, even when another candidate splits it differently.
                if (!record.Values.TryGetValue(generalizedTags[i], out var value)) return null;
                sources[i] = value;
                anchor.Append(value);
            }
            else
            {
                identifierIndices.Add(i);
                anchors.Add(anchor.ToString());
                anchor.Clear();
            }
            anchor.Append(literals[i + 1]);
        }
        anchors.Add(anchor.ToString());

        if (identifierIndices.Count == 0)
        {
            if (!string.Equals(question, anchors[0], StringComparison.Ordinal)) return null;
        }
        else
        {
            int[] latest;
            try { latest = FindLatestLiterals(question, anchors.ToArray()); }
            catch (FormatException) { return null; }
            int start = anchors[0].Length;
            for (int i = 0; i < identifierIndices.Count; i++)
            {
                int next = i + 1;
                int end = FollowingLiteralPositions(question, start, anchors[next], latest[next], next == identifierIndices.Count)
                    .FirstOrDefault(-1);
                if (end < 0) return null;
                sources[identifierIndices[i]] = question[start..end];
                start = end + anchors[next].Length;
            }
        }
        return string.Join("\n", tags.Select((tag, i) => $"{sources[i]} = {tag}").Distinct(StringComparer.Ordinal));
    }
}
