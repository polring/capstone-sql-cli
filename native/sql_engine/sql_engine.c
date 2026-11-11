/* capstone_sql F32 decoder for token IDs, with a per-generation KV cache. */
#if !defined(_WIN32) && !defined(_POSIX_C_SOURCE)
#define _POSIX_C_SOURCE 200809L
#endif
#include "sql_engine.h"
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#define GGUF_VERSION 3
#define CONTRACT_VERSION 2
#define CONTRACT_VERSION_TEXT "2"

typedef struct {
    char *name;
    uint32_t rank;
    size_t dimensions[2];
    size_t element_count;
    uint64_t data_offset;
    float *values;
} Tensor;

typedef struct {
    int vocab_size;
    int embedding_dim;
    int layer_count;
    int head_count;
    int ffn_dim;
    int context_length;
    float norm_epsilon;
    float rope_theta;
} ModelConfig;

typedef struct SqlModel {
    ModelConfig config;
    Tensor *tensors;
    size_t tensor_count;
} Model;

#define MAX_ALLOCATION_BYTES ((size_t)1024 * 1024 * 1024)
#define MAX_STRING_BYTES 1048576
#define MAX_DIMENSION 131072
#define MAX_LAYERS 256
#define MAX_CONTEXT_LENGTH 4096
#define MAX_TENSOR_COUNT 10000
#define GGUF_ALIGNMENT 32
#define MAX_METADATA_COUNT 1024

typedef enum {
    GGUF_UINT32 = 4,
    GGUF_FLOAT32 = 6,
    GGUF_STRING = 8,
    GGUF_ARRAY = 9
} GgufMetadataType;

typedef enum {
    METADATA_ARCHITECTURE = 1u << 0,
    METADATA_ALIGNMENT = 1u << 1,
    METADATA_CONTRACT_VERSION = 1u << 2,
    METADATA_EPSILON = 1u << 3,
    METADATA_ROPE_THETA = 1u << 4,
    METADATA_FIRST_DIMENSION = 1u << 5,
    METADATA_COMPUTE_FIELDS = (1u << 11) - 1
} MetadataField;

typedef struct {
    unsigned char *data;
    size_t size;
    size_t position;
} BinaryReader;

typedef struct {
    float *hidden_states;
    float *normalized_states;
    float *qkv_states;
    float *attention_output;
    float *temporary_vector;
    float *ffn_gate;
    float *ffn_up;
    float *attention_scores;
} InferenceWorkspace;

typedef struct {
    const float *attention_norm;
    const float *qkv;
    const float *attention_output;
    const float *ffn_norm;
    const float *ffn_gate;
    const float *ffn_up;
    const float *ffn_down;
} DecoderWeights;

/* ------------------------- Memory and binary reading ---------------------- */

#include <setjmp.h>
#if defined(_WIN32)
#include <windows.h>
static __declspec(thread) jmp_buf *s_current_bailout = NULL;
static __declspec(thread) char s_last_error_message[512] = "";
#else
static _Thread_local jmp_buf *s_current_bailout = NULL;
static _Thread_local char s_last_error_message[512] = "";
#endif

SQL_API const char *sql_get_last_error(void) {
    return s_last_error_message;
}

static void sql_set_last_error(const char *message) {
    if (message) {
        strncpy(s_last_error_message, message, sizeof(s_last_error_message) - 1);
        s_last_error_message[sizeof(s_last_error_message) - 1] = '\0';
    } else {
        s_last_error_message[0] = '\0';
    }
}

#ifdef _WIN32
static FILE *open_file_utf8(const char *path, const char *mode) {
    if (!path || !mode) {
        return NULL;
    }
    int wlen_path = MultiByteToWideChar(CP_UTF8, 0, path, -1, NULL, 0);
    int wlen_mode = MultiByteToWideChar(CP_UTF8, 0, mode, -1, NULL, 0);
    if (wlen_path > 0 && wlen_mode > 0) {
        wchar_t *wpath = (wchar_t *)malloc(wlen_path * sizeof(wchar_t));
        wchar_t *wmode = (wchar_t *)malloc(wlen_mode * sizeof(wchar_t));
        if (wpath && wmode) {
            MultiByteToWideChar(CP_UTF8, 0, path, -1, wpath, wlen_path);
            MultiByteToWideChar(CP_UTF8, 0, mode, -1, wmode, wlen_mode);
            FILE *f = _wfopen(wpath, wmode);
            free(wpath);
            free(wmode);
            return f;
        }
        free(wpath);
        free(wmode);
    }
    return fopen(path, mode);
}
#else
static FILE *open_file_utf8(const char *path, const char *mode) {
    return fopen(path, mode);
}
#endif

static void fail(const char *message) {
    sql_set_last_error(message);
    if (s_current_bailout) {
        longjmp(*s_current_bailout, 1);
    }
    fprintf(stderr, "error: %s\n", message);
    exit(1);
}

/* Allocations made while loading a model or computing logits
 * are linked until the call succeeds. Error unwinding also frees temporaries
 * that have not yet been installed in a model/workspace. */
typedef struct Allocation {
    struct Allocation **link;
    struct Allocation *next;
} Allocation;

#ifdef _WIN32
static __declspec(thread) Allocation *pending;
#else
static _Thread_local Allocation *pending;
#endif

static void unlink_allocation(Allocation *block) {
    if (block->link) {
        *block->link = block->next;
        if (block->next) {
            block->next->link = block->link;
        }
        block->link = NULL;
        block->next = NULL;
    }
}

static void free_array(void *ptr) {
    if (ptr) {
        Allocation *block = (Allocation *)ptr - 1;
        unlink_allocation(block);
        free(block);
    }
}

static void finish_call(int success) {
    while (pending) {
        Allocation *block = pending;
        unlink_allocation(block);
        if (!success) {
            free(block);
        }
    }
    s_current_bailout = NULL;
}

static void *try_allocate(size_t bytes) {
    Allocation *block = calloc(1, sizeof(*block) + bytes);
    if (!block) {
        return NULL;
    }
    if (s_current_bailout) {
        block->link = &pending;
        block->next = pending;
        if (pending) {
            pending->link = &block->next;
        }
        pending = block;
    }
    return block + 1;
}

static void *allocate_array(size_t count, size_t element_size) {
    if (element_size && count > MAX_ALLOCATION_BYTES / element_size) {
        fail("allocation exceeds 1 GiB baseline limit");
    }
    void *allocation = try_allocate(count * element_size);
    if (!allocation) {
        fail("out of memory");
    }
    return allocation;
}

static size_t checked_product(size_t left, size_t right) {
    if (right && left > MAX_ALLOCATION_BYTES / right) {
        fail("tensor or workspace too large");
    }
    return left * right;
}

static unsigned char *read_bytes(BinaryReader *reader, size_t byte_count) {
    if (byte_count > reader->size - reader->position) {
        fail("truncated GGUF");
    }
    unsigned char *bytes = reader->data + reader->position;
    reader->position += byte_count;
    return bytes;
}

static uint32_t read_uint32(BinaryReader *reader) {
    unsigned char *bytes = read_bytes(reader, 4);
    /* GGUF integers are little endian regardless of the host's byte order. */
    return (uint32_t)bytes[0] | ((uint32_t)bytes[1] << 8) | ((uint32_t)bytes[2] << 16) |
           ((uint32_t)bytes[3] << 24);
}

static uint64_t read_uint64(BinaryReader *reader) {
    uint64_t low_bits = read_uint32(reader);
    return low_bits | ((uint64_t)read_uint32(reader) << 32);
}

static float read_float32(BinaryReader *reader) {
    uint32_t bits = read_uint32(reader);
    float value;
    memcpy(&value, &bits, sizeof(value));
    return value;
}

static char *read_string(BinaryReader *reader) {
    uint64_t byte_count = read_uint64(reader);
    if (byte_count > MAX_STRING_BYTES) {
        fail("GGUF string too long");
    }
    char *text = allocate_array((size_t)byte_count + 1, 1);
    memcpy(text, read_bytes(reader, (size_t)byte_count), (size_t)byte_count);
    if (memchr(text, 0, (size_t)byte_count)) {
        fail("embedded NUL in metadata string");
    }
    return text;
}

static BinaryReader read_model_file(const char *path) {
    FILE *file = open_file_utf8(path, "rb");
    if (!file) {
        fail("cannot open model file");
    }
    if (fseek(file, 0, SEEK_END)) {
        fclose(file);
        fail("cannot seek model");
    }
    long file_size = ftell(file);
    if (file_size < 24 || (size_t)file_size > MAX_ALLOCATION_BYTES) {
        fclose(file);
        fail("model size outside baseline limit");
    }
    rewind(file);
    BinaryReader reader = {0};
    reader.size = (size_t)file_size;
    reader.data = try_allocate(reader.size);
    if (!reader.data) {
        fclose(file);
        fail("out of memory");
    }
    if (fread(reader.data, 1, reader.size, file) != reader.size) {
        fclose(file);
        free_array(reader.data);
        fail("cannot read model");
    }
    fclose(file);
    return reader;
}

/* ---------------------------- Named validation --------------------------- */

static int has_expected_tensor_shape(const Tensor *tensor, const char *name,
                                     size_t rows, size_t columns) {
    int is_norm_vector = strstr(name, "norm.weight") != NULL;
    /* GGUF dimensions are fastest-first; values keep PyTorch [out, in] order. */
    return tensor->dimensions[0] == columns &&
           (is_norm_vector ? tensor->rank == 1
                           : tensor->rank == 2 && tensor->dimensions[1] == rows);
}

static int is_model_config_compatible(const ModelConfig *config, unsigned seen_fields) {
    /* Check completeness first: missing heads must not cause division by zero.
     * Present dimensions have already been validated as positive. */
    return seen_fields == METADATA_COMPUTE_FIELDS && config->vocab_size >= 256 &&
           config->embedding_dim % config->head_count == 0 &&
           (config->embedding_dim / config->head_count) % 2 == 0 &&
           config->layer_count <= MAX_LAYERS &&
           config->context_length <= MAX_CONTEXT_LENGTH;
}

static int is_tensor_data_in_bounds(const Tensor *tensor, size_t byte_count,
                                    size_t data_base, size_t file_size) {
    /* data_base is checked by the caller. Check the offset before subtraction
     * to avoid unsigned underflow hiding a truncated tensor. */
    return tensor->data_offset <= file_size - data_base &&
           byte_count <= file_size - data_base - (size_t)tensor->data_offset;
}

static int tensor_data_overlaps(const Tensor *tensor, size_t byte_count,
                                const Tensor *previous) {
    return tensor->data_offset < previous->data_offset + previous->element_count * 4 &&
           previous->data_offset < tensor->data_offset + byte_count;
}

static int has_duplicate_tensor_name(const Model *model, size_t tensor_index) {
    const char *name = model->tensors[tensor_index].name;
    for (size_t previous_index = 0; previous_index < tensor_index; previous_index++) {
        if (!strcmp(model->tensors[previous_index].name, name)) {
            return 1;
        }
    }
    return 0;
}

static const float *get_tensor_weights(const Model *model, const char *name,
                                       size_t rows, size_t columns) {
    for (size_t index = 0; index < model->tensor_count; index++) {
        const Tensor *tensor = &model->tensors[index];
        if (!strcmp(tensor->name, name)) {
            if (!has_expected_tensor_shape(tensor, name, rows, columns)) {
                fail("tensor shape disagrees with model config");
            }
            return tensor->values;
        }
    }
    char message[192];
    snprintf(message, sizeof(message), "incomplete model: missing tensor %s", name);
    fail(message);
    return NULL;
}

static const float *get_block_weights(const Model *model, int layer_index,
                                      const char *suffix, size_t rows, size_t columns) {
    char tensor_name[128];
    snprintf(tensor_name, sizeof(tensor_name), "blocks.%d.%s", layer_index, suffix);
    return get_tensor_weights(model, tensor_name, rows, columns);
}

/* ----------------------------- GGUF metadata ----------------------------- */

static size_t read_gguf_header(BinaryReader *reader, Model *model) {
    if (memcmp(read_bytes(reader, 4), "GGUF", 4) ||
        read_uint32(reader) != GGUF_VERSION) {
        fail("expected little-endian GGUF v3");
    }
    uint64_t tensor_count = read_uint64(reader);
    uint64_t metadata_count = read_uint64(reader);
    if (tensor_count > MAX_TENSOR_COUNT || metadata_count > MAX_METADATA_COUNT) {
        fail("unsupported GGUF metadata contract");
    }
    model->tensor_count = (size_t)tensor_count;
    model->tensors = allocate_array(model->tensor_count, sizeof(Tensor));
    return (size_t)metadata_count;
}

static void read_expected_uint32(BinaryReader *reader, uint32_t type, uint32_t expected,
                                 const char *error_message) {
    /* A wrong type must not consume a value: preserve short-circuit reading. */
    if (type != GGUF_UINT32 || read_uint32(reader) != expected) {
        fail(error_message);
    }
}

static void read_architecture_metadata(BinaryReader *reader, uint32_t type) {
    if (type != GGUF_STRING) {
        fail("architecture must be string");
    }
    char *architecture = read_string(reader);
    if (strcmp(architecture, "capstone_sql")) {
        fail("unsupported architecture (only capstone_sql)");
    }
    free_array(architecture);
}

/* Managed code owns the tokenizer. Only traverse the serialized metadata to
 * locate the tensor descriptors, without allocating any token data. */
static void skip_tokenizer_metadata(BinaryReader *reader, uint32_t type) {
    uint64_t count = 1;
    if (type == GGUF_ARRAY) {
        type = read_uint32(reader);
        count = read_uint64(reader);
    }
    if (type == GGUF_UINT32) {
        if (count > (reader->size - reader->position) / 4) {
            fail("truncated metadata");
        }
        read_bytes(reader, (size_t)count * 4);
    } else if (type == GGUF_STRING) {
        for (uint64_t i = 0; i < count; ++i) {
            uint64_t length = read_uint64(reader);
            if (length > reader->size - reader->position) {
                fail("truncated metadata string");
            }
            read_bytes(reader, (size_t)length);
        }
    } else {
        fail("unsupported metadata type");
    }
}

static float read_positive_float_metadata(BinaryReader *reader, uint32_t type) {
    if (type != GGUF_FLOAT32) {
        fail("expected float metadata");
    }
    float value = read_float32(reader);
    if (!isfinite(value) || value <= 0) {
        fail("invalid float metadata");
    }
    return value;
}

static unsigned read_dimension_metadata(BinaryReader *reader, ModelConfig *config,
                                        const char *key, uint32_t type) {
    const char *field_names[] = {"vocab_size", "dim",     "layers",
                                 "heads",      "ffn_dim", "context"};
    int *destinations[] = {&config->vocab_size,  &config->embedding_dim,
                           &config->layer_count, &config->head_count,
                           &config->ffn_dim,     &config->context_length};
    for (int index = 0; index < 6; index++) {
        char metadata_name[64];
        snprintf(metadata_name, sizeof(metadata_name), "capstone_sql.%s",
                 field_names[index]);
        if (!strcmp(key, metadata_name)) {
            if (type != GGUF_UINT32) {
                fail("expected uint32 config");
            }
            uint32_t dimension = read_uint32(reader);
            if (dimension == 0 || dimension > MAX_DIMENSION) {
                fail("invalid dimension");
            }
            *destinations[index] = (int)dimension;
            return METADATA_FIRST_DIMENSION << index;
        }
    }
    fail("unsupported metadata key");
    return 0;
}

static unsigned read_metadata_entry(BinaryReader *reader, Model *model, const char *key,
                                    uint32_t type) {
    if (!strcmp(key, "general.architecture")) {
        read_architecture_metadata(reader, type);
        return METADATA_ARCHITECTURE;
    }
    if (!strcmp(key, "general.alignment")) {
        read_expected_uint32(reader, type, GGUF_ALIGNMENT, "expected alignment=32");
        return METADATA_ALIGNMENT;
    }
    if (!strcmp(key, "capstone_sql.contract_version")) {
        read_expected_uint32(reader, type, CONTRACT_VERSION,
                             "unsupported model contract version; re-export as "
                             "version " CONTRACT_VERSION_TEXT);
        return METADATA_CONTRACT_VERSION;
    }
    if (!strcmp(key, "capstone_sql.eps")) {
        model->config.norm_epsilon = read_positive_float_metadata(reader, type);
        return METADATA_EPSILON;
    }
    if (!strcmp(key, "capstone_sql.theta")) {
        model->config.rope_theta = read_positive_float_metadata(reader, type);
        return METADATA_ROPE_THETA;
    }
    if (!strncmp(key, "capstone_sql.tokenizer.", 23)) {
        skip_tokenizer_metadata(reader, type);
        return 0;
    }
    return read_dimension_metadata(reader, &model->config, key, type);
}

static void read_model_metadata(BinaryReader *reader, Model *model,
                                size_t metadata_count) {
    unsigned seen_fields = 0;
    for (size_t index = 0; index < metadata_count; index++) {
        char *key = read_string(reader);
        uint32_t type = read_uint32(reader);
        unsigned field = read_metadata_entry(reader, model, key, type);
        if (seen_fields & field) {
            fail("duplicate compute metadata key");
        }
        seen_fields |= field;
        free_array(key);
    }
    if (!is_model_config_compatible(&model->config, seen_fields)) {
        fail("incomplete or incompatible config");
    }
    /* Embedding, final norm, output head, and seven tensors per decoder block. */
    if (model->tensor_count != (size_t)(3 + 7 * model->config.layer_count)) {
        fail("unexpected tensor count");
    }
}

/* ----------------------- Tensor loading ------------------ */

static void read_tensor_shape_and_offset(BinaryReader *reader, Tensor *tensor) {
    tensor->rank = read_uint32(reader);
    if (tensor->rank < 1 || tensor->rank > 2) {
        fail("unsupported tensor rank");
    }
    tensor->element_count = 1;
    for (uint32_t axis = 0; axis < tensor->rank; axis++) {
        uint64_t dimension = read_uint64(reader);
        if (!dimension || dimension > MAX_DIMENSION) {
            fail("invalid tensor dimension");
        }
        tensor->dimensions[axis] = (size_t)dimension;
        tensor->element_count =
            checked_product(tensor->element_count, (size_t)dimension);
    }
    if (read_uint32(reader) != 0) {
        fail("only F32 tensors supported; quantized files are unsupported");
    }
    tensor->data_offset = read_uint64(reader);
    if (tensor->data_offset % GGUF_ALIGNMENT) {
        fail("unaligned tensor");
    }
}

static void read_tensor_descriptors(BinaryReader *reader, Model *model) {
    for (size_t index = 0; index < model->tensor_count; index++) {
        Tensor *tensor = &model->tensors[index];
        tensor->name = read_string(reader);
        if (has_duplicate_tensor_name(model, index)) {
            fail("duplicate tensor");
        }
        read_tensor_shape_and_offset(reader, tensor);
    }
}

static void validate_tensor_data_range(const Model *model, size_t tensor_index,
                                       size_t byte_count, size_t data_base,
                                       size_t file_size) {
    const Tensor *tensor = &model->tensors[tensor_index];
    if (!is_tensor_data_in_bounds(tensor, byte_count, data_base, file_size)) {
        fail("truncated tensor data");
    }
    for (size_t index = 0; index < tensor_index; index++) {
        if (tensor_data_overlaps(tensor, byte_count, &model->tensors[index])) {
            fail("overlapping tensor data");
        }
    }
}

static void read_tensor_values(BinaryReader *reader, Model *model) {
    /* Tensor offsets are relative to the aligned data section, not file start. */
    size_t data_base =
        (reader->position + GGUF_ALIGNMENT - 1) & ~(size_t)(GGUF_ALIGNMENT - 1);
    if (data_base > reader->size) {
        fail("missing tensor data");
    }
    for (size_t index = 0; index < model->tensor_count; index++) {
        Tensor *tensor = &model->tensors[index];
        size_t byte_count = checked_product(tensor->element_count, 4);
        validate_tensor_data_range(model, index, byte_count, data_base, reader->size);
        reader->position = data_base + (size_t)tensor->data_offset;
        tensor->values = allocate_array(tensor->element_count, sizeof(float));
        for (size_t element = 0; element < tensor->element_count; element++) {
            tensor->values[element] = read_float32(reader);
            if (!isfinite(tensor->values[element])) {
                fail("non-finite weight");
            }
        }
    }
}

static void validate_model_weights(const Model *model) {
    const ModelConfig *config = &model->config;
    int dimension = config->embedding_dim;
    get_tensor_weights(model, "embedding.embedding.weight", config->vocab_size,
                       dimension);
    get_tensor_weights(model, "final_norm.weight", 1, dimension);
    get_tensor_weights(model, "lm_head.weight", config->vocab_size, dimension);
    for (int layer = 0; layer < config->layer_count; layer++) {
        get_block_weights(model, layer, "attn_norm.weight", 1, dimension);
        get_block_weights(model, layer, "ffn_norm.weight", 1, dimension);
        get_block_weights(model, layer, "attn.qkv_proj.weight", 3 * dimension,
                          dimension);
        get_block_weights(model, layer, "attn.out_proj.weight", dimension, dimension);
        get_block_weights(model, layer, "ffn.w1.weight", config->ffn_dim, dimension);
        get_block_weights(model, layer, "ffn.w3.weight", config->ffn_dim, dimension);
        get_block_weights(model, layer, "ffn.w2.weight", dimension, config->ffn_dim);
    }
}

SQL_API Model *sql_model_open(const char *path) {
    sql_set_last_error(NULL);
    if (!path || !*path) {
        sql_set_last_error("model path is empty");
        return NULL;
    }
    jmp_buf bailout;
    s_current_bailout = &bailout;
    if (setjmp(bailout)) {
        finish_call(0);
        return NULL;
    }
    Model *model = allocate_array(1, sizeof(*model));
    BinaryReader reader = read_model_file(path);
    size_t metadata_count = read_gguf_header(&reader, model);
    read_model_metadata(&reader, model, metadata_count);
    read_tensor_descriptors(&reader, model);
    read_tensor_values(&reader, model);
    free_array(reader.data);
    reader.data = NULL;
    validate_model_weights(model);

    finish_call(1);
    return model;
}

/* -------------------------- Transformer inference ------------------------ */

static void linear_projection(float *output, const float *input, const float *weights,
                              int rows, int columns) {
    for (int row = 0; row < rows; row++) {
        float sum = 0;
        for (int column = 0; column < columns; column++) {
            sum += weights[(size_t)row * columns + column] * input[column];
        }
        output[row] = sum;
    }
}

static void apply_rms_norm(float *output, const float *input, const float *weights,
                           int dimension, float epsilon) {
    float square_sum = 0;
    for (int index = 0; index < dimension; index++) {
        square_sum += input[index] * input[index];
    }
    float scale = 1 / sqrtf(square_sum / dimension + epsilon);
    for (int index = 0; index < dimension; index++) {
        output[index] = input[index] * scale * weights[index];
    }
}

static InferenceWorkspace create_workspace(const ModelConfig *config, int token_count,
                                           int attention_capacity) {
    size_t state_count = checked_product((size_t)token_count, config->embedding_dim);
    InferenceWorkspace workspace = {0};
    workspace.hidden_states = allocate_array(state_count, sizeof(float));
    workspace.normalized_states = allocate_array(state_count, sizeof(float));
    workspace.qkv_states =
        allocate_array(checked_product(state_count, 3), sizeof(float));
    workspace.attention_output = allocate_array(state_count, sizeof(float));
    workspace.temporary_vector = allocate_array(config->embedding_dim, sizeof(float));
    workspace.ffn_gate = allocate_array(config->ffn_dim, sizeof(float));
    workspace.ffn_up = allocate_array(config->ffn_dim, sizeof(float));
    workspace.attention_scores = allocate_array(attention_capacity, sizeof(float));
    return workspace;
}

static void release_workspace(InferenceWorkspace *workspace) {
    free_array(workspace->hidden_states);
    free_array(workspace->normalized_states);
    free_array(workspace->qkv_states);
    free_array(workspace->attention_output);
    free_array(workspace->temporary_vector);
    free_array(workspace->ffn_gate);
    free_array(workspace->ffn_up);
    free_array(workspace->attention_scores);
}

static void copy_token_embeddings(const Model *model, const int32_t *token_ids,
                                  int token_count, float *hidden_states) {
    int dimension = model->config.embedding_dim;
    const float *embeddings = get_tensor_weights(model, "embedding.embedding.weight",
                                                 model->config.vocab_size, dimension);
    for (int position = 0; position < token_count; position++) {
        int token_id = token_ids[position];
        if (token_id < 0 || token_id >= model->config.vocab_size) {
            fail("token ID outside vocabulary");
        }
        memcpy(hidden_states + (size_t)position * dimension,
               embeddings + (size_t)token_id * dimension,
               (size_t)dimension * sizeof(float));
    }
}

static void apply_rope_to_query_and_key(float *qkv, const ModelConfig *config,
                                        int position) {
    int dimension = config->embedding_dim;
    int head_dim = dimension / config->head_count;
    for (int head = 0; head < config->head_count; head++) {
        for (int pair = 0; pair < head_dim; pair += 2) {
            float angle =
                (float)position / powf(config->rope_theta, (float)pair / head_dim);
            float cosine = cosf(angle);
            float sine = sinf(angle);
            /* Packed order is Q, K, V. Rotate adjacent Q/K pairs only;
             * V is unchanged, matching the team's Python RoPE. */
            for (int projection = 0; projection < 2; projection++) {
                int offset = projection * dimension + head * head_dim + pair;
                float first = qkv[offset];
                float second = qkv[offset + 1];
                qkv[offset] = first * cosine - second * sine;
                qkv[offset + 1] = first * sine + second * cosine;
            }
        }
    }
}

static void project_token_qkv(const ModelConfig *config, const DecoderWeights *weights,
                              const float *hidden, float *normalized, float *qkv,
                              int position) {
    int dimension = config->embedding_dim;
    apply_rms_norm(normalized, hidden, weights->attention_norm, dimension,
                   config->norm_epsilon);
    linear_projection(qkv, normalized, weights->qkv, 3 * dimension, dimension);
    apply_rope_to_query_and_key(qkv, config, position);
}

static float compute_attention_scores(float *scores, const float *query,
                                      const float *keys, int key_count,
                                      size_t kv_stride, int head_dim) {
    float maximum_score = -INFINITY;
    for (int key_position = 0; key_position < key_count; key_position++) {
        const float *key = keys + (size_t)key_position * kv_stride;
        float dot_product = 0;
        for (int index = 0; index < head_dim; index++) {
            dot_product += query[index] * key[index];
        }
        float score = dot_product / sqrtf((float)head_dim);
        scores[key_position] = score;
        if (score > maximum_score) {
            maximum_score = score;
        }
    }
    return maximum_score;
}

static float exponentiate_attention_scores(float *scores, int score_count,
                                           float maximum_score) {
    float denominator = 0;
    /* Subtracting the maximum stabilizes softmax. Normalize when accumulating
     * values below, retaining the original engine's arithmetic order. */
    for (int index = 0; index < score_count; index++) {
        scores[index] = expf(scores[index] - maximum_score);
        denominator += scores[index];
    }
    return denominator;
}

static void accumulate_attention_values(float *output, const float *scores,
                                        const float *values, int key_count,
                                        size_t kv_stride, int head_dim,
                                        float denominator) {
    for (int value_position = 0; value_position < key_count; value_position++) {
        const float *value = values + (size_t)value_position * kv_stride;
        for (int index = 0; index < head_dim; index++) {
            output[index] += scores[value_position] / denominator * value[index];
        }
    }
}

/* kv_stride spans Q/K/V in the reference pass or just K/V in the cache.
 * Only the prefix through the query position participates in causal attention. */
static void attend_to_prefix(const ModelConfig *config, const float *query,
                             const float *keys, const float *values, int key_count,
                             size_t kv_stride, float *scores, float *output) {
    int head_dim = config->embedding_dim / config->head_count;
    memset(output, 0, (size_t)config->embedding_dim * sizeof(float));
    for (int head = 0; head < config->head_count; head++) {
        int offset = head * head_dim;
        float maximum = compute_attention_scores(scores, query + offset, keys + offset,
                                                 key_count, kv_stride, head_dim);
        float denominator = exponentiate_attention_scores(scores, key_count, maximum);
        accumulate_attention_values(output + offset, scores, values + offset, key_count,
                                    kv_stride, head_dim, denominator);
    }
}

static void add_residual(float *hidden_state, const float *update, int dimension) {
    for (int index = 0; index < dimension; index++) {
        hidden_state[index] += update[index];
    }
}

static void apply_swiglu(float *gate, const float *up, int ffn_dim) {
    for (int index = 0; index < ffn_dim; index++) {
        /* SwiGLU = SiLU(gate) * up; this must not be replaced with GELU. */
        gate[index] = (gate[index] / (1 + expf(-gate[index]))) * up[index];
    }
}

static DecoderWeights get_decoder_weights(const Model *model, int layer_index) {
    int dimension = model->config.embedding_dim;
    int ffn_dim = model->config.ffn_dim;
    DecoderWeights weights = {0};
    weights.attention_norm =
        get_block_weights(model, layer_index, "attn_norm.weight", 1, dimension);
    weights.qkv = get_block_weights(model, layer_index, "attn.qkv_proj.weight",
                                    3 * dimension, dimension);
    weights.attention_output = get_block_weights(
        model, layer_index, "attn.out_proj.weight", dimension, dimension);
    weights.ffn_norm =
        get_block_weights(model, layer_index, "ffn_norm.weight", 1, dimension);
    weights.ffn_gate =
        get_block_weights(model, layer_index, "ffn.w1.weight", ffn_dim, dimension);
    weights.ffn_up =
        get_block_weights(model, layer_index, "ffn.w3.weight", ffn_dim, dimension);
    weights.ffn_down =
        get_block_weights(model, layer_index, "ffn.w2.weight", dimension, ffn_dim);
    return weights;
}

static void apply_attention_residual(const ModelConfig *config,
                                     const DecoderWeights *weights, int position,
                                     InferenceWorkspace *workspace) {
    int dimension = config->embedding_dim;
    size_t offset = (size_t)position * dimension;
    linear_projection(workspace->temporary_vector, workspace->attention_output + offset,
                      weights->attention_output, dimension, dimension);
    add_residual(workspace->hidden_states + offset, workspace->temporary_vector,
                 dimension);
}

static void apply_ffn_residual(const ModelConfig *config, const DecoderWeights *weights,
                               int position, InferenceWorkspace *workspace) {
    int dimension = config->embedding_dim;
    int ffn_dim = config->ffn_dim;
    float *hidden_state = workspace->hidden_states + (size_t)position * dimension;
    float *temporary = workspace->temporary_vector;
    apply_rms_norm(temporary, hidden_state, weights->ffn_norm, dimension,
                   config->norm_epsilon);
    linear_projection(workspace->ffn_gate, temporary, weights->ffn_gate, ffn_dim,
                      dimension);
    linear_projection(workspace->ffn_up, temporary, weights->ffn_up, ffn_dim,
                      dimension);
    apply_swiglu(workspace->ffn_gate, workspace->ffn_up, ffn_dim);
    linear_projection(temporary, workspace->ffn_gate, weights->ffn_down, dimension,
                      ffn_dim);
    add_residual(hidden_state, temporary, dimension);
}

static void run_decoder_block(const Model *model, int layer_index, int token_count,
                              InferenceWorkspace *workspace) {
    const ModelConfig *config = &model->config;
    int dimension = config->embedding_dim;
    DecoderWeights weights = get_decoder_weights(model, layer_index);
    for (int position = 0; position < token_count; position++) {
        size_t offset = (size_t)position * dimension;
        project_token_qkv(config, &weights, workspace->hidden_states + offset,
                          workspace->normalized_states + offset,
                          workspace->qkv_states + 3 * offset, position);
    }
    for (int position = 0; position < token_count; position++) {
        size_t offset = (size_t)position * dimension;
        attend_to_prefix(config, workspace->qkv_states + 3 * offset,
                         workspace->qkv_states + dimension,
                         workspace->qkv_states + 2 * dimension, position + 1,
                         3 * dimension, workspace->attention_scores,
                         workspace->attention_output + offset);
        apply_attention_residual(config, &weights, position, workspace);
        apply_ffn_residual(config, &weights, position, workspace);
    }
}

static void compute_last_token_logits(const Model *model, int token_count,
                                      InferenceWorkspace *workspace, float *logits) {
    int dimension = model->config.embedding_dim;
    int vocab_size = model->config.vocab_size;
    const float *norm_weights =
        get_tensor_weights(model, "final_norm.weight", 1, dimension);
    const float *output_weights =
        get_tensor_weights(model, "lm_head.weight", vocab_size, dimension);
    const float *last_state =
        workspace->hidden_states + (size_t)(token_count - 1) * dimension;
    apply_rms_norm(workspace->temporary_vector, last_state, norm_weights, dimension,
                   model->config.norm_epsilon);
    linear_projection(logits, workspace->temporary_vector, output_weights, vocab_size,
                      dimension);
    for (int token_id = 0; token_id < vocab_size; token_id++) {
        if (!isfinite(logits[token_id])) {
            fail("non-finite logits");
        }
    }
}

/* One independent cache per sentence. Each row stores the rotated key then
 * the unrotated value for a single layer and absolute token position. */
typedef struct {
    DecoderWeights weights;
    float *rows;
} KvLayer;

typedef struct {
    int length;
    int capacity;
    int reserved;
    int layer_count;
    KvLayer *layers;
    InferenceWorkspace workspace;
} KvCache;

static KvCache create_kv_cache(const Model *model, int capacity) {
    const ModelConfig *config = &model->config;
    if (capacity < 1 || capacity > config->context_length) {
        fail("KV capacity outside model context");
    }
    KvCache cache = {0};
    cache.capacity = capacity;
    cache.layer_count = config->layer_count;
    cache.layers = allocate_array(config->layer_count, sizeof(*cache.layers));
    for (int layer = 0; layer < config->layer_count; layer++) {
        cache.layers[layer].weights = get_decoder_weights(model, layer);
    }
    cache.workspace = create_workspace(config, 1, capacity);
    return cache;
}

static void release_kv_cache(KvCache *cache) {
    release_workspace(&cache->workspace);
    for (int layer = 0; layer < cache->layer_count; layer++) {
        free_array(cache->layers[layer].rows);
    }
    free_array(cache->layers);
}

/* Grow on demand, so an early EOS does not reserve an entire context for
 * every layer. Individual layer buffers obey the allocator's size limit. */
static void grow_kv_cache(KvCache *cache, size_t stride) {
    int reserved = cache->reserved ? cache->reserved * 2 : 16;
    if (reserved > cache->capacity) {
        reserved = cache->capacity;
    }
    size_t elements = checked_product(reserved, stride);
    for (int i = 0; i < cache->layer_count; i++) {
        KvLayer *layer = &cache->layers[i];
        float *rows = allocate_array(elements, sizeof(*rows));
        if (layer->rows) {
            memcpy(rows, layer->rows, cache->length * stride * sizeof(*rows));
            free_array(layer->rows);
        }
        layer->rows = rows;
    }
    cache->reserved = reserved;
}

static void cached_forward_token(const Model *model, KvCache *cache, int32_t token_id,
                                 float *logits) {
    int position = cache->length;
    if (position >= cache->capacity) {
        fail("KV cache is full");
    }
    const ModelConfig *config = &model->config;
    int dimension = config->embedding_dim;
    size_t stride = 2 * (size_t)dimension;
    if (position == cache->reserved) {
        grow_kv_cache(cache, stride);
    }
    InferenceWorkspace *workspace = &cache->workspace;
    copy_token_embeddings(model, &token_id, 1, workspace->hidden_states);
    for (int layer = 0; layer < config->layer_count; layer++) {
        const DecoderWeights *weights = &cache->layers[layer].weights;
        float *kv = cache->layers[layer].rows;
        project_token_qkv(config, weights, workspace->hidden_states,
                          workspace->normalized_states, workspace->qkv_states,
                          position);
        memcpy(kv + (size_t)position * stride, workspace->qkv_states + dimension,
               stride * sizeof(float));
        attend_to_prefix(config, workspace->qkv_states, kv, kv + dimension,
                         position + 1, stride, workspace->attention_scores,
                         workspace->attention_output);
        apply_attention_residual(config, weights, 0, workspace);
        apply_ffn_residual(config, weights, 0, workspace);
    }
    cache->length++;
    if (logits) {
        compute_last_token_logits(model, 1, workspace, logits);
    }
}

/* Monotonic elapsed wall time includes the forward workspace and decoder.
 * It excludes model loading, interop, and all caller-side text processing. */
static double monotonic_milliseconds(void) {
#if defined(_WIN32)
    LARGE_INTEGER ticks, frequency;
    if (!QueryPerformanceFrequency(&frequency) || !QueryPerformanceCounter(&ticks)) {
        fail("cannot read performance counter");
    }
    return (double)ticks.QuadPart * 1000.0 / (double)frequency.QuadPart;
#else
    struct timespec time;
    if (clock_gettime(CLOCK_MONOTONIC, &time) != 0) {
        fail("cannot read monotonic clock");
    }
    return (double)time.tv_sec * 1000.0 + (double)time.tv_nsec / 1000000.0;
#endif
}

SQL_API int sql_model_forward(const Model *model, const int32_t *token_ids,
                              int32_t token_count, float *logits,
                              int32_t logits_capacity, double *elapsed_ms) {
    sql_set_last_error(NULL);
    if (elapsed_ms) {
        *elapsed_ms = 0;
    }
    if (!model || !token_ids || !logits || !elapsed_ms) {
        sql_set_last_error("invalid forward arguments");
        return 0;
    }
    if (token_count <= 0 || token_count > model->config.context_length) {
        sql_set_last_error("token count outside model context");
        return 0;
    }
    if (logits_capacity < model->config.vocab_size) {
        sql_set_last_error("logits buffer is smaller than vocabulary");
        return 0;
    }

    jmp_buf bailout;
    s_current_bailout = &bailout;
    if (setjmp(bailout)) {
        finish_call(0);
        return 0;
    }
    double start_ms = monotonic_milliseconds();
    InferenceWorkspace workspace =
        create_workspace(&model->config, token_count, token_count);
    copy_token_embeddings(model, token_ids, token_count, workspace.hidden_states);
    for (int layer = 0; layer < model->config.layer_count; layer++) {
        run_decoder_block(model, layer, token_count, &workspace);
    }
    compute_last_token_logits(model, token_count, &workspace, logits);
    release_workspace(&workspace);
    *elapsed_ms = monotonic_milliseconds() - start_ms;
    finish_call(1);
    return 1;
}

SQL_API void sql_model_close(Model *model) {
    if (!model) {
        return;
    }
    if (model->tensors) {
        for (size_t index = 0; index < model->tensor_count; index++) {
            free_array(model->tensors[index].name);
            free_array(model->tensors[index].values);
        }
        free_array(model->tensors);
        model->tensors = NULL;
    }
    free_array(model);
}

static int32_t select_greedy_token(const float *logits, const uint8_t *allowed,
                                   int vocab_size, int32_t eos_id) {
    int32_t best = eos_id;
    for (int32_t id = 0; id < vocab_size; ++id) {
        if (allowed[id] && logits[id] > logits[best]) {
            best = id;
        }
    }
    return best;
}

SQL_API int sql_model_generate_ids(const Model *model, const int32_t *prompt_ids,
                                   int32_t prompt_count, const uint8_t *allowed,
                                   int32_t allowed_count, int32_t eos_id,
                                   int32_t max_new_tokens, int32_t *generated,
                                   int32_t generated_capacity, int32_t *generated_count,
                                   int32_t *stop_reason, double *compute_ms) {
    sql_set_last_error(NULL);
    if (generated_count) {
        *generated_count = 0;
    }
    if (compute_ms) {
        *compute_ms = 0;
    }
    if (!model || !prompt_ids || !allowed || !generated_count || !stop_reason ||
        !compute_ms) {
        sql_set_last_error("invalid generation arguments");
        return 0;
    }
    const int vocab_size = model->config.vocab_size;
    if (prompt_count <= 0 || prompt_count > model->config.context_length ||
        max_new_tokens < 0 || allowed_count != vocab_size || eos_id < 0 ||
        eos_id >= vocab_size) {
        sql_set_last_error("invalid prompt length, limit, vocabulary mask or EOS");
        return 0;
    }
    for (int i = 0; i < prompt_count; ++i) {
        if (prompt_ids[i] < 0 || prompt_ids[i] >= vocab_size) {
            sql_set_last_error("token ID outside vocabulary");
            return 0;
        }
    }
    int room = model->config.context_length - prompt_count;
    int budget = max_new_tokens < room ? max_new_tokens : room;
    if (generated_capacity < budget || (budget && !generated)) {
        sql_set_last_error("missing or undersized generated ID buffer");
        return 0;
    }
    int reason = max_new_tokens <= room ? SQL_STOP_MAX_NEW_TOKENS : SQL_STOP_CONTEXT;
    if (!budget) {
        *stop_reason = reason;
        return 1;
    }

    jmp_buf bailout;
    s_current_bailout = &bailout;
    if (setjmp(bailout)) {
        finish_call(0);
        return 0;
    }
    double start_ms = monotonic_milliseconds();
    // The last emitted ID never needs a forward pass when either budget is spent.
    KvCache cache = create_kv_cache(model, prompt_count + budget - 1);
    float *logits = allocate_array(vocab_size, sizeof(*logits));
    for (int i = 0; i < prompt_count; ++i) {
        cached_forward_token(model, &cache, prompt_ids[i],
                             i == prompt_count - 1 ? logits : NULL);
    }
    double elapsed_ms = monotonic_milliseconds() - start_ms;
    int count = 0;
    while (count < budget) {
        int32_t next = select_greedy_token(logits, allowed, vocab_size, eos_id);
        if (next == eos_id) {
            reason = SQL_STOP_EOS;
            break;
        }
        generated[count++] = next;
        if (count < budget) {
            start_ms = monotonic_milliseconds();
            cached_forward_token(model, &cache, next, logits);
            elapsed_ms += monotonic_milliseconds() - start_ms;
        }
    }
    free_array(logits);
    release_kv_cache(&cache);
    finish_call(1);
    *generated_count = count;
    *stop_reason = reason;
    *compute_ms = elapsed_ms;
    return 1;
}
