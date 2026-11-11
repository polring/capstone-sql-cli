using System.Text.Json;
using System.Text.Json.Nodes;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.Services.Pipeline;

internal static class CandidatePipelineChecks
{
    internal static async Task<PipelineRun> RunInSessionAsync(this ConversationSession session, StageRange range,
        string input, string auxiliaryInput, string directory, string db, IReadOnlyList<TableSchemaInfo> schema,
        Func<PipelineStage, string, Task<InferenceResult>> predict)
    {
        var message = new ChatMessage { Sender = MessageSender.Assistant, DbPath = db };
        var run = await PipelineRunner.RunAsync(range, input, auxiliaryInput, directory, db, schema, predict,
            session.Id, message.Id, id => session.Messages.SingleOrDefault(m => m.Id == id)?.PipelineRun);
        message.PipelineRun = run;
        session.Messages.Add(message);
        return run;
    }

    internal static async Task RunAsync(string directory, string db, IReadOnlyList<TableSchemaInfo> schema,
        string question, string taggedQuestion, string generalized, string taggedSql)
    {
        var session = new ConversationSession();
        const string valueTag = "[items.category, string, #1]";
        const string category = "[TABLE1.COL2, string, #1]";
        List<MappingRecord> Map(string source, string tags) =>
            Mapper.MapCandidates(source, tags, schema).Candidates;

        const string secondCategory = "[TABLE1.COL2, string, #2]";
        const string pairSql = "SELECT {TABLE1.COL1, string} FROM <TABLE1> WHERE <TABLE1.COL2, string> IN " +
            "('[TABLE1.COL2, string, #1]', '[TABLE1.COL2, string, #2]')";
        var pairs = await session.RunInSessionAsync(new(PipelineStage.Mapper, PipelineStage.Postprocessor),
            $"show {{items.item_name, string}} in {valueTag} and [items.category, string, #2]",
            "show names in home and kitchen and garden", directory, db, schema,
            (_, _) => Task.FromResult(new InferenceResult(pairSql, "eos", 1)));
        var bindings = pairs.MappingCandidates.Select(m => (m.Values[category], m.Values[secondCategory])).ToList();
        Require(bindings.Count == 2 && bindings.Contains(("home", "kitchen and garden")) &&
            bindings.Contains(("home and kitchen", "garden")), "Keep paired values from each complete sentence alignment");
        Require(pairs.FinalSqlCandidates.Count == 2 && pairs.FinalSqlCandidates.All(candidate =>
        {
            var (first, second) = bindings[candidate.MappingIndices.Single()];
            return candidate.Sql == $"SELECT item_name FROM items WHERE category IN ('{first}', '{second}')";
        }), "Each SQL uses both values from the same mapping");
        var imported = JsonNode.Parse(pairs.SerializeMappings())!;
        var candidates = imported["Candidates"]!.AsArray();
        var duplicate = candidates[0]!.DeepClone();
        var reversedValues = duplicate["Values"]!.AsObject().Reverse().Select(entry =>
            KeyValuePair.Create(entry.Key, entry.Value?.DeepClone()));
        duplicate["Values"] = new JsonObject(reversedValues);
        candidates.Insert(1, duplicate);
        var deduplicated = await session.RunInSessionAsync(new(PipelineStage.Postprocessor, PipelineStage.Postprocessor),
            pairSql, imported.ToJsonString(), directory, db, schema,
            (_, _) => throw new InvalidOperationException("A postprocessor replay must not call a model"));
        Require(deduplicated.Error == "" && deduplicated.MappingCandidates.Count == 2 &&
            deduplicated.FinalSqlCandidates.All(candidate => candidate.MappingIndices.Count == 1) &&
            deduplicated.FinalSqlCandidates.Select(candidate => candidate.Sql).SequenceEqual(pairs.FinalSqlCandidates.Select(candidate => candidate.Sql)),
            "Replaying identical value assignments with a different JSON property order keeps a single mapping");
        var fixedFirst = Map("compare fixed with red and blue and green",
            $"compare {valueTag} with [items.category, string, #2] and <items.category, string>");
        Require(fixedFirst.Count == 2 && fixedFirst.All(m => m.Values["[TABLE1.COL1, string, #1]"] == "fixed") &&
            fixedFirst.Select(m => m.Values["[TABLE1.COL1, string, #2]"]).Order(StringComparer.Ordinal)
                .SequenceEqual(["red", "red and blue"]), "Distinct second values survive even when the first value is shared");
        var repeated = Map("red blue red blue red", $"{valueTag} <items.category, string> {valueTag}");
        Require(repeated.Count == 1 && repeated[0].Values.Single().Value == "red",
            "A repeated tag must recover the same original value at both positions");

        var adjacent = Map("show 가😀나", $"show {{items.item_name, string}}{valueTag}");
        var suffixes = adjacent.Select(m => m.Values[category]).ToList();
        Require(suffixes.Count == 2 && suffixes.Contains("😀나") && suffixes.Contains("나"),
            "An adjacent tag may split at each Unicode scalar boundary");
        var many = Map("show " + new string('x', 130), $"show {{items.item_name, string}}{valueTag}");
        Require(many.Select(m => m.Values[category].Length).Order().SequenceEqual(Enumerable.Range(1, 129)),
            "Retain alternatives even when there are more than one hundred boundaries");

        const string twoOccurrences = "show x a x a x";
        var duplicateValue = await session.RunInSessionAsync(new(PipelineStage.Mapper, PipelineStage.Postprocessor),
            $"show <items> {valueTag} <items>", twoOccurrences, directory, db, schema,
            (_, _) => Task.FromResult(new InferenceResult(
                "SELECT {TABLE1.COL1, string} FROM <TABLE1> WHERE <TABLE1.COL1, string> = '[TABLE1.COL1, string, #1]'", "eos", 1)));
        var sameSql = duplicateValue.FinalSqlCandidates.Single(c => c.Sql.EndsWith("= 'a'"));
        Require(duplicateValue.MappingCandidates.Count == 5 && duplicateValue.FinalSqlCandidates.Count == 5 &&
            duplicateValue.MappingCandidates[sameSql.MappingIndices.Single()].Values.Single().Value == "a" &&
            duplicateValue.MappingCandidates.Select(m => m.Values.Single().Value).Distinct(StringComparer.Ordinal).Count() == 5,
            "Repeated positions for the same value produce just one mapping and one SQL");
        var withCase = Map("show x a x A x", $"show <items> {valueTag} <items>")
            .Select(mapping => mapping.Values.Single().Value).ToHashSet(StringComparer.Ordinal);
        Require(withCase.Contains("a") && withCase.Contains("A"), "Preserve case when comparing condition values");

        string ambiguous = question.Replace("kitchen category", "home kitchen category", StringComparison.Ordinal);
        var calls = new List<(PipelineStage Stage, string Input)>();
        Task<InferenceResult> Predict(PipelineStage stage, string input)
        {
            calls.Add((stage, input));
            return Task.FromResult(new InferenceResult(stage == PipelineStage.Front ? taggedQuestion : taggedSql, "eos", 1));
        }

        var run = await session.RunInSessionAsync(new(PipelineStage.Front, PipelineStage.Postprocessor),
            ambiguous, "", directory, db, schema, Predict);
        Guid origin = session.Messages.Last().Id;
        Require(run.Error == "" && calls.SequenceEqual([(PipelineStage.Front, ambiguous), (PipelineStage.Back, generalized)]),
            $"Call each model exactly once with the common generalized question: {run.Error}");
        Require(run.MappingCandidates.Count == 2 && run.FinalSqlCandidates.Count == 2, "Retain both home kitchen interpretations");
        var expected = new HashSet<string>(["home", "home kitchen"]);
        foreach (var sql in run.FinalSqlCandidates)
        {
            int index = sql.MappingIndices.Single();
            var record = run.MappingCandidates[index];
            string value = record.Values[category];
            Require(expected.Remove(value) && run.GeneralizedQuestion == generalized && sql.Error == "" &&
                sql.Sql == $"SELECT item_name FROM items WHERE category = '{value}'", "Restore each SQL from its own complete mapping");
        }

        calls.Clear();
        string mappings = run.SerializeMappings();
        using var exported = JsonDocument.Parse(mappings);
        VerifyEnvelope(exported.RootElement, session.Id, origin);
        var restored = await session.RunInSessionAsync(new(PipelineStage.Postprocessor, PipelineStage.Postprocessor),
            taggedSql, mappings, directory, db, schema, Predict);
        Require(restored.Error == "" && calls.Count == 0 && restored.FinalSqlCandidates.Select(c => c.Sql)
            .SequenceEqual(run.FinalSqlCandidates.Select(c => c.Sql)), "Replay restores literal values without running either model");
        using var replayed = JsonDocument.Parse(restored.SerializeMappings());
        VerifyEnvelope(replayed.RootElement, session.Id, origin);
        var replay = await session.RunInSessionAsync(new(PipelineStage.Postprocessor, PipelineStage.Postprocessor),
            "SELECT {TABLE1.COL1, string} FROM <TABLE1>", mappings, directory, db, schema, Predict);
        var merged = replay.FinalSqlCandidates.Single();
        Require(replay.Error == "" && replay.MappingCandidates.Count == 2 && merged.Sql == "SELECT item_name FROM items" &&
            merged.MappingIndices.Order().SequenceEqual([0, 1]) && calls.Count == 0,
            "A postprocessor replay merges identical final SQL while preserving both source mappings");

        var foreign = JsonNode.Parse(mappings)!;
        foreign["Candidates"]![1]!["Values"]![category] = "garden";
        var rejected = await session.RunInSessionAsync(new(PipelineStage.Back, PipelineStage.Postprocessor),
            generalized, foreign.ToJsonString(), directory, db, schema, Predict);
        Require(rejected.Error.Length > 0 && calls.Count == 0, "Reject a value from a different mapping before inference");
    }

    internal static void VerifyEnvelope(JsonElement envelope, string conversationId, Guid messageId)
    {
        Require(envelope.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["ConversationId", "MessageId", "Candidates"]),
            "The mapping envelope contains only the owning conversation, originating message and candidates");
        Require(envelope.GetProperty("ConversationId").GetString() == conversationId &&
            envelope.GetProperty("MessageId").GetGuid() == messageId, "Mappings refer to the originating assistant message");
        var candidates = envelope.GetProperty("Candidates");
        Require(candidates.GetArrayLength() > 0, "Expected at least one serialized mapping");
        foreach (var record in candidates.EnumerateArray())
        {
            Require(record.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(["Tables", "Columns", "Values"]),
                "Store only table, column and literal value bindings per candidate");
            var values = record.GetProperty("Values");
            Require(values.EnumerateObject().All(binding => binding.Value.ValueKind == JsonValueKind.String),
                "Serialize condition tags with literal strings");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
