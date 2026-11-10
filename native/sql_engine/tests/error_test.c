/* In-process rejection tests for the exported engine API. */
#include "../sql_engine.h"
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    unsigned char *data;
    size_t size;
} ByteBuffer;

typedef struct {
    const char *model_path;
    const char *work_dir;
    int passed;
    int failed;
} TestContext;

static ByteBuffer read_file(const char *path) {
    ByteBuffer buffer = {0};
    FILE *file = fopen(path, "rb");
    if (!file) {
        return buffer;
    }
    fseek(file, 0, SEEK_END);
    buffer.size = (size_t)ftell(file);
    fseek(file, 0, SEEK_SET);
    buffer.data = malloc(buffer.size + 1);
    if (!buffer.data || fread(buffer.data, 1, buffer.size, file) != buffer.size) {
        fprintf(stderr, "error: cannot read %s\n", path);
        exit(1);
    }
    buffer.data[buffer.size] = '\0';
    fclose(file);
    return buffer;
}

static void write_file(const char *path, const ByteBuffer *buffer) {
    FILE *file = fopen(path, "wb");
    if (!file || fwrite(buffer->data, 1, buffer->size, file) != buffer->size) {
        fprintf(stderr, "error: cannot write %s\n", path);
        exit(1);
    }
    fclose(file);
}

static ByteBuffer copy_buffer(const ByteBuffer *source) {
    ByteBuffer copy = {malloc(source->size + 1), source->size};
    if (!copy.data) {
        fprintf(stderr, "error: out of memory\n");
        exit(1);
    }
    memcpy(copy.data, source->data, source->size + 1);
    return copy;
}

/* Position right after the first occurrence of text at or after start. */
static size_t find_after(const ByteBuffer *buffer, const char *text, size_t start) {
    size_t length = strlen(text);
    for (size_t position = start; position + length <= buffer->size; position++) {
        if (!memcmp(buffer->data + position, text, length)) {
            return position + length;
        }
    }
    fprintf(stderr, "error: fixture model has no '%s'\n", text);
    exit(1);
}

static void put_uint32(ByteBuffer *buffer, size_t position, uint32_t value) {
    for (int index = 0; index < 4; index++) {
        buffer->data[position + index] = (unsigned char)(value >> (8 * index));
    }
}

static void put_uint64(ByteBuffer *buffer, size_t position, uint64_t value) {
    for (int index = 0; index < 8; index++) {
        buffer->data[position + index] = (unsigned char)(value >> (8 * index));
    }
}

static uint64_t get_uint64(const ByteBuffer *buffer, size_t position) {
    uint64_t value = 0;
    for (int index = 7; index >= 0; index--) {
        value = (value << 8) | buffer->data[position + index];
    }
    return value;
}

static uint32_t get_uint32(const ByteBuffer *buffer, size_t position) {
    uint32_t value = 0;
    for (int index = 3; index >= 0; index--) {
        value = (value << 8) | buffer->data[position + index];
    }
    return value;
}

/* A tensor descriptor follows its name: rank, rank dimensions, type, offset. */
static size_t descriptor_position(const ByteBuffer *buffer, const char *name) {
    return find_after(buffer, name, 0);
}

static void report(TestContext *context, int ok, const char *name) {
    if (ok) {
        context->passed++;
        return;
    }
    context->failed++;
    fprintf(stderr, "FAIL %s\n", name);
}

static void expect_file_rejected(TestContext *context, const char *name,
                                 const ByteBuffer *model, const char *message) {
    char path[512];
    snprintf(path, sizeof(path), "%s/invalid.gguf", context->work_dir);
    write_file(path, model);
    SqlModel *loaded = sql_model_open(path);
    report(context,
           !loaded && *sql_get_last_error() && strstr(sql_get_last_error(), message),
           name);
    sql_model_close(loaded);
}

static void test_generation_recovery(TestContext *context) {
    SqlModel *model = sql_model_open(context->model_path);
    SqlGeneration result;
    const char *bad[] = {"\xed\x95\x9c", "1111111111111111111111111111111"};
    for (size_t i = 0; i < sizeof(bad) / sizeof(*bad); ++i) {
        report(context,
               !sql_model_generate(model, bad[i], 1, &result) &&
                   *sql_get_last_error() && !result.data,
               "reject invalid input");
    }
    report(context, sql_model_generate(model, "hello", 1, &result),
           "retry after error");
    sql_generation_free(&result);
    sql_model_close(model);
}

static void test_eos_stop(TestContext *context, const ByteBuffer *fixture) {
    /* lm_head is the last tensor: zeroing it makes every logit tie, and the
     * tie policy picks EOS first. */
    ByteBuffer model = copy_buffer(fixture);
    size_t position = descriptor_position(&model, "lm_head.weight") + 4;
    size_t byte_count = (size_t)get_uint64(&model, position) *
                        (size_t)get_uint64(&model, position + 8) * sizeof(float);
    size_t padding = (32 - byte_count % 32) % 32;
    memset(model.data + model.size - padding - byte_count, 0, byte_count);
    char path[512];
    snprintf(path, sizeof(path), "%s/eos.gguf", context->work_dir);
    write_file(path, &model);
    SqlModel *engine = sql_model_open(path);
    SqlGeneration result;
    report(context,
           sql_model_generate(engine, "hello", 8, &result) &&
               !strcmp(result.stop_reason, "eos") && result.length == 0,
           "zero logits stop at eos");
    sql_generation_free(&result);
    sql_model_close(engine);
    free(model.data);
}

static void test_corrupted_files(TestContext *context, const ByteBuffer *fixture) {
    ByteBuffer model = copy_buffer(fixture);
    model.size -= 64;
    expect_file_rejected(context, "truncated file", &model, "");
    free(model.data);

    model = copy_buffer(fixture);
    put_uint32(&model, 4, 2);
    expect_file_rejected(context, "gguf version", &model, "");
    free(model.data);

    model = copy_buffer(fixture);
    size_t position = find_after(&model, "general.architecture", 0) + 20;
    position = find_after(&model, "capstone_sql", position) - strlen("capstone_sql");
    memcpy(model.data + position, "other_model!", 12);
    expect_file_rejected(context, "architecture", &model, "");
    free(model.data);

    size_t name_end = descriptor_position(fixture, "embedding.embedding.weight");
    uint32_t rank = get_uint32(fixture, name_end);
    model = copy_buffer(fixture);
    put_uint32(&model, name_end + 4 + rank * 8, 1);
    expect_file_rejected(context, "tensor type", &model, "");
    free(model.data);

    model = copy_buffer(fixture);
    put_uint64(&model, name_end + 4, 15);
    expect_file_rejected(context, "tensor shape", &model, "");
    free(model.data);

    model = copy_buffer(fixture);
    put_uint64(&model, name_end + 8 + rank * 8, (uint64_t)1 << 63);
    expect_file_rejected(context, "tensor offset", &model, "");
    free(model.data);

    /* The first tensor starts at offset zero; point the next one there too. */
    name_end = descriptor_position(fixture, "blocks.0.attn_norm.weight");
    rank = get_uint32(fixture, name_end);
    model = copy_buffer(fixture);
    put_uint64(&model, name_end + 8 + rank * 8, 0);
    expect_file_rejected(context, "overlapping tensors", &model,
                         "overlapping tensor data");
    free(model.data);
}

static void test_incompatible_config(TestContext *context, const ByteBuffer *fixture) {
    static const struct {
        const char *key;
        uint32_t value;
        const char *message;
    } cases[] = {
        {"capstone_sql.heads", 0, "invalid dimension"},
        {"capstone_sql.vocab_size", 255, "incomplete or incompatible config"},
        {"capstone_sql.dim", 15, "incomplete or incompatible config"},
        {"capstone_sql.heads", 16, "incomplete or incompatible config"},
        {"capstone_sql.layers", 257, "incomplete or incompatible config"},
        {"capstone_sql.context", 4097, "incomplete or incompatible config"},
    };

    for (size_t index = 0; index < sizeof(cases) / sizeof(cases[0]); index++) {
        ByteBuffer model = copy_buffer(fixture);
        /* A scalar value follows the key bytes and its four-byte GGUF type. */
        size_t position = find_after(&model, cases[index].key, 0) + 4;
        put_uint32(&model, position, cases[index].value);
        expect_file_rejected(context, cases[index].key, &model, cases[index].message);
        free(model.data);
    }
    /* merge[0].a sits after the array element type and element count. */
    const uint32_t bad_references[] = {256, 298, 300};
    for (size_t index = 0; index < 3; index++) {
        ByteBuffer model = copy_buffer(fixture);
        size_t position = find_after(&model, "capstone_sql.tokenizer.merges", 0) + 16;
        put_uint32(&model, position, bad_references[index]);
        expect_file_rejected(context, "merge reference", &model,
                             "invalid merge references");
        free(model.data);
    }
}

static void test_tokenizer_layout(TestContext *context, const ByteBuffer *fixture) {
    /* The base fixture uses PAD..SEP = 256..259, seeds from 260 and merges from
     * 298 (12 merges, vocab 320). Each value breaks one layout rule. */
    static const struct {
        const char *key;
        uint32_t value;
    } cases[] = {
        {"capstone_sql.tokenizer.pad_id", 0},       /* inside byte IDs */
        {"capstone_sql.tokenizer.bos_id", 258},     /* duplicates EOS */
        {"capstone_sql.tokenizer.sep_id", 260},     /* inside seed IDs */
        {"capstone_sql.tokenizer.merge_base", 320}, /* merges exceed vocab */
        {"capstone_sql.tokenizer.seed_base", 400},  /* beyond merge_base */
    };

    for (size_t index = 0; index < sizeof(cases) / sizeof(cases[0]); index++) {
        ByteBuffer model = copy_buffer(fixture);
        size_t position = find_after(&model, cases[index].key, 0) + 4;
        put_uint32(&model, position, cases[index].value);
        expect_file_rejected(context, cases[index].key, &model,
                             "invalid tokenizer layout");
        free(model.data);
    }
    ByteBuffer model = copy_buffer(fixture);
    put_uint32(&model, find_after(&model, "capstone_sql.contract_version", 0) + 4, 1);
    expect_file_rejected(context, "contract v1", &model, "re-export as version 2");
    free(model.data);
}

int main(int argc, char **argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: %s MODEL.gguf WORK_DIR\n", argv[0]);
        return 2;
    }
    TestContext context = {argv[1], argv[2], 0, 0};
    ByteBuffer fixture = read_file(context.model_path);
    if (!fixture.data) {
        fprintf(stderr, "error: cannot open %s\n", context.model_path);
        return 1;
    }
    test_generation_recovery(&context);
    test_eos_stop(&context, &fixture);
    test_corrupted_files(&context, &fixture);
    test_incompatible_config(&context, &fixture);
    test_tokenizer_layout(&context, &fixture);
    printf("error cases: %d passed, %d failed\n", context.passed, context.failed);
    free(fixture.data);
    return context.failed || !context.passed;
}
