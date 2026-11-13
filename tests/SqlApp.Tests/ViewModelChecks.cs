using System.Text.Json;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.ViewModels;

internal static class ViewModelChecks
{
    internal static async Task RunAsync(string tempRoot, List<AssetBundle> assets, JsonElement example, JsonElement expected)
    {
        string historyFolder = Path.Combine(tempRoot, "viewmodel-history");
        var history = new SessionStorageService(historyFolder);
        var dual = assets.Single(b => b.FrontModelPath.Length > 0 && b.BackModelPath.Length > 0);
        string question = example.GetProperty("input").GetString()!, taggedQuestion = example.GetProperty("output").GetString()!;
        string generalized = expected.GetProperty("generalizedQuestion").GetString()!, taggedSql = expected.GetProperty("taggedSql").GetString()!;
        string ambiguous = question.Replace("kitchen category", "kitchen home category", StringComparison.Ordinal);
        const string numeric = "find items over 20 red limits";
        string categorySql = taggedSql;
        var roles = new List<PipelineStage>();
        using var inference = new InferenceService();
        Task<InferenceResult> Predict(PipelineStage stage, string model, string input, int limit)
        {
            roles.Add(stage);
            Require(limit == 64, "Selected token budget");
            if (stage == PipelineStage.Front && model == dual.FrontModelPath && (input == question || input == ambiguous))
                return Task.FromResult(new InferenceResult(taggedQuestion, "eos", 0.75));
            if (stage == PipelineStage.Back && model == dual.BackModelPath && input == generalized)
                return Task.FromResult(new InferenceResult(categorySql, "eos", 2.25));
            if (stage == PipelineStage.Front && model == dual.FrontModelPath && input == numeric)
                return Task.FromResult(new InferenceResult("find <items> over [AGG_VALUE, numeric, #1] <items.category, string>", "eos", 0.75));
            if (stage == PipelineStage.Back && model == dual.BackModelPath && input == "find <TABLE1> over [AGG_VALUE, numeric, #1] <TABLE1.COL1, string>")
                return Task.FromResult(new InferenceResult("SELECT {COUNT(*)} FROM <TABLE1> HAVING COUNT(*) > [AGG_VALUE, numeric, #1]", "eos", 2.25));
            throw new InvalidOperationException($"Unexpected model input for {stage}");
        }

        var vm = new MainViewModel(assets.ToList(), history, inference, Predict);
        await vm.Initialization;
        Require(vm.IsSingleMode && vm.AvailableBundles.All(b => !b.IsDual), "New single-model conversation");
        vm.SelectBundle(assets.Single(b => !b.IsDual));
        string singleInputFile = Path.Combine(tempRoot, "single-question.json");
        await File.WriteAllTextAsync(singleInputFile, """{"question":"hello"}""");
        await vm.ImportJsonFileAsync(singleInputFile);
        Require(vm.InputText == "hello", "Single-model chat can still import a question");
        await vm.SendMessageAsync();
        Require(vm.SelectedSession!.Messages.Last() is { HasError: false, ComputeTimeMs: > 0 },
            "Single-model chat records real forward time");
        vm.SetDualMode(true);
        var backOnly = Directory.CreateDirectory(Path.Combine(tempRoot, "viewmodel-back-only")).FullName;
        File.Copy(dual.BackModelPath, Path.Combine(backOnly, "back.gguf"));
        vm.SelectAssetFolder(backOnly);
        Require(vm.SelectedRange == new StageRange(PipelineStage.Back, PipelineStage.Back) && vm.StatusMessage.Length == 0,
            "External back-only folder works without a database or mapping");
        var session = vm.SelectedSession!;
        var schemaOnly = Directory.CreateDirectory(Path.Combine(tempRoot, "viewmodel-schema-only")).FullName;
        File.Copy(dual.DbPath, Path.Combine(schemaOnly, "shop.db"));
        vm.SelectAssetFolder(schemaOnly);
        Require(vm.SelectedRange == new StageRange(PipelineStage.Front, PipelineStage.Front) && vm.StatusMessage.Length == 0,
            "A schema-only folder cannot introduce a mapper-only slider selection");
        int beforeSend = session.Messages.Count;
        vm.InputText = question;
        await vm.SendMessageAsync();
        Require(vm.StatusMessage.Length > 0 && vm.InputText == question && session.Messages.Count == beforeSend && roles.Count == 0,
            "Report the missing front model on send and keep the draft");
        vm.SelectBundle(dual);
        Require(vm.IsDualMode && vm.AvailableBundles.All(b => b.IsDual) && vm.CurrentSessionBundle == dual,
            "Mode filters available folders");
        Require(vm.SelectedRange == new StageRange(PipelineStage.Front, PipelineStage.Postprocessor), "Complete bundle default");
        VerifySliderRanges(vm);

        foreach (var end in new[] { PipelineStage.Front, PipelineStage.Back })
        {
            vm.SelectRange(new(PipelineStage.Front, end));
            vm.InputText = question;
            await vm.SendMessageAsync();
            var partial = session.Messages.Last();
            Require(!partial.HasError && !partial.CanExecuteSql && partial.PipelineRun?.Steps.Last().Stage == end,
                $"A typed question executes through the selected {end} endpoint");
        }
        vm.SelectRange(new(PipelineStage.Front, PipelineStage.Mapper));
        vm.InputText = question;
        await vm.ImportJsonFileAsync(Path.Combine(AppContext.BaseDirectory, "category.json"));
        Require(vm.InputText == question, "An attached training pair cannot overwrite a dual-model draft");
        await vm.SendMessageAsync();
        var front = session.Messages.Last();
        Require(front.PipelineRun?.Mapping != null && !front.CanExecuteSql && session.Messages[^2].Text == question &&
            front.PipelineRun.Steps.First().Input == question && front.PipelineRun.Steps.Last().Input == taggedQuestion,
            "One typed question feeds the front model and its output feeds the mapper");
        Require(front.ComputeTimeMs == 0.75, "Front compute time reaches chat");

        vm.SelectRange(new(PipelineStage.Back, PipelineStage.Back));
        Require(vm.InputText.Length == 0, "Selecting a back-only range leaves the chat empty");
        string frontMappingJson = front.PipelineRun!.SerializeMappings();
        using var frontMappings = JsonDocument.Parse(frontMappingJson);
        CandidatePipelineChecks.VerifyEnvelope(frontMappings.RootElement, session.Id, front.Id);
        Require(frontMappings.RootElement.GetProperty("Candidates").GetArrayLength() == 1,
            "A single candidate is carried in the same envelope");
        vm.InputText = generalized;
        await vm.SendMessageAsync();
        var back = session.Messages.Last();
        Require(back.PipelineRun?.TaggedSql == taggedSql && back.PipelineRun.Mappings == null && !back.CanExecuteSql,
            "Back-only produces tagged SQL without carrying a previous mapper result");
        Require(back.ComputeTimeMs == 2.25, "Back compute time reaches chat");
        Require(vm.StatusMessage.Length == 0, "Successful inference clears the composer status");

        vm.SelectRange(new(PipelineStage.Front, PipelineStage.Postprocessor));
        roles.Clear();
        vm.InputText = question;
        await vm.SendMessageAsync();
        var final = session.Messages.Last();
        Require(final is { HasError: false, ComputeTimeMs: 3.0, CanExecuteSql: true } &&
            final.Sql == expected.GetProperty("finalSql").GetString() && roles.SequenceEqual([PipelineStage.Front, PipelineStage.Back]),
            "A full-range question calls each model once, records their time and returns executable SQL");

        vm.InputText = ambiguous;
        roles.Clear();
        await vm.SendMessageAsync();
        var choices = session.Messages.Last();
        Require(roles.SequenceEqual([PipelineStage.Front, PipelineStage.Back]) && choices.SqlCandidates.Count == 2 &&
            choices.SqlCandidates.All(c => c.CanExecuteSql && !c.HasValidationError),
            "The same run supplies both candidate mappings to postprocessing after a single back-model call");

        categorySql = taggedSql + " AND";
        vm.InputText = ambiguous;
        await vm.SendMessageAsync();
        categorySql = taggedSql;
        var invalid = session.Messages.Last();
        Require(invalid.SqlCandidates.Count == 2 && invalid.SqlCandidates.All(c =>
            c.Sql.EndsWith(" AND") && c.HasValidationError && !c.CanExecuteSql), "Show both invalid SQL texts and disable each one");
        await vm.RunCandidateAsync(invalid.SqlCandidates[0]);
        Require(session.Messages.All(m => m.SourceMessageId != invalid.Id), "A disabled candidate cannot run through the command");

        vm.InputText = numeric;
        await vm.SendMessageAsync();
        var mixed = session.Messages.Last();
        Require(!mixed.HasError && mixed.SqlCandidates.Count == 2 &&
            mixed.SqlCandidates.Count(c => c.CanExecuteSql && c.Sql == "SELECT COUNT(*) FROM items HAVING COUNT(*) > 20") == 1 &&
            mixed.SqlCandidates.Count(c => !c.CanExecuteSql && c.HasValidationError && c.Sql.Length == 0) == 1,
            "One unparseable numeric source value leaves the other SQL candidate available");

        vm.SetDualMode(false);
        Require(vm.CurrentSessionBundle?.DbPath != final.DbPath, "Switch current database before replay");
        await vm.RunSqlAsync(final);
        Require(session.Messages.Single(m => m.Id == final.ResultMessageId).QueryResult?.Rows.Single().Single() == "cup",
            "Run against original database");
        var cup = choices.SqlCandidates.Single(c => c.Sql.EndsWith("'kitchen'"));
        var empty = choices.SqlCandidates.Single(c => c.Sql.EndsWith("'kitchen home'"));
        await vm.RunCandidateAsync(empty);
        await vm.RunCandidateAsync(cup);
        await vm.RunCandidateAsync(empty);
        var results = session.Messages.Where(m => m.SourceMessageId == choices.Id).ToList();
        Require(results.Count == 2 && results.Single(m => m.Sql == cup.Sql).QueryResult?.Rows.Single().Single() == "cup" &&
            results.Single(m => m.Sql == empty.Sql).QueryResult?.RowCount == 0,
            "Each candidate executes against the original database and reruns update only its own result");
        await history.SaveSessionsAsync(vm.Sessions);
        using var stored = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(historyFolder, "sessions.json")));
        var storedChoices = stored.RootElement[0].GetProperty("Messages").EnumerateArray()
            .Single(m => m.GetProperty("Id").GetGuid() == choices.Id);
        var storedMappings = storedChoices.GetProperty("PipelineRun").GetProperty("Mappings");
        CandidatePipelineChecks.VerifyEnvelope(storedMappings, session.Id, choices.Id);
        Require(storedMappings.GetProperty("Candidates").EnumerateArray().Select(record =>
                record.GetProperty("Values").GetProperty("[TABLE1.COL2, string, #1]").GetString())
            .Order(StringComparer.Ordinal).SequenceEqual(["kitchen", "kitchen home"]),
            "Save the condition string for every candidate in the conversation");
        var reopened = new MainViewModel(assets.ToList(), history, inference, Predict);
        await reopened.Initialization;
        var saved = reopened.SelectedSession!;
        var restored = saved.Messages.Single(m => m.Id == choices.Id);
        Require(restored.SqlCandidates.Count == 2 && restored.SqlCandidates.All(c => c.CanExecuteSql) &&
            saved.Messages.Count(m => m.SourceMessageId == restored.Id) == 2 &&
            saved.Messages.Single(m => m.Id == invalid.Id).SqlCandidates.All(c => c.HasValidationError),
            "History restores every candidate, its validated state, and its own result");

        reopened.SetDualMode(true);
        reopened.SelectBundle(dual);
        Require(reopened.InputText.Length == 0, "Restoring a session leaves the chat empty");
        using var replayMappings = JsonDocument.Parse(restored.PipelineRun!.SerializeMappings());
        CandidatePipelineChecks.VerifyEnvelope(replayMappings.RootElement, saved.Id, choices.Id);
        reopened.InputText = ambiguous;
        roles.Clear();
        await reopened.SendMessageAsync();
        var replay = saved.Messages.Last();
        Require(!replay.HasError && roles.SequenceEqual([PipelineStage.Front, PipelineStage.Back]) &&
            replay.PipelineRun!.Mappings?.MessageId == replay.Id &&
            replay.SqlCandidates.Select(c => c.Sql).SequenceEqual(restored.SqlCandidates.Select(c => c.Sql)),
            "After restart, a new full range builds its own mappings and restores both candidates");

        reopened.CreateNewSession();
        reopened.SetDualMode(true);
        reopened.SelectBundle(dual);
        reopened.SelectRange(new(PipelineStage.Back, PipelineStage.Postprocessor));
        reopened.InputText = generalized;
        roles.Clear();
        await reopened.SendMessageAsync();
        var standalone = reopened.SelectedSession!.Messages.Last();
        Require(!standalone.HasError && standalone.PipelineRun?.Mappings == null && roles.SequenceEqual([PipelineStage.Back]),
            "A new conversation snaps back-through-post to back-only and uses no historical mapping");
        await history.SaveSessionsAsync(reopened.Sessions);
    }

    private static void VerifySliderRanges(MainViewModel vm)
    {
        var full = new StageRange(PipelineStage.Front, PipelineStage.Postprocessor);
        var backOnly = new StageRange(PipelineStage.Back, PipelineStage.Back);
        var stages = Enum.GetValues<PipelineStage>();
        var reachable = new HashSet<StageRange>();
        vm.InputText = "draft while moving handles";
        foreach (var start in stages)
        foreach (var end in stages.Where(stage => stage >= start))
        {
            var expected = start <= PipelineStage.Mapper ? new(PipelineStage.Front, end) : backOnly;
            vm.SelectRange(new(start, end));
            Require(vm.SelectedRange == expected && vm.InputText == "draft while moving handles" && vm.StatusMessage.Length == 0,
                $"{start} through {end} snaps to {expected} without disturbing the draft or announcing a correction");
            reachable.Add(vm.SelectedRange);
        }
        Require(reachable.Count == 5, "Exactly five selections can be displayed");

        foreach (var end in stages)
        {
            var frontLed = new StageRange(PipelineStage.Front, end);
            Require(full.WithEnd(end) == frontLed && frontLed.WithStart(PipelineStage.Back) == backOnly,
                $"The end handle reaches {end} and the start handle can then select back-only");
            Require(backOnly.WithEnd(end) == backOnly, "Changing the end keeps a back-only range on its single cell");
            Require(backOnly.WithStart(PipelineStage.Front).WithEnd(end) == frontLed,
                $"Moving the start handle back to front allows the end handle to reach {end} again");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException($"ViewModel: {message}");
    }
}
