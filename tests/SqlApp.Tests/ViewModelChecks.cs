using System.ComponentModel;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.ViewModels;

internal static class ViewModelChecks
{
    internal static async Task RunAsync(string tempRoot, List<AssetBundle> assets)
    {
        string historyFolder = Path.Combine(tempRoot, "viewmodel-history");
        var history = new SessionStorageService(historyFolder);
        var dual = assets.Single(b => b.FrontModelPath.Length > 0 && b.BackModelPath.Length > 0);
        const string numeric = "find items over 20 red limits";
        const string question = "please show me items";
        const string frontOutput = "Whatthe <items> are there?";
        var roles = new List<PipelineStage>();
        using var inference = new InferenceService();
        Task<InferenceResult> Predict(PipelineStage stage, string model, string input, int limit)
        {
            roles.Add(stage);
            Require(limit == 200, "Default token budget for each model stage");
            if (stage == PipelineStage.Front && model == dual.FrontModelPath && input == numeric)
                return Task.FromResult(new InferenceResult("find <items> over [AGG_VALUE, numeric, #1] <items.category, string>", "eos", 0.75));
            if (stage == PipelineStage.Front && model == dual.FrontModelPath && input == question)
                return Task.FromResult(new InferenceResult(frontOutput, "eos", 0.75));
            if (stage == PipelineStage.Back && model == dual.BackModelPath && input == "find <TABLE1> over [AGG_VALUE, numeric, #1] <TABLE1.COL1, string>")
                return Task.FromResult(new InferenceResult("SELECT {COUNT(*)} FROM <TABLE1> HAVING COUNT(*) > [AGG_VALUE, numeric, #1]", "eos", 2.25));
            throw new InvalidOperationException($"Unexpected model input for {stage}");
        }

        var vm = new MainViewModel(assets.ToList(), history, inference, Predict);
        await vm.Initialization;
        Require(vm.IsSingleMode && vm.AvailableBundles.All(b => !b.IsDual), "New single-model conversation");
        await VerifyWelcomeSuggestionsAsync(vm, dual);
        vm.SelectBundle(assets.Single(b => !b.IsDual));
        vm.InputText = "hello";
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
        vm.InputText = numeric;
        await vm.SendMessageAsync();
        Require(vm.StatusMessage.Length > 0 && vm.InputText == numeric && session.Messages.Count == beforeSend && roles.Count == 0,
            "Report the missing front model on send and keep the draft");
        vm.SelectBundle(dual);
        Require(vm.IsDualMode && vm.AvailableBundles.All(b => b.IsDual) && vm.CurrentSessionBundle == dual,
            "Mode filters available folders");
        Require(vm.SelectedRange == new StageRange(PipelineStage.Front, PipelineStage.Postprocessor), "Complete bundle default");
        VerifySliderRanges(vm);

        vm.SelectRange(new(PipelineStage.Front, PipelineStage.Postprocessor));
        vm.InputText = numeric;
        await vm.SendMessageAsync();
        var mixed = session.Messages.Last();
        Require(!mixed.HasError && mixed.SqlCandidates.Count == 2 &&
            mixed.SqlCandidates.Count(c => c.CanExecuteSql && c.Sql == "SELECT COUNT(*) FROM items HAVING COUNT(*) > 20") == 1 &&
            mixed.SqlCandidates.Count(c => !c.CanExecuteSql && c.HasValidationError && c.Sql.Length == 0) == 1,
            "One unparseable numeric source value leaves the other SQL candidate available");
        await vm.RunCandidateAsync(mixed.SqlCandidates.Single(c => c.CanExecuteSql));
        Require(session.Messages.Any(m => m.SourceMessageId == mixed.Id && m.QueryResult is { HasError: false }),
            "A full dual run can execute its valid SQL candidate");
        await VerifyPartialOutputsAsync(vm, history, assets, dual.DbPath);

        vm.InputText = question;
        await vm.SendMessageAsync();
        var failed = session.Messages.Last();
        var mapper = failed.PipelineRun!.Steps.Last();
        Require(mapper.Stage == PipelineStage.Mapper && mapper.Input == frontOutput && mapper.Error.Length > 0,
            "A changed front-model sentence fails the mapper's source alignment");
        string expectedDetails = $"원문 질문\n{question}\n\n매퍼 입력\n{frontOutput}\n\n오류 원인\n{mapper.Error}";
        await history.SaveSessionsAsync(vm.Sessions);
        var restored = (await history.LoadSessionsAsync(assets)).Single(s => s.Id == session.Id);
        foreach (var message in new[] { failed, restored.Messages.Single(m => m.Id == failed.Id) })
            Require(message.HasError && message.ErrorMessage == mapper.Error && message.HasMapperError &&
                message.ShowStageDetails && !message.ShowPipelineOutput && message.MapperErrorDetails == expectedDetails &&
                message.StageDetails.Contains("[앞모델 ·") && message.StageDetails.Contains($"[매퍼 · 실패]\n입력\n{question}\n{frontOutput}") &&
                message.StageDetails.Contains(mapper.Error) && !message.StageDetails.Contains("[뒷모델 ·"),
                "Mapper failures retain both the error details and the stages actually reached, including after reload");

        vm.InputText = "unexpected front-model input";
        await vm.SendMessageAsync();
        var frontFailure = session.Messages.Last();
        Require(frontFailure.HasError && !frontFailure.HasMapperError && frontFailure.ShowStageDetails,
            "A full run failing in the front model retains its stage details without a mapper detail button");
    }

    private static async Task VerifyWelcomeSuggestionsAsync(MainViewModel vm, AssetBundle dual)
    {
        string[] singleExamples = vm.PromptSuggestions.ToArray();
        string singleDescription = vm.WelcomeDescription;
        Require(singleExamples.Length == 3 && singleExamples.Contains("please show me information of peter."),
            "The single-model welcome screen retains its original examples");

        vm.SetDualMode(true);
        vm.SelectBundle(dual);
        await vm.LoadSchemaAsync();
        var changed = new HashSet<string?>();
        void TrackChanges(object? sender, PropertyChangedEventArgs e) => changed.Add(e.PropertyName);
        vm.PropertyChanged += TrackChanges;
        var descriptions = new HashSet<string>();
        foreach (var range in new StageRange[] {
            new(PipelineStage.Front, PipelineStage.Front), new(PipelineStage.Front, PipelineStage.Mapper),
            new(PipelineStage.Front, PipelineStage.Back), new(PipelineStage.Front, PipelineStage.Postprocessor),
            new(PipelineStage.Back, PipelineStage.Back) })
        {
            string previousDraft = vm.InputText;
            changed.Clear();
            vm.SelectRange(range);
            Require(vm.InputText == previousDraft && changed.Contains(nameof(vm.WelcomeDescription)) &&
                !string.IsNullOrWhiteSpace(vm.WelcomeDescription) && descriptions.Add(vm.WelcomeDescription),
                $"Selecting {range} refreshes its distinct welcome instructions without replacing the draft");
            var examples = vm.PromptSuggestions;
            Require(examples.Count == 3 && examples.Distinct().Count() == 3 && examples.All(s => !string.IsNullOrWhiteSpace(s)),
                $"{range} displays three different example inputs");
            if (range.Start == PipelineStage.Back)
                Require(changed.Contains(nameof(vm.PromptSuggestions)) && examples.All(s => s.Contains("TABLE1")),
                    "Back-only selection refreshes the list with generalized questions");
            else
                Require(examples.Any(s => s.Contains("items", StringComparison.OrdinalIgnoreCase)) &&
                    examples.All(s => !s.Contains("TABLE1")), "Front-led examples use names from the selected database");

            foreach (var example in examples)
            {
                vm.UsePromptSuggestionCommand.Execute(example);
                Require(vm.InputText == example && !vm.SelectedSession!.HasMessages,
                    $"Choosing an example in {range} fills the composer and waits for Send");
            }
        }
        vm.PropertyChanged -= TrackChanges;
        vm.SetDualMode(false);
        Require(vm.WelcomeDescription == singleDescription && vm.PromptSuggestions.SequenceEqual(singleExamples),
            "Switching back to single mode restores its welcome description and examples");
    }

    private static async Task VerifyPartialOutputsAsync(MainViewModel vm, SessionStorageService history,
        List<AssetBundle> assets, string db)
    {
        var session = vm.SelectedSession!;
        var stages = Enum.GetValues<PipelineStage>();
        var full = new StageRange(PipelineStage.Front, PipelineStage.Postprocessor);
        var expected = new Dictionary<Guid, string>();
        foreach (var start in stages)
        foreach (var end in stages.Where(stage => stage >= start))
        {
            var range = new StageRange(start, end);
            if (range == full) continue;
            // Older partial runs ending at the postprocessor can contain valid restored SQL.
            string output = end == PipelineStage.Postprocessor ? "SELECT 1" : $"Output from {end}";
            var run = new PipelineRun { Range = range };
            foreach (var stage in stages.Where(range.Contains))
                run.Steps.Add(new() { Stage = stage, Input = $"Input for {stage}", Output = $"Output from {stage}" });
            run.Steps[^1].Output = output;
            if (end == PipelineStage.Postprocessor) run.FinalSqlCandidates.Add(new() { Sql = output });
            if (start == PipelineStage.Mapper && end == PipelineStage.Postprocessor)
            {
                run.FinalSqlCandidates.Add(new() { Sql = "SELECT 2" });
                run.Steps[^1].Output = "복원된 SQL 2개 · 복원 실패 0개";
                output = "SELECT 1\n\nSELECT 2";
            }
            var message = new ChatMessage
            {
                Sender = MessageSender.Assistant, DbPath = db, PipelineRun = run, Sql = run.FinalSql
            };
            session.Messages.Add(message);
            expected.Add(message.Id, output);
        }
        // The active UI range is still full. Each saved response must use its own recorded range.
        await history.SaveSessionsAsync(vm.Sessions);
        var restored = (await history.LoadSessionsAsync(assets)).Single(s => s.Id == session.Id);
        var partials = restored.Messages.Where(m => m.PipelineRun is { } run && run.Range != full).ToList();
        Require(partials.Count == 9 && partials.All(m => !m.IsFullPipelineRun && m.ShowStageDetails && m.ShowPipelineOutput &&
            !m.HasSqlCandidates && !m.CanExecuteSql && m.PipelineOutput == expected[m.Id]),
            "Every partial range keeps its plain-text output and offers stage details after reload");
        foreach (var message in partials)
        {
            var run = message.PipelineRun!;
            string details = message.StageDetails;
            Require(stages.All(stage => details.Contains("[" + ChatMessage.StageLabel(stage) + " ·") == run.Range.Contains(stage)) &&
                run.Steps.All(step => details.Contains(step.Input)) &&
                run.Steps.Where(step => step.Stage != PipelineStage.Postprocessor).All(step => details.Contains(step.Output)) &&
                run.FinalSqlCandidates.All(candidate => details.Contains(candidate.Sql)),
                $"Details for {run.Range} contain inputs and outputs only for the recorded stages");
        }
        var empty = new ChatMessage { Sender = MessageSender.Assistant, PipelineRun = new() { Range = full } };
        Require(!empty.ShowStageDetails && empty.StageDetails.Length == 0, "An empty run has no details to open");
        var final = restored.Messages.Single(m => m.PipelineRun?.Range == full);
        Require(final.IsFullPipelineRun && final.ShowStageDetails && !final.HasMapperError &&
            !final.ShowPipelineOutput && final.SqlCandidates.Any(c => c.CanExecuteSql) &&
            final.StageDetails.Contains("20 = [AGG_VALUE, numeric, #1]") &&
            final.StageDetails.Contains("20 red = [AGG_VALUE, numeric, #1]") &&
            final.PipelineRun!.FinalSqlCandidates.All(c => final.StageDetails.Contains(c.Error.Length > 0 ? c.Error : c.Sql)),
            "The full run retains its executable SQL and the complete set of mappings in its details");
        var replay = session.Messages.Last();
        int count = session.Messages.Count;
        await vm.RunSqlAsync(replay);
        Require(session.Messages.Count == count && replay.ResultMessageId == null,
            "A postprocessor-only reply cannot execute its stored SQL");
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
