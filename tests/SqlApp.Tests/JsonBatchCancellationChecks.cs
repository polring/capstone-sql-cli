using System.Text.Json.Nodes;
using SqlApp.Services;

internal static class JsonBatchCancellationChecks
{
    internal static async Task RunAsync(string directory)
    {
        string path = Path.Combine(directory, "stopped-batch.json");
        string output = Path.Combine(directory, "stopped-batch_eval.json");
        const string source = """
            [{"id":1,"input":"first","output":"ok","extra":{"team":["팀"]}},
             {"id":2,"input":"second","output":"ok"}, {"id":3,"input":"unstarted","output":"ok"}]
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
            [{"id":1,"input":"first","output":"ok","extra":{"team":["팀"]},"matched":true},
             {"id":2,"input":"second","output":"ok","matched":true}]
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
            JsonNode.DeepEquals(JsonNode.Parse("""{"input":"one","output":"ok"}"""),
                JsonNode.Parse(await File.ReadAllTextAsync(complete.Path))),
            "A stop during the final question still saves the full result in its original shape");

        using var beforeFailure = new CancellationTokenSource();
        await File.WriteAllTextAsync(path, """[{"input":"fails"},{"input":"unstarted"}]""");
        var failed = await JsonBatchService.ProcessAsync(path, input =>
        {
            Require(input == "fails", "No question starts after the requested stop");
            beforeFailure.Cancel();
            throw new InvalidOperationException("inference failed");
        }, beforeFailure.Token);
        Require(failed is { IsCancelled: true, Total: 2, Processed: 1, Succeeded: 0 } &&
            JsonNode.DeepEquals(JsonNode.Parse("""[{"input":"fails","output":null}]"""),
                JsonNode.Parse(await File.ReadAllTextAsync(failed.Path))),
            "A failed current question still receives its normal failure record before the batch stops");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
