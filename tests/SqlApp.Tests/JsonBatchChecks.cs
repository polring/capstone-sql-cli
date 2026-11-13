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
        var calls = new List<string>();
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
              {"id":17,"input":"match","output":"SELECT 1","extra":{"team":["하나",2]}},
              {"id":"018","input":"case","output":"select 1"},
              {"id":19,"input":"space","output":"SELECT 1 "}
            ]
            """;
        string path = Path.Combine(directory, "questions.v2.json");
        await File.WriteAllTextAsync(path, source);
        await vm.ImportJsonFileAsync(path);
        string resultPath = Path.Combine(directory, "questions.v2_eval.json");
        var evaluated = JsonNode.Parse(await File.ReadAllTextAsync(resultPath))!.AsArray();
        var originals = JsonNode.Parse(source)!.AsArray();
        Require(await File.ReadAllTextAsync(path) == source && calls.SequenceEqual(["match", "case", "space"]),
            "The source stays intact and every row runs in order");
        Require(evaluated.Count == originals.Count, "Evaluation retains all records");
        for (int i = 0; i < evaluated.Count; i++)
        {
            var row = evaluated[i]!.AsObject();
            Require(row["matched"]!.GetValue<bool>() == (i == 0), "Matching is case- and whitespace-sensitive");
            row.Remove("matched");
            Require(JsonNode.DeepEquals(row, originals[i]), "Only matched is appended to the original evaluation record");
        }
        var summary = session.Messages.Single(m => m.IsAssistant);
        Require(summary.Text.Contains("3개 중 1개") && summary.Text.Contains(resultPath) &&
            !summary.HasSqlCandidates && !summary.IsWaitingResponse,
            "A batch produces one completed chat summary with match count and saved path");

        path = Path.Combine(directory, "inputs.json");
        await File.WriteAllTextAsync(path, """[{"input":"fail"},{"input":"predict","id":100,"group":"test"}]""");
        await vm.ImportJsonFileAsync(path);
        var inferred = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "inputs_infer.json")))!.AsArray();
        Require(JsonNode.DeepEquals(inferred, JsonNode.Parse("""
            [{"input":"fail","output":null},{"input":"predict","id":100,"group":"test","output":"SELECT 1"}]
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
            if (evaluate) row.Add("output", expected);
            path = Path.Combine(directory, name + ".json");
            await File.WriteAllTextAsync(path, row.ToJsonString());
            int previousReplies = session.Messages.Count(m => m.IsAssistant);
            await vm.ImportJsonFileAsync(path);
            string suffix = evaluate ? "_eval.json" : "_infer.json";
            resultPath = Path.Combine(directory, name + suffix);
            var saved = JsonNode.Parse(await File.ReadAllTextAsync(resultPath))!.AsObject();
            Require(saved["id"]!.GetValue<string>() == name && saved["output"]!.GetValue<string>() == expected &&
                (!evaluate || saved["matched"]!.GetValue<bool>()), $"Object input keeps its shape and uses the output of {start} to {end}");
            Require(session.Messages.Count(m => m.IsAssistant) == previousReplies + 1 &&
                session.Messages.Last().Text.Contains(resultPath), "Dual and single imports both post one result summary");
        }
        await history.SaveSessionsAsync(vm.Sessions);
        var restored = (await history.LoadSessionsAsync(assets)).Single(s => s.Id == session.Id);
        Require(restored.Messages.Any(m => m.Id == summary.Id && m.Text == summary.Text && !m.IsWaitingResponse),
            "Reloading the conversation preserves completed batch summaries");
        await VerifyCandidatesAndInvalidInputAsync(directory);
        await VerifyBatchStopAsync(directory, assets, dual: false);
        await VerifyBatchStopAsync(directory, assets, dual: true);
        await JsonBatchCancellationChecks.RunAsync(directory);
    }

    private static async Task VerifyBatchStopAsync(string directory, List<AssetBundle> assets, bool dual)
    {
        const string question = "count items.";
        const string tagged = "count <items>.";
        const string taggedSql = "SELECT {COUNT(*)} FROM <TABLE1>";
        const string sql = "SELECT COUNT(*) FROM items";
        string name = dual ? "stop-dual" : "stop-single";
        var history = new SessionStorageService(Path.Combine(directory, name));
        using var inference = new InferenceService();
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishQuestion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        async Task<InferenceResult> Predict(string response)
        {
            if (++calls == 2)
            {
                inFlight.SetResult();
                await finishQuestion.Task;
            }
            return new InferenceResult(response, "eos", 1);
        }
        var vm = new MainViewModel(assets.ToList(), history, inference,
            (stage, _, _, _) => stage == PipelineStage.Front
                ? Predict(tagged) : Task.FromResult(new InferenceResult(taggedSql, "eos", 1)),
            (_, _, _) => Predict(sql));
        await vm.Initialization;
        vm.SetDualMode(dual);
        vm.SelectBundle(assets.Single(b => b.IsDual == dual));
        var session = vm.SelectedSession!;
        var rows = new JsonArray();
        for (int i = 0; i < 3; i++)
        {
            var row = new JsonObject { ["id"] = i, ["input"] = question };
            if (dual) row["output"] = sql;
            rows.Add(row);
        }
        string path = Path.Combine(directory, name + ".json");
        string outputPath = Path.Combine(directory, name + (dual ? "_eval.json" : "_infer.json"));
        string original = rows.ToJsonString();
        await File.WriteAllTextAsync(path, original);
        var running = vm.ImportJsonFileAsync(path);
        await inFlight.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(vm.IsJsonBatchRunning && vm.IsGenerating && !vm.CanStartInference && vm.CancelJsonBatchCommand.CanExecute(null),
            "An active batch offers Stop while a row is generating");
        vm.CancelJsonBatchCommand.Execute(null);
        vm.CancelJsonBatch();
        Require(vm.IsStoppingJsonBatch && vm.IsGenerating && !vm.CancelJsonBatchCommand.CanExecute(null) &&
            vm.StatusMessage == "현재 질문을 마친 뒤 중단합니다." && !running.IsCompleted,
            "Stop acknowledges the request while the in-flight question continues");
        finishQuestion.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        var summary = session.Messages.Single(m => m.IsAssistant);
        var partial = JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsArray();
        Require(calls == 2 && partial.Count == 2 && await File.ReadAllTextAsync(path) == original,
            "Stop preserves the second question and never starts the third");
        for (int i = 0; i < partial.Count; i++)
            Require(partial[i]!["id"]!.GetValue<int>() == i && partial[i]!["output"]!.GetValue<string>() == sql &&
                (!dual || partial[i]!["matched"]!.GetValue<bool>()), "Every saved question ran to completion");
        Require(!vm.IsJsonBatchRunning && vm.CanStartInference && !vm.CancelJsonBatchCommand.CanExecute(null) &&
            !summary.HasError && summary.Text.Contains("3개 중 2개 처리 후 중단") && summary.Text.Contains(outputPath) &&
            summary.Text.Contains(dual ? "2개가 일치" : "2개의 output") && !summary.Text.Contains("추론 실패"),
            "The summary includes the in-flight question and the UI returns to the send state");
        await vm.ImportJsonFileAsync(path);
        Require(calls == 5 && !session.Messages.Last().Text.Contains("중단") &&
            JsonNode.Parse(await File.ReadAllTextAsync(outputPath))!.AsArray().Count == 3,
            "Starting a new batch after stopping gets a fresh token");
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
        await File.WriteAllTextAsync(path, """{"id":"sample","input":"query","output":"SELECT 2","matched":false}""");
        var result = await JsonBatchService.ProcessAsync(path, Infer);
        Require(result is { Matched: 1, Succeeded: 1, IsEvaluation: true, Total: 1 },
            "Any successful restored SQL candidate can match the expected string");
        Require(JsonNode.Parse(await File.ReadAllTextAsync(result.Path))!["matched"]!.GetValue<bool>() &&
            !JsonNode.Parse(await File.ReadAllTextAsync(path))!["matched"]!.GetValue<bool>(),
            "Re-evaluating refreshes matched in the saved copy only");
        await File.WriteAllTextAsync(path, """{"input":"query"}""");
        result = await JsonBatchService.ProcessAsync(path, Infer);
        var output = JsonNode.Parse(await File.ReadAllTextAsync(result.Path))!["output"];
        Require(JsonNode.DeepEquals(output, JsonNode.Parse("""["SELECT 1","SELECT 2"]""")),
            "Inference retains multiple distinct SQL candidates, in order");

        int previousCalls = calls;
        string untouched = await File.ReadAllTextAsync(result.Path);
        await File.WriteAllTextAsync(path, """[{"input":"ok"},{"input":"also ok","output":"x"}]""");
        try
        {
            await JsonBatchService.ProcessAsync(path, Infer);
            throw new Exception("Mixed rows must not process a subset of the file");
        }
        catch (FormatException)
        {
            Require(calls == previousCalls && await File.ReadAllTextAsync(result.Path) == untouched,
                "Mixed input is rejected before prediction or replacement of an existing result");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception($"JSON batch: {message}");
    }
}
