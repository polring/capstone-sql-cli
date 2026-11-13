using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.ViewModels;

internal static class JsonBatchChecks
{
    internal static async Task RunAsync(string tempRoot, List<AssetBundle> assets)
    {
        string directory = Directory.CreateDirectory(Path.Combine(tempRoot, "json-batch")).FullName;
        var history = new SessionStorageService(Path.Combine(directory, "history"));
        var single = assets.Single(b => !b.IsDual);
        var dual = assets.Single(b => b.IsDual);
        using var inference = new InferenceService();
        var calls = new ConcurrentBag<string>();
        const int tokens = 39;
        const string question = "count items.";
        const string tagged = "count <items>.";
        const string generalized = "count <TABLE1>.";
        const string taggedSql = "SELECT {COUNT(*)} FROM <TABLE1>";
        const string sql = "SELECT COUNT(*) FROM items";
        Task<InferenceResult> PredictSingle(string path, string input, int limit)
        {
            Require(path == single.ModelPath && limit == tokens, "Single imports use the selected model and token limit");
            calls.Add(input);
            if (input == "fail") throw new InvalidOperationException("inference failed");
            return Task.FromResult(new InferenceResult("SELECT 1", "eos", 1));
        }
        Task<InferenceResult> PredictStage(PipelineStage stage, string path, string input, int limit)
        {
            Require(limit == tokens, "Dual imports use the session token limit");
            calls.Add(input);
            if (stage == PipelineStage.Front && path == dual.FrontModelPath && input == question)
                return Task.FromResult(new InferenceResult(tagged, "eos", 1));
            if (stage == PipelineStage.Back && path == dual.BackModelPath && input == generalized)
                return Task.FromResult(new InferenceResult(taggedSql, "eos", 1));
            throw new Exception($"Unexpected stage, path or input for {stage}: {path}, {input}");
        }

        var vm = new MainViewModel(assets.ToList(), history, inference, PredictStage, PredictSingle);
        await vm.Initialization;
        vm.SelectBundle(single);
        var session = vm.SelectedSession!;
        session.MaxTokens = tokens;
        const string source = """
            [
              {"id":17,"input":"match","expected":"SELECT 1","output":"legacy","extra":{"team":["하나",2]}},
              {"id":"018","input":"case","expected":"select 1"},
              {"id":19,"input":"space","expected":"SELECT 1 "}
            ]
            """;
        string path = Path.Combine(directory, "questions.v2.json");
        await File.WriteAllTextAsync(path, source);
        string resultPath = Path.Combine(directory, "questions.v2_eval.json");
        vm.AttachJsonFile(path);
        vm.CreateNewSession();
        Require(!vm.HasJsonAttachment, "A new conversation has no pending file");
        vm.SelectSession(session);
        Require(vm.HasJsonAttachment && vm.PendingJsonFileName == Path.GetFileName(path),
            "Returning to the original conversation restores its unsent attachment");
        vm.ClearJsonAttachment();

        vm.InputText = "unsent draft";
        vm.AttachJsonFile(path);
        vm.UsePromptSuggestion("suggested text");
        vm.ClearJsonAttachment();
        Require(!vm.HasJsonAttachment && vm.InputText == "unsent draft",
            "Removing the attachment restores the draft without applying suggestions");
        vm.AttachJsonFile(path);
        Require(!vm.IsGenerating && calls.IsEmpty && session.Messages.Count == 0 && !File.Exists(resultPath),
            "Selecting a JSON file alone does not run or post anything");
        await vm.SendMessageCommand.ExecuteAsync(null);
        Require(!vm.HasJsonAttachment && vm.InputText == "unsent draft",
            "Sending consumes the attachment and keeps the hidden prompt out of the batch");
        var evaluated = JsonNode.Parse(await File.ReadAllTextAsync(resultPath))!.AsArray();
        var originals = JsonNode.Parse(source)!.AsArray();
        Require(await File.ReadAllTextAsync(path) == source && calls.Order().SequenceEqual(["case", "match", "space"]),
            "The source stays intact and every row runs once");
        Require(evaluated.Count == originals.Count, "Evaluation retains all records");
        for (int i = 0; i < evaluated.Count; i++)
        {
            var row = evaluated[i]!.AsObject();
            Require(row["matched"]!.GetValue<bool>() == (i == 0), "Matching is case- and whitespace-sensitive");
            Require(row["inferred"]!.GetValue<string>() == "SELECT 1", "Evaluation saves the actual prediction even on a mismatch");
            row.Remove("inferred");
            row.Remove("matched");
            Require(JsonNode.DeepEquals(row, originals[i]), "Evaluation preserves all original fields, including legacy output");
        }
        var summary = session.Messages.Single(m => m.IsAssistant);
        Require(summary.Text.Contains("3개 중 1개") && summary.Text.Contains(resultPath) &&
            !summary.HasSqlCandidates && !summary.IsWaitingResponse,
            "A batch produces one completed chat summary with match count and saved path");

        path = Path.Combine(directory, "inputs.json");
        await File.WriteAllTextAsync(path, """[{"input":"fail"},{"input":"predict","id":100,"output":{"group":"test"}}]""");
        vm.AttachJsonFile(path);
        await vm.SendMessageAsync();
        var inferred = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "inputs_infer.json")))!.AsArray();
        Require(JsonNode.DeepEquals(inferred, JsonNode.Parse("""
            [{"input":"fail","inferred":null},{"input":"predict","id":100,"output":{"group":"test"},"inferred":"SELECT 1"}]
            """)) && session.Messages.Last().Text.Contains("2개 중 1개"),
            "Failure stays on its row, later outputs are saved and the summary counts successful rows");

        vm.SetDualMode(true);
        vm.SelectBundle(dual);
        foreach (var (start, end, input, expected) in new[]
        {
            (PipelineStage.Front, PipelineStage.Front, question, tagged),
            (PipelineStage.Front, PipelineStage.Mapper, question, generalized),
            (PipelineStage.Front, PipelineStage.Back, question, taggedSql),
            (PipelineStage.Back, PipelineStage.Back, generalized, taggedSql),
            (PipelineStage.Front, PipelineStage.Postprocessor, question, sql)
        })
        {
            vm.SelectRange(new(start, end));
            bool evaluate = end == PipelineStage.Postprocessor;
            string name = $"{start}-{end}";
            var row = new JsonObject { ["id"] = name, ["input"] = input };
            if (evaluate) row.Add("expected", expected);
            path = Path.Combine(directory, name + ".json");
            await File.WriteAllTextAsync(path, row.ToJsonString());
            int previousReplies = session.Messages.Count(m => m.IsAssistant);
            string suffix = evaluate ? "_eval.json" : "_infer.json";
            resultPath = Path.Combine(directory, name + suffix);
            vm.AttachJsonFile(path);
            Require(vm.HasJsonAttachment && !vm.IsJsonBatchRunning && !File.Exists(resultPath) &&
                session.Messages.Count(m => m.IsAssistant) == previousReplies, "Dual mode also waits for Send");
            await vm.SendMessageCommand.ExecuteAsync(null);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(resultPath))!.AsObject();
            Require(saved["id"]!.GetValue<string>() == name && saved["inferred"]!.GetValue<string>() == expected &&
                (!evaluate || (saved["expected"]!.GetValue<string>() == expected && saved["matched"]!.GetValue<bool>())),
                $"Object input keeps its shape and uses the output of {start} to {end}");
            Require(session.Messages.Count(m => m.IsAssistant) == previousReplies + 1 &&
                session.Messages.Last().Text.Contains(resultPath), "Dual and single imports both post one result summary");
        }
        vm.JsonBatchWorkerCount = 1;
        vm.CreateNewSession();
        var otherSession = vm.SelectedSession!;
        Require(vm.JsonBatchWorkerCount == Math.Min(4, Environment.ProcessorCount),
            "A new conversation starts with the default JSON worker count");
        vm.JsonBatchWorkerCount = Math.Min(5, Environment.ProcessorCount);
        vm.SelectSession(session);
        Require(vm.JsonBatchWorkerCount == 1, "Switching conversations restores the selected worker count");
        await history.SaveSessionsAsync(vm.Sessions);
        var savedSessions = await history.LoadSessionsAsync(assets);
        var restored = savedSessions.Single(s => s.Id == session.Id);
        Require(restored.JsonBatchWorkerCount == 1 &&
            savedSessions.Single(s => s.Id == otherSession.Id).JsonBatchWorkerCount == Math.Min(5, Environment.ProcessorCount),
            "Worker counts survive reopening independently for each conversation");
        Require(restored.Messages.Any(m => m.Id == summary.Id && m.Text == summary.Text && !m.IsWaitingResponse),
            "Reloading the conversation preserves completed batch summaries");
        await VerifyCandidatesAndInvalidInputAsync(directory);
        foreach (bool dualMode in new[] { false, true })
        {
            await VerifyWorkerSelectionAsync(directory, assets, dualMode);
            if (Environment.ProcessorCount >= 2)
                await VerifyBatchStopAsync(directory, assets, dualMode);
        }
        await JsonBatchCancellationChecks.RunAsync(directory);
    }

    private static async Task VerifyWorkerSelectionAsync(string directory, List<AssetBundle> assets, bool dual)
    {
        string name = dual ? "workers-dual" : "workers-single";
        var history = new SessionStorageService(Path.Combine(directory, name));
        using var inference = new InferenceService();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int expected = 0;
        int started = 0;
        async Task<InferenceResult> Predict(string input)
        {
            if (Interlocked.Increment(ref started) == expected) release.SetResult();
            await release.Task;
            return new InferenceResult(input, "eos", 1);
        }
        var vm = new MainViewModel(assets.ToList(), history, inference,
            (_, _, input, _) => Predict(input), (_, input, _) => Predict(input));
        await vm.Initialization;
        vm.SetDualMode(dual);
        vm.SelectBundle(assets.Single(b => b.IsDual == dual));
        if (dual) vm.SelectRange(new(PipelineStage.Front, PipelineStage.Front));
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.StatusMessage) && vm.StatusMessage.Contains(" · 1/"))
                vm.CancelJsonBatch();
        };

        int high = Math.Min(5, Environment.ProcessorCount);
        foreach (var (workers, rows) in new[] { (1, 7), (high, 7), (high, 2) })
        {
            vm.JsonBatchWorkerCount = workers;
            expected = Math.Min(workers, rows);
            started = 0;
            release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new JsonArray();
            for (int n = 1; n <= rows; n++) source.Add(new JsonObject { ["input"] = $"question {n}" });
            string path = Path.Combine(directory, name + ".json");
            await File.WriteAllTextAsync(path, source.ToJsonString());
            // No prediction can finish until the entire first group has started.
            vm.AttachJsonFile(path);
            await vm.SendMessageAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, name + "_infer.json")))!.AsArray();
            Require(started == expected && saved.Count == expected, $"Selecting {workers} workers for {rows} questions");
        }
    }

    private static async Task VerifyBatchStopAsync(string directory, List<AssetBundle> assets, bool dual)
    {
        string name = dual ? "stop-dual" : "stop-single";
        var history = new SessionStorageService(Path.Combine(directory, name));
        using var inference = new InferenceService();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new int[4];
        static string Sql(int n) => $"SELECT COUNT(*) FROM items LIMIT {n}";
        async Task<InferenceResult> Predict(string input)
        {
            int n = input[0] - '0';
            Interlocked.Increment(ref calls[n - 1]);
            if (n == 1 || n == 3)
            {
                (n == 1 ? firstStarted : thirdStarted).TrySetResult();
                await finish.Task;
            }
            return new InferenceResult(dual ? input.Replace("items", "<items>") : Sql(n), "eos", 1);
        }
        var vm = new MainViewModel(assets.ToList(), history, inference,
            (stage, _, input, _) => stage == PipelineStage.Front ? Predict(input) :
                Task.FromResult(new InferenceResult($"SELECT {{COUNT(*)}} FROM <TABLE1> LIMIT {input[0]}", "eos", 1)),
            (_, input, _) => Predict(input));
        await vm.Initialization;
        vm.SetDualMode(dual);
        vm.SelectBundle(assets.Single(b => b.IsDual == dual));
        vm.JsonBatchWorkerCount = 2;
        var session = vm.SelectedSession!;
        var rows = new JsonArray();
        for (int n = 1; n <= calls.Length; n++)
        {
            var row = new JsonObject { ["id"] = n, ["input"] = $"{n} count items." };
            if (dual) row["expected"] = n == 2 ? "SELECT 2" : Sql(n);
            rows.Add(row);
        }
        string path = Path.Combine(directory, name + ".json");
        string outputPath = Path.Combine(directory, name + (dual ? "_eval.json" : "_infer.json"));
        string original = rows.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        const string oldResult = "[]";
        await File.WriteAllTextAsync(outputPath, oldResult);
        vm.AttachJsonFile(path);
        var running = vm.SendMessageAsync();
        // With two workers, question 2 must finish before 3 can start while 1 is held.
        await Task.WhenAll(firstStarted.Task, thirdStarted.Task).WaitAsync(TimeSpan.FromSeconds(10));
        Require(vm.IsJsonBatchRunning && vm.IsGenerating && !vm.CanStartInference && vm.CancelJsonBatchCommand.CanExecute(null),
            "An active batch offers Stop while a row is generating");
        vm.AttachJsonFile(path);
        await vm.SendMessageAsync();
        Require(!vm.HasJsonAttachment && session.Messages.Count == 1, "Repeated Send cannot submit a second active batch");
        vm.CancelJsonBatchCommand.Execute(null);
        vm.CancelJsonBatch();
        Require(vm.IsStoppingJsonBatch && vm.IsGenerating && !vm.CancelJsonBatchCommand.CanExecute(null) &&
            vm.StatusMessage == "진행 중인 질문을 모두 마친 뒤 중단합니다." && !running.IsCompleted &&
            await File.ReadAllTextAsync(outputPath) == oldResult,
            "Stop waits for both active questions and keeps the old result intact until saving");
        finish.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        var summary = session.Messages.Single(m => m.IsAssistant);
        var partial = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsArray();
        Require(calls.SequenceEqual([1, 1, 1, 0]) && partial.Count == 3 && await File.ReadAllTextAsync(path) == original,
            "Stop finishes all admitted questions and never starts the fourth");
        for (int n = 1; n <= partial.Count; n++)
        {
            var saved = partial[n - 1]!;
            Require(saved["id"]!.GetValue<int>() == n && saved["inferred"]!.GetValue<string>() == Sql(n) &&
                (!dual || (saved["expected"]!.GetValue<string>() == (n == 2 ? "SELECT 2" : Sql(n)) &&
                    saved["matched"]!.GetValue<bool>() == (n != 2))),
                "Out-of-order completions save predictions and scores at the original input positions");
        }
        Require(!vm.IsJsonBatchRunning && vm.CanStartInference && !vm.CancelJsonBatchCommand.CanExecute(null) &&
            !summary.HasError && summary.Text.Contains("4개 중 3개 처리 후 중단") && summary.Text.Contains(outputPath) &&
            summary.Text.Contains(dual ? "2개가 일치" : "3개의 추론 결과") && !summary.Text.Contains("추론 실패"),
            "The summary counts every finished question and the UI returns to the send state");
        vm.AttachJsonFile(path);
        await vm.SendMessageAsync();
        var restarted = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsArray();
        Require(calls.SequenceEqual([2, 2, 2, 1]) && !session.Messages.Last().Text.Contains("중단") &&
            restarted.Select(row => row!["id"]!.GetValue<int>()).SequenceEqual([1, 2, 3, 4]),
            "A new batch after stopping runs and saves every question in input order");
    }

    private static async Task VerifyCandidatesAndInvalidInputAsync(string directory)
    {
        string path = Path.Combine(directory, "candidates.json");
        int calls = 0;
        Task<IReadOnlyList<string>> Infer(string input)
        {
            calls++;
            return Task.FromResult<IReadOnlyList<string>>(["SELECT 1", "SELECT 2", "SELECT 1"]);
        }
        const string scoredSource = """{"id":"sample","input":"query","expected":"SELECT 2","matched":false,"inferred":"old"}""";
        await File.WriteAllTextAsync(path, scoredSource);
        var result = await JsonBatchService.ProcessAsync(path, Infer);
        Require(result is { Matched: 1, Succeeded: 1, IsEvaluation: true, Total: 1 },
            "Any successful restored SQL candidate can match the expected string");
        string evalPath = result.Path;
        string evaluated = await File.ReadAllTextAsync(evalPath);
        var expectedRow = JsonNode.Parse("""
            {"id":"sample","input":"query","expected":"SELECT 2","matched":true,"inferred":["SELECT 1","SELECT 2"]}
            """);
        Require(JsonNode.DeepEquals(JsonNode.Parse(evaluated), expectedRow) && await File.ReadAllTextAsync(path) == scoredSource,
            "Re-evaluation refreshes both inferred and matched only in the saved copy");
        await File.WriteAllTextAsync(path, """{"input":"query","output":"SELECT 3"}""");
        result = await JsonBatchService.ProcessAsync(path, Infer);
        string inferred = await File.ReadAllTextAsync(result.Path);
        Require(!result.IsEvaluation && JsonNode.DeepEquals(JsonNode.Parse(inferred), JsonNode.Parse("""
            {"input":"query","output":"SELECT 3","inferred":["SELECT 1","SELECT 2"]}
            """)), "A legacy output stays intact during inference and all distinct candidates are saved in order");

        int previousCalls = calls;
        foreach (var (invalidRow, field) in new[]
        {
            ("""{"input":"no expected"}""", "expected"),
            ("""{"input":"wrong type","expected":null}""", "expected"),
            ("""{"input":"wrong type","expected":19}""", "expected"),
            ("""{"expected":"missing input"}""", "input")
        })
        {
            await File.WriteAllTextAsync(path, """[{"input":"valid","expected":"ok"},""" + invalidRow + "]");
            try
            {
                await JsonBatchService.ProcessAsync(path, Infer);
                throw new Exception("Invalid input must not process a subset of the file");
            }
            catch (FormatException ex)
            {
                Require(ex.Message.Contains("2번째") && ex.Message.Contains(field) && calls == previousCalls &&
                    await File.ReadAllTextAsync(evalPath) == evaluated && await File.ReadAllTextAsync(result.Path) == inferred,
                    "An invalid second row is identified before inference or replacement of either saved result");
            }
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception($"JSON batch: {message}");
    }
}
