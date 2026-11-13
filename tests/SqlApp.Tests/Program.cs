using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.Services.Pipeline;

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (!File.Exists(Path.Combine(root.FullName, "SqlApp.sln")))
    root = root.Parent ?? throw new DirectoryNotFoundException("SqlApp.sln");
var temp = Directory.CreateDirectory(Path.Combine(root.FullName, "build", "tests", Guid.NewGuid().ToString("N")));
try
{
    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "category.json")));
    using var golden = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "category.expected.json")));
    var example = document.RootElement;
    var expected = golden.RootElement;
    string question = example.GetProperty("input").GetString()!, taggedQuestion = example.GetProperty("output").GetString()!;
    string generalized = expected.GetProperty("generalizedQuestion").GetString()!, taggedSql = expected.GetProperty("taggedSql").GetString()!;
    string single = Directory.CreateDirectory(Path.Combine(temp.FullName, "single")).FullName;
    string dual = Directory.CreateDirectory(Path.Combine(temp.FullName, "dual")).FullName;
    string db = Path.Combine(dual, "shop.db");
    var fixtureModel = Path.Combine(root.FullName, "fixtures", "v2", "base", "model.gguf");
    var singleModel = Path.Combine(single, "custom.gguf");
    File.Copy(fixtureModel, singleModel);
    using (File.Create(Path.Combine(single, "custom.db"))) { }
    using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db, Pooling = false }.ToString()))
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE items(item_name TEXT, category TEXT); INSERT INTO items VALUES ('cup','kitchen');";
        command.ExecuteNonQuery();
    }
    var assets = new List<AssetBundle>();
    var legacy = AssetDiscoveryService.AddFolder(assets, single);
    Require(legacy is { IsDual: false } && legacy.ModelPath == singleModel, "Legacy custom filenames");
    var schemaOnly = AssetDiscoveryService.AddFolder(assets, dual);
    Require(schemaOnly is { IsDual: true } && schemaOnly.DbPath == db && schemaOnly.BackModelPath == "", "Mapper-only schema folder");
    string back = Path.Combine(dual, "back.gguf");
    File.Copy(fixtureModel, back);
    var bundle = AssetDiscoveryService.AddFolder(assets, dual)!;
    Require(bundle.IsDual && bundle.FrontModelPath == "" && bundle.BackModelPath == back, "Back role discovery");
    string front = Path.Combine(dual, "front.gguf");
    File.Copy(fixtureModel, front);
    bundle = AssetDiscoveryService.AddFolder(assets, dual)!;
    Require(bundle.FrontModelPath == front && assets.Count == 2, "Refresh a dual bundle");

    var sqlite = new SqlExecutionService();
    await SqlQueryChecks.VerifyPreparationAsync(sqlite, db);
    var schema = await sqlite.GetDatabaseSchemaAsync(db);
    var calls = new List<PipelineStage>();
    Task<InferenceResult> Predict(PipelineStage stage, string input)
    {
        calls.Add(stage);
        if (stage == PipelineStage.Front && input == question) return Task.FromResult(new InferenceResult(taggedQuestion, "eos", 1.25));
        if (stage == PipelineStage.Back && input == generalized) return Task.FromResult(new InferenceResult(taggedSql, "eos", 3.5));
        throw new InvalidOperationException($"Unexpected input at {stage}: {input}");
    }
    var session = new ConversationSession { Bundle = bundle, BundleDirectory = dual, IsDualMode = true };
    Task<PipelineRun> Run(PipelineStage start, PipelineStage end, string input, string extra = "")
        => session.RunInSessionAsync(new(start, end), input, extra, dual, db, schema, Predict);
    var full = await Run(PipelineStage.Front, PipelineStage.Postprocessor, question);
    Guid fullId = session.Messages.Last().Id;
    Require(full.Error == "" && full.Mapping != null && full.FinalSql == expected.GetProperty("finalSql").GetString(), $"Full run: {full.Error} {full.FinalSql}");
    Require(full.Steps.Select(s => s.ComputeTimeMs).SequenceEqual(new double?[] { 1.25, null, 3.5, null }),
        "Keep native compute time only on model stages");
    Require(full.FrontOutput == taggedQuestion && full.GeneralizedQuestion == generalized &&
        full.Mapping!.Values["[TABLE1.COL2, string, #1]"] == "kitchen", "Restore values from the original question and tagged sentence");
    var rows = await sqlite.ExecuteQueryAsync(db, full.FinalSql);
    Require(rows.ErrorMessage == null && rows.Rows.Single().Single() == "cup", "Read-only SQLite result");
    var mapping = full.SerializeMappings();
    using var singleMapping = JsonDocument.Parse(mapping);
    CandidatePipelineChecks.VerifyEnvelope(singleMapping.RootElement, session.Id, fullId);
    Require(singleMapping.RootElement.GetProperty("Candidates").GetArrayLength() == 1, "Always export a candidate array for one mapping");
    // Exercise restoration directly with the special characters kept in original values.
    var literalMap = Mapper.MapCandidates(question.Replace("kitchen", "chef's_20%", StringComparison.Ordinal),
        taggedQuestion, schema).Candidates.Single();
    string like = taggedSql.Replace("= '[", "LIKE '%[", StringComparison.Ordinal).Replace("]'", "]%'", StringComparison.Ordinal);
    string restoredLike = Postprocessor.Restore(like, literalMap, schema);
    Require(restoredLike.EndsWith("LIKE '%chef''s!_20!%%' ESCAPE '!'"), "Escape original quote and LIKE wildcards");

    var priceSchema = new[] { new TableSchemaInfo { TableName = "items", Columns = ["item_name (TEXT)", "price (REAL)"] } };
    var priceMap = Mapper.MapCandidates("products priced between 20 dollars and 10 dollars",
        "{items.item_name, string} priced between [items.price, numeric, #1] and [items.price, numeric, #2]",
        priceSchema).Candidates.Single();
    string interval = Postprocessor.Restore("SELECT {TABLE1.COL1, string} FROM <TABLE1> WHERE <TABLE1.COL2, numeric> BETWEEN [TABLE1.COL2, numeric, #1] AND [TABLE1.COL2, numeric, #2]", priceMap, priceSchema);
    Require(interval.EndsWith("BETWEEN 10 AND 20"), "Restore numeric units and sort BETWEEN bounds");
    foreach (var start in Enum.GetValues<PipelineStage>())
    foreach (var end in Enum.GetValues<PipelineStage>().Where(s => s >= start))
    {
        calls.Clear();
        string input = start switch
        {
            PipelineStage.Mapper => taggedQuestion,
            PipelineStage.Back => generalized,
            PipelineStage.Postprocessor => taggedSql,
            _ => question
        };
        string extra = start == PipelineStage.Mapper ? question
            : start >= PipelineStage.Back && end == PipelineStage.Postprocessor ? mapping : "";
        var run = await Run(start, end, input, extra);
        var stages = Enum.GetValues<PipelineStage>().Where(s => s >= start && s <= end).ToList();
        Require(run.Error == "" && run.Steps.Select(s => s.Stage).SequenceEqual(stages), $"{start}–{end}: {run.Error}");
        Require(run.Input == input && run.Steps[0].Input == input, "The first stage receives the exact chat input");
        Require(calls.SequenceEqual(stages.Where(s => s is PipelineStage.Front or PipelineStage.Back)), "Unselected model called");
        Require(run.FinalSql == (end == PipelineStage.Postprocessor ? full.FinalSql : ""), "Executable output boundary");
        if (start <= PipelineStage.Mapper && end >= PipelineStage.Mapper)
            Require(run.GeneralizedQuestion == generalized, "Mapper output");
    }
    var tagged = await Run(PipelineStage.Mapper, PipelineStage.Mapper, taggedQuestion, question);
    Guid taggedId = session.Messages.Last().Id;
    Require(tagged.Error == "" && tagged.GeneralizedQuestion == generalized, $"Tagged front input: {tagged.Error}");
    var missingQuestion = await Run(PipelineStage.Mapper, PipelineStage.Mapper, taggedQuestion);
    Require(missingQuestion.Error.Contains("원문 질문") && missingQuestion.Steps.Count == 0,
        "A mapper start needs the original question to recover the source values");
    var reversedInputs = await Run(PipelineStage.Mapper, PipelineStage.Mapper, question, taggedQuestion);
    Require(reversedInputs.Error.Length > 0 && reversedInputs.Steps.Single().Input == question,
        "The chat field supplies the tagged sentence, even if the auxiliary field contains tags");
    const string valueTag = "[items.category, string, #1]";
    var repeated = await Run(PipelineStage.Mapper, PipelineStage.Mapper, $"compare {valueTag} with {valueTag}",
        "compare Hot Pink with Hot Pink");
    Guid repeatedId = session.Messages.Last().Id;
    Require(repeated.Error == "" && repeated.Mapping!.Values.Single().Value == "Hot Pink",
        "Preserve a repeated multiword value with its original case");
    await CandidatePipelineChecks.RunAsync(dual, db, schema, question, taggedQuestion, generalized, taggedSql);
    var identifiers = await Run(PipelineStage.Mapper, PipelineStage.Mapper, "show {items.item_name, string} <items.category, string>",
        "show the item names category");
    Require(identifiers.GeneralizedQuestion == "show {TABLE1.COL1, string} <TABLE1.COL2, string>" &&
            identifiers.MappingCandidates.Count == 1 && identifiers.MappingCandidates[0].Values.Count == 0,
            "Ambiguous identifier spans with identical value bindings produce one mapping");
    var mismatch = await Run(PipelineStage.Mapper, PipelineStage.Mapper, taggedQuestion.Replace("things", "products"), question);
    Require(mismatch.Error != "", "Reject edited untagged text");
    var malformed = await Run(PipelineStage.Mapper, PipelineStage.Mapper, taggedQuestion.Replace("#1]", "#1"), question);
    Require(malformed.Error != "", "Reject malformed tag syntax");
    const string spanObject = """{"select":[{"mark":"{items.item_name, string}","start":13,"end":18}]}""";
    var indexed = await Run(PipelineStage.Mapper, PipelineStage.Mapper, spanObject, question);
    Require(indexed.Error != "", "Reject obsolete index labels passed to the mapper");
    var wrapped = await Run(PipelineStage.Mapper, PipelineStage.Mapper, example.GetRawText(), question);
    Require(wrapped.Error != "", "The mapper expects the tagged text itself");
    var badFront = await session.RunInSessionAsync(new(PipelineStage.Front, PipelineStage.Front), question, "", dual, db, [],
        (_, _) => Task.FromResult(new InferenceResult(spanObject, "eos", 0.25)));
    Require(badFront.Error != "" && badFront.Steps.Single().Output == spanObject, "Reject a model emitting index labels on a front-only run");
    calls.Clear();
    var missing = await Run(PipelineStage.Back, PipelineStage.Postprocessor, generalized);
    Require(missing.Error != "" && calls.Count == 0, "Preflight must detect missing mapping before inference");
    var backOnly = await Run(PipelineStage.Back, PipelineStage.Back, generalized);
    Require(backOnly.Error == "" && backOnly.Mappings == null && backOnly.TaggedSql == taggedSql,
        "A back-only run takes a generalized question and returns tagged SQL");
    var postOnly = await Run(PipelineStage.Postprocessor, PipelineStage.Postprocessor, taggedSql, mapping);
    Require(postOnly.Error == "" && postOnly.FinalSql == full.FinalSql && postOnly.Mappings?.MessageId == fullId,
        "A postprocessor start takes tagged SQL and the mapping supplied for the original question");
    calls.Clear();
    var reverse = await Run(PipelineStage.Back, PipelineStage.Front, generalized);
    Require(reverse.Error != "" && calls.Count == 0, "Reverse range");
    var replayRange = new StageRange(PipelineStage.Back, PipelineStage.Postprocessor);
    async Task RejectReplay(ConversationSession context, string json, string folder, string database, string reason)
    {
        var result = await context.RunInSessionAsync(replayRange, generalized, json, folder, database, schema, Predict);
        Require(result.Error.Length > 0 && calls.Count == 0, reason);
    }
    await RejectReplay(new ConversationSession(), mapping, dual, db, "A different conversation cannot reuse the same folder's mapping");
    await RejectReplay(session, mapping, single, db, "A changed bundle cannot reuse a mapping from the original bundle");
    await RejectReplay(session, mapping, dual, Path.Combine(dual, "other.db"), "A changed database cannot reuse the original mapping");
    var wrongMessage = JsonNode.Parse(mapping)!;
    wrongMessage["MessageId"] = Guid.NewGuid();
    await RejectReplay(session, wrongMessage.ToJsonString(), dual, db, "Reject a mapping whose source message is missing");
    wrongMessage["MessageId"] = repeatedId;
    await RejectReplay(session, wrongMessage.ToJsonString(), dual, db, "Reject candidates attached to another query's message");
    wrongMessage["MessageId"] = session.Messages.Single(m => m.PipelineRun == backOnly).Id;
    await RejectReplay(session, wrongMessage.ToJsonString(), dual, db, "A back-only reply is not the mapper's originating reply");
    var incomplete = await session.RunInSessionAsync(new(PipelineStage.Front, PipelineStage.Back), question, "", dual, db, schema,
        (_, _) => Task.FromResult(new InferenceResult(taggedQuestion, "max_new_tokens", 0.75)));
    Require(incomplete.Error != "" && incomplete.Steps.Count == 1 && incomplete.TaggedSql == "", "Stop at incomplete generation");
    Require(incomplete.Steps.Single().ComputeTimeMs == 0.75, "Keep compute time for truncated generation");

    var history = Directory.CreateDirectory(Path.Combine(temp.FullName, "history")).FullName;
    var store = new SessionStorageService(history);
    var oldFormat = new[] { new { Id = "old", ModelPath = singleModel, DbPath = Path.Combine(single, "custom.db"),
        Messages = new[] { new { Sender = 1, Sql = "SELECT 1", InferenceTimeMs = 3 },
            new { Sender = 1, Sql = "SELECT", InferenceTimeMs = 2 } } } };
    await File.WriteAllTextAsync(Path.Combine(history, "sessions.json"), JsonSerializer.Serialize(oldFormat));
    var previous = (await store.LoadSessionsAsync(assets)).Single();
    Require(!previous.IsDualMode && previous.Messages[0].CanExecuteSql && previous.Bundle?.ModelPath == singleModel,
        "Legacy session restores as single mode with its original DB");
    Require(previous.Messages[1] is { HasSqlValidationError: true, CanExecuteSql: false },
        "Legacy invalid SQL remains visible but cannot run");
    Require(previous.Messages[0] is { InferenceTimeMs: 3, ComputeTimeMs: null },
        "Existing end-to-end timings stay separate from native compute timings");
    var newSession = new ConversationSession { Id = session.Id, Bundle = bundle, BundleDirectory = dual, IsDualMode = true, SelectedRange = tagged.Range };
    newSession.Messages.Add(new ChatMessage { Id = taggedId, Sender = MessageSender.Assistant, PipelineRun = tagged, DbPath = db });
    newSession.Messages.Add(new ChatMessage { Id = fullId, Sender = MessageSender.Assistant, PipelineRun = full, Sql = full.FinalSql, DbPath = db, ComputeTimeMs = 4.75 });
    await store.SaveSessionsAsync([previous, newSession]);
    var savedPath = Path.Combine(history, "sessions.json");
    var legacySelection = JsonNode.Parse(await File.ReadAllTextAsync(savedPath))!.AsArray();
    legacySelection[1]!["SelectedRange"] = JsonSerializer.SerializeToNode(tagged.Range);
    await File.WriteAllTextAsync(savedPath, legacySelection.ToJsonString());
    var saved = (await store.LoadSessionsAsync(assets)).Single(s => s.IsDualMode);
    Require(saved.SelectedRange == new StageRange(PipelineStage.Front, PipelineStage.Mapper) &&
        saved.Bundle?.DirectoryPath == dual && saved.Messages.Count == 2, "Snap a legacy mapper-only selection to front through mapper");
    Require(!saved.Messages[0].IsWaitingResponse && !saved.Messages[0].CanExecuteSql
        && saved.Messages[0].PipelineRun?.Mappings?.MessageId == saved.Messages[0].Id && saved.Messages[0].Id == taggedId,
        "Intermediate display and mapper response identity on reload");
    var savedMapper = saved.Messages[0].PipelineRun!;
    Require(savedMapper.Range == tagged.Range && savedMapper.Input == taggedQuestion && savedMapper.Steps.Single().Input == taggedQuestion &&
        savedMapper.MapperSourceQuestion == question, "Keep the original executed range and inputs on historical mapper responses");
    Require(saved.Messages[1].CanExecuteSql && saved.Messages[1].DbPath == db, "SQL target DB on reload");
    Require(saved.Messages[1].ComputeTimeMs == 4.75 && saved.Messages[1].PipelineRun!.Steps
        .Select(s => s.ComputeTimeMs).SequenceEqual(full.Steps.Select(s => s.ComputeTimeMs)), "Persist fractional compute times");
    var afterRestart = await saved.RunInSessionAsync(new(PipelineStage.Postprocessor, PipelineStage.Postprocessor),
        taggedSql, mapping, dual, db, schema, Predict);
    Require(afterRestart.Error == "" && afterRestart.FinalSql == full.FinalSql &&
        afterRestart.Mappings?.ConversationId == saved.Id && afterRestart.Mappings.MessageId == fullId,
        "An exported record resolves to the same source message after reloading the conversation");

    var nativeName = OperatingSystem.IsWindows() ? "sql_engine.dll" : OperatingSystem.IsMacOS() ? "libsql_engine.dylib" : "libsql_engine.so";
    var nativePath = Path.Combine(root.FullName, "app", "runtimes", RuntimeInformation.RuntimeIdentifier, "native", nativeName);
    NativeLibrary.SetDllImportResolver(typeof(InferenceService).Assembly, (name, _, _) =>
        name == "sql_engine" ? NativeLibrary.Load(nativePath) : IntPtr.Zero);
    await SqlQueryChecks.VerifyChatAsync(temp.FullName, db);
    await ViewModelChecks.RunAsync(temp.FullName, assets, example, expected);
    ContractV2Checks.Verify(Path.Combine(root.FullName, "fixtures", "v2"), temp.FullName);
    using var inference = new InferenceService();
    Require(inference.GetStageUnavailableReason(PipelineStage.Front, "") != null, "Missing model availability");
    var incompatible = Path.Combine(temp.FullName, "other-version.gguf");
    byte[] bytes = File.ReadAllBytes(fixtureModel);
    var key = "capstone_sql.contract_version"u8;
    int valueOffset = bytes.AsSpan().IndexOf(key) + key.Length + sizeof(uint);
    Require(valueOffset > key.Length + sizeof(uint), "Fixture missing contract_version metadata");
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(valueOffset), 99);
    File.WriteAllBytes(incompatible, bytes);
    Require(inference.GetStageUnavailableReason(PipelineStage.Back, incompatible)?.Contains("v99") == true, "Explain unsupported model contract");
    // The base fixture's independent full-prefix predictions for these literal prompts differ.
    foreach (var (input, expectedText) in new[] { ("Show <TABLE1.COL2>", "|||"), ("show <table1.col2>", "|::") })
    {
        var singleResult = await inference.RunQueryAsync(singleModel, input, 3);
        var frontResult = await inference.RunStageAsync(PipelineStage.Front, front, input, 3);
        var backResult = await inference.RunStageAsync(PipelineStage.Back, back, input, 3);
        foreach (var result in new[] { singleResult, frontResult, backResult })
            Require(result.Text == expectedText && result.StopReason == "max_new_tokens" && result.ComputeTimeMs > 0,
                $"Single, front and back must preserve case for {input}: expected {expectedText}, got {result.Text}");
    }
    await NativePipelineSmoke.VerifyAsync(temp.FullName, db, schema, example, expected);
    Console.WriteLine("Verified five slider selections, ten internal pipeline ranges, distinct value mappings, one back-model call for multiple SQLs, candidate validation and execution, session storage, native inference and truncation notices.");
}
finally
{
    SqliteConnection.ClearAllPools();
    temp.Delete(true);
}
