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

static void test_forward_recovery(TestContext *context) {
    /* fixtures/v2/base has context=32 and vocab_size=320. */
    SqlModel *model = sql_model_open(context->model_path);
    int32_t ids[] = {257, 100};
    float logits[320];
    double elapsed_ms = -1;
    report(context, model != NULL, "open model");
    if (!model) {
        return;
    }
    report(context,
           !sql_model_forward(model, ids, 0, logits, 320, &elapsed_ms) &&
               elapsed_ms == 0,
           "empty prefix");
    report(context, !sql_model_forward(model, ids, 33, logits, 320, &elapsed_ms),
           "prefix exceeds context");
    report(context, !sql_model_forward(model, ids, 2, logits, 319, &elapsed_ms),
           "undersized logits buffer");
    report(context, !sql_model_forward(model, NULL, 2, logits, 320, &elapsed_ms),
           "missing input IDs");
    report(context, !sql_model_forward(model, ids, 2, NULL, 320, &elapsed_ms),
           "missing output buffer");
    report(context, !sql_model_forward(model, ids, 2, logits, 320, NULL),
           "missing time output");

    const int32_t invalid_ids[] = {-1, 320};
    for (size_t i = 0; i < sizeof(invalid_ids) / sizeof(*invalid_ids); i++) {
        report(
            context,
            !sql_model_forward(model, &invalid_ids[i], 1, logits, 320, &elapsed_ms) &&
                strstr(sql_get_last_error(), "outside vocabulary") && elapsed_ms == 0,
            "invalid token ID");
    }
    report(context,
           sql_model_forward(model, ids, 2, logits, 320, &elapsed_ms) &&
               elapsed_ms >= 0 && !*sql_get_last_error(),
           "valid forward after error");
    sql_model_close(model);
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
    /* The string value starts after its uint32 type and uint64 length. */
    size_t position = find_after(&model, "general.architecture", 0) + 4 + 8;
    memcpy(model.data + position, "other_model!", 12);
    expect_file_rejected(context, "architecture", &model, "unsupported architecture");
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

static void test_generation_recovery(TestContext *context) {
    SqlModel *model = sql_model_open(context->model_path);
    if (!model) {
        report(context, 0, "open generation model");
        return;
    }
    int32_t prompt[32] = {257}, output[32], count = -1, reason = -1;
    uint8_t mask[320] = {0};
    double elapsed_ms = -1;

    static const struct {
        int prompt_count, mask_length, eos, limit, capacity;
    } cases[] = {{0, 320, 258, 1, 1}, {33, 320, 258, 1, 1}, {1, 319, 258, 1, 1},
                 {1, 320, 320, 1, 1}, {1, 320, 258, -1, 0}, {1, 320, 258, 8, 7}};

    for (size_t i = 0; i < sizeof(cases) / sizeof(*cases); ++i) {
        report(context,
               !sql_model_generate_ids(model, prompt, cases[i].prompt_count, mask,
                                       cases[i].mask_length, cases[i].eos,
                                       cases[i].limit, output, cases[i].capacity,
                                       &count, &reason, &elapsed_ms) &&
                   count == 0 && elapsed_ms == 0 && *sql_get_last_error(),
               "bad generation arguments");
    }
    prompt[0] = -1;
    report(context,
           !sql_model_generate_ids(model, prompt, 1, mask, 320, 258, 0, NULL, 0, &count,
                                   &reason, &elapsed_ms),
           "invalid prompt ID with zero limit");
    prompt[0] = 257;
    report(context,
           sql_model_generate_ids(model, prompt, 1, mask, 320, 258, 4, output, 4,
                                  &count, &reason, &elapsed_ms) &&
               reason == SQL_STOP_EOS && count == 0 && elapsed_ms > 0 &&
               !*sql_get_last_error(),
           "EOS-only mask after failed call");
    report(context,
           sql_model_generate_ids(model, prompt, 32, mask, 320, 258, 1, NULL, 0, &count,
                                  &reason, &elapsed_ms) &&
               reason == SQL_STOP_CONTEXT && count == 0 && elapsed_ms == 0,
           "full prompt skips cache creation");
    report(context,
           sql_model_generate_ids(model, prompt, 32, mask, 320, 258, 0, NULL, 0, &count,
                                  &reason, &elapsed_ms) &&
               reason == SQL_STOP_MAX_NEW_TOKENS && count == 0 && elapsed_ms == 0,
           "zero limit wins over full context");
    sql_model_close(model);
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
    ByteBuffer model = copy_buffer(fixture);
    const char *key = "general.alignment";
    model.data[find_after(&model, key, 0) - strlen(key)] = 'x';
    expect_file_rejected(context, "missing required alignment", &model,
                         "incomplete or incompatible config");
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
    test_forward_recovery(&context);
    test_generation_recovery(&context);
    test_corrupted_files(&context, &fixture);
    test_incompatible_config(&context, &fixture);
    printf("error cases: %d passed, %d failed\n", context.passed, context.failed);
    free(fixture.data);
    return context.failed || !context.passed;
}
