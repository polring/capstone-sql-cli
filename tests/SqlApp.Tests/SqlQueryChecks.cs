using Microsoft.Data.Sqlite;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.ViewModels;

internal static class SqlQueryChecks
{
    internal static async Task VerifyPreparationAsync(SqlExecutionService service, string db)
    {
        const string withQuery = "WITH names AS (SELECT item_name FROM items) SELECT * FROM names;";
        const string quoted = "SELECT '문자;열' AS [값;열]; /* ; */ -- final ;";
        foreach (string sql in new[] { withQuery, quoted, "VALUES (1), (2)" })
            Require(await service.ValidateQueryAsync(db, sql) == null, $"Valid SQLite query rejected: {sql}");
        var actual = await service.ExecuteQueryAsync(db, quoted);
        Require(actual.ErrorMessage == null && actual.Rows.Single().Single() == "문자;열",
            "Execute the complete statement with quoted semicolons and trailing comments");

        foreach (string sql in new[]
        {
            "-- empty", "SELECT item_name FROM items WHERE", "SELECT missing_column FROM items",
            "SELECT 1; SELECT 2", "SELECT 1; /* trailing */ broken",
            "UPDATE items SET category = category WHERE 0"
        })
        {
            Require(!string.IsNullOrEmpty(await service.ValidateQueryAsync(db, sql)),
                $"Validation accepted invalid or non-query SQL: {sql}");
            Require(!string.IsNullOrEmpty((await service.ExecuteQueryAsync(db, sql)).ErrorMessage),
                $"Execution accepted rejected SQL: {sql}");
        }

        const string overflow = "SELECT abs(-9223372036854775808)";
        Require(await service.ValidateQueryAsync(db, overflow) == null, "Validation must not evaluate a query");
        Require((await service.ExecuteQueryAsync(db, overflow)).ErrorMessage != null, "The error occurs at execution");
    }

    internal static async Task VerifyChatAsync(string tempRoot, string sourceDb)
    {
        const string valid = "SELECT item_name FROM items";
        const string invalid = "SELECT item_name FROM items WHERE";
        var bundles = new List<AssetBundle>();
        AssetBundle MakeBundle(string name, string output)
        {
            string folder = Directory.CreateDirectory(Path.Combine(tempRoot, name)).FullName;
            TestModel.Write(Path.Combine(folder, "model.gguf"), output);
            File.Copy(sourceDb, Path.Combine(folder, "shop.db"));
            return AssetDiscoveryService.AddFolder(bundles, folder)!;
        }
        var validBundle = MakeBundle("partial-valid", valid);
        var invalidBundle = MakeBundle("partial-invalid", invalid);
        var history = new SessionStorageService(Path.Combine(tempRoot, "sql-validation-history"));
        using var inference = new InferenceService();
        var vm = new MainViewModel(bundles, history, inference);
        await vm.Initialization;
        var session = vm.SelectedSession!;
        async Task<ChatMessage> Send(AssetBundle bundle, int limit, string input = "hello")
        {
            vm.SelectBundle(bundle);
            session.MaxTokens = limit;
            vm.InputText = input;
            await vm.SendMessageAsync();
            return session.Messages.Last();
        }

        var partial = await Send(validBundle, 1);
        Require(partial.Sql == valid && partial.StopReason == "max_new_tokens" &&
            partial.HasGenerationWarning && partial.GenerationWarning.Contains("Max Tokens") && partial.CanExecuteSql,
            "Show truncated SQL and its limit notice; a valid prefix remains executable");
        var validRow = partial.SqlCandidates.Single();
        Require(validRow.Sql == valid && validRow.CanExecuteSql && !validRow.HasValidationError,
            "A single-model response exposes the same validated candidate row as a dual result");
        Require(partial.ResultMessageId == null, "Validation must not automatically run a query");
        await vm.RunSqlAsync(partial);
        Require(session.Messages.Single(m => m.Id == partial.ResultMessageId).QueryResult?.Rows.Single().Single() == "cup",
            "Execute a valid truncated query on explicit request");

        var rejected = await Send(invalidBundle, 1);
        Require(rejected.Sql == invalid && rejected.HasGenerationWarning && rejected.HasSqlValidationError &&
            !rejected.CanExecuteSql, "Preserve an invalid partial query and show both notices");
        var invalidRow = rejected.SqlCandidates.Single();
        Require(invalidRow.Sql == invalid && !invalidRow.CanExecuteSql && invalidRow.HasValidationError &&
            invalidRow.ValidationLabel != validRow.ValidationLabel, "Show distinct validation statuses even for a single SQL candidate");
        await vm.RunSqlAsync(rejected);
        Require(rejected.ResultMessageId == null, "Disabled SQL cannot be executed via the command");
        var complete = await Send(invalidBundle, 2);
        Require(complete.StopReason == "eos" && complete.Sql == invalid && !complete.HasGenerationWarning &&
            complete.HasSqlValidationError && !complete.CanExecuteSql, "EOS does not imply valid SQL");
        var empty = await Send(validBundle, 1, new string('1', 254));
        Require(empty.StopReason == "context" && empty.HasGenerationWarning && !empty.HasSql && !empty.CanExecuteSql,
            "A full prompt context shows its notice even when no output token fits");

        await history.SaveSessionsAsync(vm.Sessions);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               { DataSource = validBundle.DbPath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var schemaChange = connection.CreateCommand();
            schemaChange.CommandText = "ALTER TABLE items RENAME TO renamed_items";
            schemaChange.ExecuteNonQuery();
        }
        var restored = (await history.LoadSessionsAsync(bundles)).Single().Messages.Single(m => m.Id == partial.Id);
        Require(restored.Sql == valid && restored.HasGenerationWarning && restored.HasSqlValidationError &&
            !restored.CanExecuteSql, "Reopen partial output and revalidate it against the current schema");
        await vm.RunSqlAsync(partial);
        Require(!partial.CanExecuteSql && partial.HasSqlValidationError &&
            session.Messages.Single(m => m.Id == partial.ResultMessageId).QueryResult?.HasError == true,
            "A schema change after generation is caught on execution");
        await history.SaveSessionsAsync(vm.Sessions);
    }

    private static void Require(bool ok, string message)
    {
        if (!ok) throw new InvalidOperationException(message);
    }
}
