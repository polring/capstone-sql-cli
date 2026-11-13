using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SqlApp.Interop.SqlEngine;
using SqlApp.Models;
using SqlApp.Services;
using SqlApp.Services.Pipeline;
using SqlApp.ViewModels;

static void Require(bool ok, string message)
{
    if (!ok) throw new InvalidOperationException(message);
}

static async Task VerifyAutomaticBundlesAsync(string tempRoot, string frontModel)
{
    string assetsRoot = Path.Combine(tempRoot, "assets");
    string Folder(string name, params string[] files)
    {
        string folder = Directory.CreateDirectory(Path.Combine(assetsRoot, name)).FullName;
        foreach (string file in files) File.WriteAllBytes(Path.Combine(folder, file), []);
        return folder;
    }

    Folder("stage900", "shop.db", "model.gguf");
    string single = Folder("z-single", "Catalog.DB", "front.GGUF", "Catalog.DB-wal");
    Folder("stage800", "shop.db", "back.gguf", "front.gguf");
    Folder("generic-pair", "schema.db", "a.gguf", "b.gguf");
    string dual = Folder("z-dual", "other.Db", "first.GgUf", "second.gguf");
    File.Copy(frontModel, Path.Combine(dual, "second.gguf"), overwrite: true);
    Folder("no-db", "front.gguf", "back.gguf");
    Folder("no-model", "shop.db");
    Folder("extra-db", "shop.db", "other.db", "model.gguf");
    Folder("extra-model", "shop.db", "front.gguf", "back.gguf", "third.gguf");

    var bundles = AssetDiscoveryService.DiscoverBundles(assetsRoot);
    Require(bundles.Count == 5 && bundles.Count(b => b.IsDual) == 3,
        "Auto-discovery requires exactly one DB and one or two GGUF files");
    var one = bundles.Single(b => b.DirectoryPath == single);
    Require(!one.IsDual && one.ModelPath == Path.Combine(single, "front.GGUF") &&
        one.DbPath == Path.Combine(single, "Catalog.DB"), "A lone model is single regardless of its name");
    var two = bundles.Single(b => b.DirectoryPath == dual);
    Require(two.IsDual && two.DbPath == Path.Combine(dual, "other.Db") &&
        two.FrontModelPath == Path.Combine(dual, "second.gguf") && two.BackModelPath == Path.Combine(dual, "first.GgUf"),
        "A front GGUF with a custom name keeps its role even when it sorts last");
    var named = bundles.Single(b => b.Name == "stage800");
    Require(Path.GetFileName(named.FrontModelPath) == "front.gguf" && Path.GetFileName(named.BackModelPath) == "back.gguf",
        "Explicit front and back filenames preserve their roles");
    var generic = bundles.Single(b => b.Name == "generic-pair");
    Require(new HashSet<string> { Path.GetFileName(generic.FrontModelPath), Path.GetFileName(generic.BackModelPath) }
        .SetEquals(["a.gguf", "b.gguf"]), "Two generic names still qualify and use both model files");
    var ascending = bundles.OrderBy(b => b.Name, StringComparer.Ordinal);
    Require(AssetDiscoveryService.GetDefaultBundle(ascending, false) == one &&
        AssetDiscoveryService.GetDefaultBundle(ascending, true) == two, "Folder name takes precedence over stage numbers");

    using var inference = new InferenceService();
    var history = new SessionStorageService(Path.Combine(tempRoot, "auto-history"));
    var vm = new MainViewModel(ascending.ToList(), history, inference);
    await vm.Initialization;
    Require(vm.CurrentSessionBundle == one, "A new chat selects the alphabetically last single bundle");
    vm.SetDualMode(true);
    Require(vm.CurrentSessionBundle == two, "Switching to dual mode selects the alphabetically last dual bundle");
    vm.SelectBundle(named);
    await history.SaveSessionsAsync(vm.Sessions);
    Require((await history.LoadSessionsAsync(bundles)).Single().Bundle == named,
        "Reopening a chat restores its chosen folder even when another is the default");
}

static void VerifyFrontTokenizer(string model)
{
    // Reference IDs from mapper feat/#4-natural-questions (dc640bf), using this bundled model.
    var tokenizer = GgufContract.ReadTokenizer(model, ModelRole.Front);
    Require(tokenizer.EncodePrompt("date #1").AsSpan().SequenceEqual([257, 398, 32, 35, 49, 259]),
        "Front questions apply word BPE to date and preserve #1 as two bytes");
    Require(tokenizer.Encode("café\0😀").AsSpan().SequenceEqual([99, 486, 195, 169, 0, 240, 159, 152, 128]),
        "Front questions preserve UTF-8 bytes and NUL");
    Require(tokenizer.Encode("<customers>").AsSpan().SequenceEqual([60, 489, 62]) &&
        tokenizer.Decode([60, 267, 62]).AsSpan().SequenceEqual("<customers>"u8),
        "Schema names use word BPE in questions and dedicated IDs inside output tags");

    const string outputChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ 0123456789.,?'-<>{}[]*";
    var expected = outputChars.Select(c => (int)c).ToHashSet();
    expected.UnionWith(Enumerable.Range(260, 623)); // Model's 7 fixed atoms, 16 names and 600 merges
    Require(tokenizer.EosId == 258 && Enumerable.Range(0, tokenizer.VocabSize)
        .All(id => (tokenizer.DecodableMask[id] != 0) == expected.Contains(id)),
        "Every front output ID matches mapper; native generation handles EOS separately");
}

var root = new DirectoryInfo(AppContext.BaseDirectory);
while (!File.Exists(Path.Combine(root.FullName, "SqlApp.sln")))
    root = root.Parent ?? throw new DirectoryNotFoundException("SqlApp.sln");
var temp = Directory.CreateDirectory(Path.Combine(root.FullName, "build", "tests", Guid.NewGuid().ToString("N")));
try
{
    string frontModel = Path.Combine(root.FullName, "assets", "dual-model-test", "front.gguf");
    await VerifyAutomaticBundlesAsync(temp.FullName, frontModel);
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
    File.Copy(frontModel, front);
    VerifyFrontTokenizer(front);
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
    await JsonBatchChecks.RunAsync(temp.FullName, assets);
    using var inference = new InferenceService();
    Require(inference.GetStageUnavailableReason(PipelineStage.Front, "") != null, "Missing model availability");
    Console.WriteLine("Verified asset discovery, session migration, stage selection, candidate mapping, SQL validation and execution, and JSON batch inference and evaluation.");
}
finally
{
    SqliteConnection.ClearAllPools();
    temp.Delete(true);
}
