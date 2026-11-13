using System.Text.Json.Nodes;
using SqlApp.Services;

internal static class JsonBatchCancellationChecks
{
    internal static async Task RunAsync(string directory)
    {
        string path = Path.Combine(directory, "stopped-batch.json");
        string output = Path.Combine(directory, "stopped-batch_eval.json");
        const string source = """
            [{"id":1,"input":"first","expected":"ok","extra":{"team":["팀"]}},
             {"id":2,"input":"second","expected":"ok"}, {"id":3,"input":"unstarted","expected":"ok"}]
            """;
        await File.WriteAllTextAsync(path, source);
        using var stop = new CancellationTokenSource();
        var enteredSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        async Task<IReadOnlyList<string>> InferAsync(string input)
        {
            calls.Add(input);
            if (input == "second")
            {
                enteredSecond.SetResult();
                await finishSecond.Task;
            }
            return ["ok"];
        }
        var batch = JsonBatchService.ProcessAsync(path, InferAsync, stop.Token);
        await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        Require(!batch.IsCompleted, "Stopping an active question waits for its inference");
        finishSecond.SetResult();
        var partial = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        var expected = JsonNode.Parse("""
            [{"id":1,"input":"first","expected":"ok","extra":{"team":["팀"]},"inferred":"ok","matched":true},
             {"id":2,"input":"second","expected":"ok","inferred":"ok","matched":true}]
            """);
        Require(partial is { IsCancelled: true, Total: 3, Processed: 2, Succeeded: 2, Matched: 2 } &&
            calls.SequenceEqual(["first", "second"]) && partial.Path == output &&
            JsonNode.DeepEquals(expected, JsonNode.Parse(await File.ReadAllTextAsync(output))) &&
            await File.ReadAllTextAsync(path) == source, "An in-flight stop includes the current question and skips the next one");

        string kept = await File.ReadAllTextAsync(output);
        var empty = await JsonBatchService.ProcessAsync(path, InferAsync, stop.Token);
        Require(empty is { IsCancelled: true, Total: 3, Processed: 0, Path.Length: 0 } &&
            calls.Count == 2 && await File.ReadAllTextAsync(output) == kept,
            "Stopping before the first row leaves an existing result intact");

        using var last = new CancellationTokenSource();
        await File.WriteAllTextAsync(path, """{"input":"one"}""");
        var complete = await JsonBatchService.ProcessAsync(path, _ =>
        {
            last.Cancel();
            return Task.FromResult<IReadOnlyList<string>>(["ok"]);
        }, last.Token);
        Require(complete is { IsCancelled: false, Total: 1, Processed: 1, Succeeded: 1 } &&
            JsonNode.DeepEquals(JsonNode.Parse("""{"input":"one","inferred":"ok"}"""),
                JsonNode.Parse(await File.ReadAllTextAsync(complete.Path))),
            "A stop during the final question still saves the full result in its original shape");

        using var beforeFailure = new CancellationTokenSource();
        await File.WriteAllTextAsync(path, """[{"input":"fails","expected":""},{"input":"unstarted","expected":"ok"}]""");
        var failed = await JsonBatchService.ProcessAsync(path, input =>
        {
            Require(input == "fails", "No question starts after the requested stop");
            beforeFailure.Cancel();
            throw new InvalidOperationException("inference failed");
        }, beforeFailure.Token);
        Require(failed is { IsEvaluation: true, IsCancelled: true, Total: 2, Processed: 1, Succeeded: 0, Matched: 0 } &&
            JsonNode.DeepEquals(JsonNode.Parse("""[{"input":"fails","expected":"","inferred":null,"matched":false}]"""),
                JsonNode.Parse(await File.ReadAllTextAsync(failed.Path))),
            "A failed current question still receives its normal failure record before the batch stops");
        await VerifyParallelResultsAsync(directory);
        await VerifyWorkerFailureAsync(directory);
    }

    private static async Task VerifyParallelResultsAsync(string directory)
    {
        string path = Path.Combine(directory, "parallel-candidates.json");
        await File.WriteAllTextAsync(path, """[{"input":"slow"},{"input":"fails"},{"input":"fast"}]""");
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new List<int>();
        async Task<IReadOnlyList<string>> InferAsync(string input)
        {
            if (input == "slow")
            {
                await releaseSlow.Task;
                return ["SQL A", "SQL B", "SQL A"];
            }
            if (input == "fails") throw new FormatException("Unsupported input");
            return ["SQL C"];
        }
        var batch = JsonBatchService.ProcessAsync(path, [InferAsync, InferAsync], CancellationToken.None,
            (done, total) =>
            {
                Require(total == 3, "Every progress update uses the full dataset size");
                progress.Add(done);
                if (done == 2) releaseSlow.SetResult();
            });
        var result = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        var expected = JsonNode.Parse("""
            [{"input":"slow","inferred":["SQL A","SQL B"]},{"input":"fails","inferred":null},{"input":"fast","inferred":"SQL C"}]
            """);
        Require(result is { IsCancelled: false, Total: 3, Processed: 3, Succeeded: 2 } &&
            progress.SequenceEqual([0, 1, 2, 3]) &&
            JsonNode.DeepEquals(expected, JsonNode.Parse(await File.ReadAllTextAsync(result.Path))),
            "Parallel failures and multi-candidate outputs retain their original rows and report completion once per row");
    }

    private static async Task VerifyWorkerFailureAsync(string directory)
    {
        string path = Path.Combine(directory, "worker-fault.json");
        string output = Path.Combine(directory, "worker-fault_infer.json");
        const string previous = """[{"saved":"previous"}]""";
        await File.WriteAllTextAsync(path, """[{"input":"active"},{"input":"fault"}]""");
        await File.WriteAllTextAsync(output, previous);
        var faulted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fault = new IOException("Model could not be read");
        int finished = 0;
        async Task<IReadOnlyList<string>> InferAsync(string input)
        {
            if (input == "fault")
            {
                faulted.SetResult();
                throw fault;
            }
            await release.Task;
            Interlocked.Increment(ref finished);
            return ["ok"];
        }
        var batch = JsonBatchService.ProcessAsync(path, [InferAsync, InferAsync], CancellationToken.None);
        await faulted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(!batch.IsCompleted, "A fatal error waits for active calls before releasing their engines");
        release.SetResult();
        try
        {
            await batch.WaitAsync(TimeSpan.FromSeconds(10));
            throw new Exception("An unexpected inference error must propagate");
        }
        catch (IOException ex) when (ReferenceEquals(ex, fault))
        {
            Require(finished == 1 && await File.ReadAllTextAsync(output) == previous,
                "After draining workers, the fatal error propagates and preserves the previous output");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
