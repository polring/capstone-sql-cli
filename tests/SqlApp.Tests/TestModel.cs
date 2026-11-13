using System.Text;

internal static class TestModel
{
    // A synthetic F32 byte-BPE decoder. The zero transformer block preserves its
    // input embedding: the head maps SEP -> one output seed -> EOS. No trained data.
    internal static void Write(string path, string seed)
    {
        const int vocab = 261, dimension = 2, eos = 258, separator = 259, output = 260;
        float[] embedding = new float[vocab * dimension], head = new float[vocab * dimension];
        embedding[separator * dimension] = head[output * dimension] = 1;
        embedding[output * dimension + 1] = head[eos * dimension + 1] = 1;
        (string Name, int[] Shape, float[] Data)[] tensors =
        [
            ("embedding.embedding.weight", [dimension, vocab], embedding),
            ("blocks.0.attn_norm.weight", [dimension], [1, 1]),
            ("blocks.0.ffn_norm.weight", [dimension], [1, 1]),
            ("blocks.0.attn.qkv_proj.weight", [2, 6], new float[12]),
            ("blocks.0.attn.out_proj.weight", [2, 2], new float[4]),
            ("blocks.0.ffn.w1.weight", [2, 2], new float[4]),
            ("blocks.0.ffn.w3.weight", [2, 2], new float[4]),
            ("blocks.0.ffn.w2.weight", [2, 2], new float[4]),
            ("final_norm.weight", [dimension], [1, 1]),
            ("lm_head.weight", [dimension, vocab], head)
        ];

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("GGUF"u8);
        writer.Write(3u);
        writer.Write((ulong)tensors.Length);
        writer.Write(18UL);

        StringField("general.architecture", "capstone_sql");
        UIntField("general.alignment", 32);
        UIntField("capstone_sql.vocab_size", vocab);
        UIntField("capstone_sql.dim", dimension);
        UIntField("capstone_sql.layers", 1);
        UIntField("capstone_sql.heads", 1);
        UIntField("capstone_sql.ffn_dim", 2);
        UIntField("capstone_sql.context", 256);
        FloatField("capstone_sql.eps", 1e-5f);
        FloatField("capstone_sql.theta", 10000f);
        UIntField("capstone_sql.tokenizer.pad_id", 256);
        UIntField("capstone_sql.tokenizer.bos_id", 257);
        UIntField("capstone_sql.tokenizer.eos_id", eos);
        UIntField("capstone_sql.tokenizer.sep_id", separator);
        UIntField("capstone_sql.tokenizer.seed_base", output);
        UIntField("capstone_sql.tokenizer.merge_base", vocab);
        Header("capstone_sql.tokenizer.seeds", 9); writer.Write(8u); writer.Write(1UL); String(seed);
        Header("capstone_sql.tokenizer.merges", 9); writer.Write(4u); writer.Write(0UL);

        long offset = 0;
        foreach (var tensor in tensors)
        {
            String(tensor.Name);
            writer.Write((uint)tensor.Shape.Length);
            foreach (int axis in tensor.Shape) writer.Write((ulong)axis);
            writer.Write(0u); // GGML_TYPE_F32
            writer.Write((ulong)offset);
            offset = Align(offset + tensor.Data.Length * sizeof(float));
        }
        foreach (var tensor in tensors)
        {
            while (writer.BaseStream.Position != Align(writer.BaseStream.Position)) writer.Write((byte)0);
            foreach (float value in tensor.Data) writer.Write(value);
        }

        static long Align(long bytes) => (bytes + 31) / 32 * 32;
        void String(string value)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
        void Header(string key, uint type) { String(key); writer.Write(type); }
        void UIntField(string key, uint value) { Header(key, 4); writer.Write(value); }
        void FloatField(string key, float value) { Header(key, 6); writer.Write(value); }
        void StringField(string key, string value) { Header(key, 8); String(value); }
    }
}
