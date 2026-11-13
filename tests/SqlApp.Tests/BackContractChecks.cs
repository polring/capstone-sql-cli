using System.Text.Json;
using SqlApp.Interop.SqlEngine;
using SqlApp.Models;
using SqlApp.Services;

internal static class BackContractChecks
{
    internal static async Task VerifyAsync(string fixtures)
    {
        string atomicV3 = Path.Combine(fixtures, "v3", "atomic_tags_2");
        using var cases = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(atomicV3, "cases.json")));
        await VerifyAtomicAsync(Path.Combine(atomicV3, "model.gguf"), cases.RootElement);
        string currentModel = Path.Combine(fixtures, "atomic_tags", "model.gguf");
        var gguf = File.ReadAllBytes(currentModel);
        Require(gguf.AsSpan().IndexOf("capstone_sql.contract_version"u8) < 0 &&
            gguf.AsSpan().IndexOf("capstone_sql.tokenizer.profile"u8) < 0,
            "The current exporter stores the complete vocabulary directly");
        await VerifyAtomicAsync(currentModel, cases.RootElement);

        string oldModel = Path.Combine(fixtures, "v3", "tagged_sql", "model.gguf");
        using var engine = new InferenceService();
        Require(engine.GetStageUnavailableReason(PipelineStage.Back, oldModel)?.Contains("95개") == true,
            "Explain a decomposed-tag model before starting inference");
        try
        {
            GgufContract.ReadTokenizer(oldModel, ModelRole.Back);
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Loaded a model with the retired vocabulary.");
    }

    private static async Task VerifyAtomicAsync(string path, JsonElement expected)
    {
        var tokenizer = GgufContract.ReadTokenizer(path, ModelRole.Back);
        Require(tokenizer is BackModelTokenizer, "Back stage selects the back-model tokenizer");
        int[] sqlIds = expected.GetProperty("sqlIds").Deserialize<int[]>()!;
        // EOS is consulted separately in the native generator.
        var decodable = Enumerable.Range(0, tokenizer.VocabSize).Where(i => tokenizer.DecodableMask[i] != 0);
        Require(sqlIds[0] == tokenizer.EosId && decodable.SequenceEqual(sqlIds.Skip(1)), "SQL mask");
        var prompts = expected.GetProperty("prompts");
        foreach (var prompt in prompts.EnumerateArray())
        {
            var ids = tokenizer.EncodePrompt(prompt.GetProperty("question").GetString()!);
            Require(ids.SequenceEqual(prompt.GetProperty("ids").Deserialize<int[]>()!), "Question tokenization");
        }

        using var engine = new InferenceService();
        Require(engine.GetStageUnavailableReason(PipelineStage.Back, path) == null &&
            engine.GetStageUnavailableReason(PipelineStage.Front, path) != null, "SQL decoder role");
        bool singleRejected = false;
        try
        {
            await engine.RunQueryAsync(path, "hello", 1);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            singleRejected = true;
        }
        Require(singleRejected, "An atomic-tag back model cannot run as a single model");
        // The fixture's highest unmasked logit is the question-only byte 'x'.
        // Compare the selected SQL IDs after decoding and the stop reason to the Python exporter.
        foreach (var item in expected.GetProperty("generations").EnumerateArray())
        {
            var result = await engine.RunStageAsync(PipelineStage.Back, path,
                prompts[0].GetProperty("question").GetString()!, item.GetProperty("limit").GetInt32());
            Require(result.Text == item.GetProperty("sql").GetString() &&
                result.StopReason == item.GetProperty("stop").GetString(), "Greedy generation");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
