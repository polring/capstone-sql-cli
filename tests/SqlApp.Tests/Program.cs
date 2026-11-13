using System.Runtime.InteropServices;
using System.Text.Json;
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
    string single = Directory.CreateDirectory(Path.Combine(temp.FullName, "single")).FullName;
    string dual = Directory.CreateDirectory(Path.Combine(temp.FullName, "dual")).FullName;
    string db = Path.Combine(dual, "shop.db");
    var singleModel = Path.Combine(single, "custom.gguf");
    TestModel.Write(singleModel, "SELECT 1");
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
    File.Copy(singleModel, back);
    var bundle = AssetDiscoveryService.AddFolder(assets, dual)!;
    Require(bundle.IsDual && bundle.FrontModelPath == "" && bundle.BackModelPath == back, "Back role discovery");
    string front = Path.Combine(dual, "front.gguf");
    File.Copy(singleModel, front);
    bundle = AssetDiscoveryService.AddFolder(assets, dual)!;
    Require(bundle.FrontModelPath == front && assets.Count == 2, "Refresh a dual bundle");

    var sqlite = new SqlExecutionService();
    await SqlQueryChecks.VerifyPreparationAsync(sqlite, db);
    var schema = await sqlite.GetDatabaseSchemaAsync(db);
    var priceSchema = new[] { new TableSchemaInfo { TableName = "items", Columns = ["item_name (TEXT)", "price (REAL)"] } };
    var priceMap = Mapper.MapCandidates("products priced between 20 dollars and 10 dollars",
        "{items.item_name, string} priced between [items.price, numeric, #1] and [items.price, numeric, #2]",
        priceSchema).Candidates.Single();
    string interval = Postprocessor.Restore("SELECT {TABLE1.COL1, string} FROM <TABLE1> WHERE <TABLE1.COL2, numeric> BETWEEN [TABLE1.COL2, numeric, #1] AND [TABLE1.COL2, numeric, #2]", priceMap, priceSchema);
    Require(interval.EndsWith("BETWEEN 10 AND 20"), "Restore numeric units and sort BETWEEN bounds");
    await CandidatePipelineChecks.RunAsync(dual, db, schema);

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

    var nativeName = OperatingSystem.IsWindows() ? "sql_engine.dll" : OperatingSystem.IsMacOS() ? "libsql_engine.dylib" : "libsql_engine.so";
    var nativePath = Path.Combine(root.FullName, "app", "runtimes", RuntimeInformation.RuntimeIdentifier, "native", nativeName);
    NativeLibrary.SetDllImportResolver(typeof(InferenceService).Assembly, (name, _, _) =>
        name == "sql_engine" ? NativeLibrary.Load(nativePath) : IntPtr.Zero);
    await SqlQueryChecks.VerifyChatAsync(temp.FullName, db);
    await ViewModelChecks.RunAsync(temp.FullName, assets);
    using var inference = new InferenceService();
    Require(inference.GetStageUnavailableReason(PipelineStage.Front, "") != null, "Missing model availability");
    Console.WriteLine("Verified asset discovery, session migration, stage selection, candidate mapping, SQL validation and execution.");
}
finally
{
    SqliteConnection.ClearAllPools();
    temp.Delete(true);
}
