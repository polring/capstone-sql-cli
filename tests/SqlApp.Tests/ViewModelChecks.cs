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
        var roles = new List<PipelineStage>();
        using var inference = new InferenceService();
        Task<InferenceResult> Predict(PipelineStage stage, string model, string input, int limit)
        {
            roles.Add(stage);
            Require(limit == 64, "Selected token budget");
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
        await history.SaveSessionsAsync(vm.Sessions);
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
