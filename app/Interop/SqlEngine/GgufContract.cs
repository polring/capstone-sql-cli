using System.Text;

namespace SqlApp.Interop.SqlEngine;

internal enum ModelRole { Single, Front, Back }

internal static class GgufContract
{
    private const string Prefix = "capstone_sql.";
    private const string Tokenizer = Prefix + "tokenizer.";
    internal const int MaxVocab = 131072;
    internal const int MaxTokenBytes = 1 << 20;

    private static readonly string[] RequiredFields =
    [
        "general.architecture", "general.alignment", Prefix + "vocab_size", Prefix + "dim",
        Prefix + "layers", Prefix + "heads", Prefix + "ffn_dim", Prefix + "context",
        Prefix + "eps", Prefix + "theta", Tokenizer + "seeds", Tokenizer + "merges",
        Tokenizer + "pad_id", Tokenizer + "bos_id", Tokenizer + "eos_id", Tokenizer + "sep_id",
        Tokenizer + "seed_base", Tokenizer + "merge_base"
    ];

    // Inspect only GGUF metadata when two filenames do not identify their roles.
    internal static ModelRole? TryReadModelRole(string path)
    {
        try
        {
            using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
            int count = ReadHeader(reader);
            ModelRole? role = null;
            for (int i = 0; i < count; i++)
            {
                string key = ReadString(reader);
                uint type = reader.ReadUInt32();
                if (key is Tokenizer + "output_ids" or Tokenizer + "sql_ids")
                {
                    if (role.HasValue || ReadIdArray(reader, type).Length == 0) return null;
                    role = key == Tokenizer + "output_ids" ? ModelRole.Front : ModelRole.Back;
                }
                else
                {
                    SkipField(reader, type);
                }
            }
            return role;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    internal static BpeCodec ReadTokenizer(string path, ModelRole role)
    {
        using var reader = new BinaryReader(File.OpenRead(path), Encoding.UTF8);
        int count = ReadHeader(reader);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var integers = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] seeds = [];
        string[]? frontNames = null;
        int[]? frontOutputIds = null;
        BpeMerge[] merges = [];
        int[]? sqlIds = null;
        for (int i = 0; i < count; i++)
        {
            string key = ReadString(reader);
            uint type = reader.ReadUInt32();
            if (!seen.Add(key)) throw new InvalidDataException($"중복 GGUF 메타데이터: {key}");
            switch (key)
            {
                case "general.architecture":
                    if (type != 8 || ReadString(reader) != "capstone_sql")
                        throw new InvalidDataException("지원하지 않는 GGUF 아키텍처입니다.");
                    break;
                case Tokenizer + "seeds":
                    seeds = ReadStringArray(reader, type);
                    break;
                case Tokenizer + "names" when role == ModelRole.Front:
                    frontNames = ReadStringArray(reader, type);
                    break;
                case Tokenizer + "output_ids" when role == ModelRole.Front:
                    frontOutputIds = ReadIdArray(reader, type);
                    break;
                case Tokenizer + "sql_ids":
                    sqlIds = ReadIdArray(reader, type);
                    break;
                case Tokenizer + "merges":
                    int entries = ReadArrayCount(reader, type, 4, MaxVocab * 3);
                    if (entries % 3 != 0) throw new InvalidDataException("GGUF merge 배열 길이가 올바르지 않습니다.");
                    merges = new BpeMerge[entries / 3];
                    for (int j = 0; j < merges.Length; j++)
                        merges[j] = new BpeMerge(ReadInteger(reader), ReadInteger(reader), ReadInteger(reader));
                    break;
                case Prefix + "eps" or Prefix + "theta":
                    if (type != 6) throw new InvalidDataException($"GGUF float32 값이 필요합니다: {key}");
                    reader.ReadSingle(); // Numerical constraints are validated by the compute engine.
                    break;
                case "general.alignment" or Prefix + "vocab_size" or
                     Prefix + "dim" or Prefix + "layers" or Prefix + "heads" or Prefix + "ffn_dim" or Prefix + "context" or
                     Tokenizer + "pad_id" or Tokenizer + "bos_id" or Tokenizer + "eos_id" or Tokenizer + "sep_id" or
                     Tokenizer + "seed_base" or Tokenizer + "merge_base":
                    if (type != 4) throw new InvalidDataException($"GGUF uint32 값이 필요합니다: {key}");
                    integers.Add(key, ReadInteger(reader));
                    break;
                default:
                    SkipField(reader, type);
                    break;
            }
        }
        foreach (string required in RequiredFields)
            if (!seen.Contains(required)) throw new InvalidDataException($"필수 GGUF 메타데이터가 없습니다: {required}");
        if (integers["general.alignment"] != 32) throw new InvalidDataException("32바이트 GGUF 정렬이 필요합니다.");
        var vocabulary = new BpeVocabulary(integers[Prefix + "vocab_size"], integers[Prefix + "context"],
            integers[Tokenizer + "pad_id"], integers[Tokenizer + "bos_id"],
            integers[Tokenizer + "eos_id"], integers[Tokenizer + "sep_id"],
            integers[Tokenizer + "seed_base"], integers[Tokenizer + "merge_base"], seeds, merges, sqlIds);
        BackModelTokenizer.ValidateVocabulary(vocabulary, role);
        return role switch
        {
            ModelRole.Single => new SingleModelTokenizer(vocabulary),
            ModelRole.Front => new FrontModelTokenizer(vocabulary,
                frontNames ?? throw new InvalidDataException("앞모델 GGUF에 tokenizer.names가 없습니다."),
                frontOutputIds ?? throw new InvalidDataException("앞모델 GGUF에 tokenizer.output_ids가 없습니다.")),
            ModelRole.Back => new BackModelTokenizer(vocabulary),
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };
    }

    private static int ReadHeader(BinaryReader reader)
    {
        if (reader.ReadUInt32() != 0x46554747 || reader.ReadUInt32() != 3)
            throw new InvalidDataException("GGUF v3 파일이 아닙니다.");
        reader.ReadUInt64(); // tensor count
        ulong count = reader.ReadUInt64();
        if (count > 1024) throw new InvalidDataException("GGUF 메타데이터 수가 너무 많습니다.");
        return (int)count;
    }

    private static int ReadArrayCount(BinaryReader reader, uint type, uint elementType, int maximum)
    {
        if (type != 9 || reader.ReadUInt32() != elementType)
            throw new InvalidDataException("GGUF 토크나이저 배열 형식이 올바르지 않습니다.");
        ulong count = reader.ReadUInt64();
        if (count > (ulong)maximum) throw new InvalidDataException("GGUF 토크나이저 배열이 너무 큽니다.");
        return (int)count;
    }

    private static string[] ReadStringArray(BinaryReader reader, uint type)
    {
        var values = new string[ReadArrayCount(reader, type, 8, MaxVocab)];
        for (int i = 0; i < values.Length; i++) values[i] = ReadString(reader);
        return values;
    }

    private static int[] ReadIdArray(BinaryReader reader, uint type)
    {
        var ids = new int[ReadArrayCount(reader, type, 4, MaxVocab)];
        for (int i = 0; i < ids.Length; i++) ids[i] = ReadInteger(reader);
        return ids;
    }

    private static int ReadInteger(BinaryReader reader)
    {
        uint value = reader.ReadUInt32();
        if (value > int.MaxValue) throw new InvalidDataException("GGUF 정수 값이 너무 큽니다.");
        return (int)value;
    }

    private static string ReadString(BinaryReader reader)
    {
        ulong size = reader.ReadUInt64();
        if (size > MaxTokenBytes || size > (ulong)(reader.BaseStream.Length - reader.BaseStream.Position))
            throw new InvalidDataException("잘못된 GGUF 문자열 길이입니다.");
        var bytes = reader.ReadBytes((int)size);
        if (bytes.Length != (int)size) throw new EndOfStreamException();
        if (Array.IndexOf(bytes, (byte)0) >= 0) throw new InvalidDataException("GGUF 문자열에 NUL 문자가 있습니다.");
        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipField(BinaryReader reader, uint type)
    {
        ulong count = 1;
        if (type == 9)
        {
            type = reader.ReadUInt32();
            count = reader.ReadUInt64();
        }
        var stream = reader.BaseStream;
        if (type == 8)
        {
            if (count > 131072) throw new InvalidDataException("잘못된 GGUF 문자열 배열입니다.");
            for (ulong i = 0; i < count; i++)
            {
                ulong length = reader.ReadUInt64();
                if (length > (ulong)(stream.Length - stream.Position)) throw new EndOfStreamException();
                stream.Seek((long)length, SeekOrigin.Current);
            }
            return;
        }
        int width = type switch
        {
            0 or 1 or 7 => 1, 2 or 3 => 2, 4 or 5 or 6 => 4, 10 or 11 or 12 => 8,
            _ => throw new InvalidDataException("지원하지 않는 GGUF 메타데이터 형식입니다.")
        };
        if (count > (ulong)((stream.Length - stream.Position) / width)) throw new EndOfStreamException();
        stream.Seek((long)count * width, SeekOrigin.Current);
    }
}
