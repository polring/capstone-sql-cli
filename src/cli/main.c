#include "../core/include/engine.h"
#include "../core/postprocessor/core.h"
#include "../core/postprocessor/schema.h"
#include "../core/postprocessor/replacement.h"
#include <ctype.h>
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>

static void try_configure_semantic_path(const char *path, int dim) {
    if (!path || dim <= 0) {
        return;
    }
    char dir[512];
    strncpy(dir, path, sizeof(dir) - 1);
    dir[sizeof(dir) - 1] = '\0';
    char *last_slash = strrchr(dir, '/');
    char *last_bslash = strrchr(dir, '\\');
    char *slash = last_slash > last_bslash ? last_slash : last_bslash;
    char candidate[512];
    if (slash) {
        *slash = '\0';
        snprintf(candidate, sizeof(candidate), "%s/semantic_dict_%dd.bin", dir, dim);
    } else {
        snprintf(candidate, sizeof(candidate), "semantic_dict_%dd.bin", dim);
    }
    FILE *f = fopen(candidate, "rb");
    if (f) {
        fclose(f);
        set_semantic_embedding_path(candidate);
    }
}

#ifdef _WIN32
#include <fcntl.h>
#include <io.h>
#define IS_TERMINAL() (_isatty(_fileno(stdin)))
#else
#include <dirent.h>
#include <strings.h>
#include <sys/stat.h>
#include <unistd.h>
#define IS_TERMINAL() (isatty(fileno(stdin)))
#endif

typedef enum {
    CLI_MODE_QUERY,       /* Text-to-SQL + Postprocessing (was CLI_PIPELINE) */
    CLI_MODE_GENERATE,    /* Text-to-SQL without Postprocessing */
    CLI_MODE_POSTPROCESS, /* Postprocessing only without model */
    CLI_MODE_ENCODE,      /* BPE Tokenizer */
    CLI_MODE_LOGITS,      /* Logits output */
    CLI_MODE_INTERACTIVE, /* Interactive REPL */
    CLI_MODE_JSON         /* JSON I/O Mode */
} CliMode;

typedef struct {
    CliMode mode;
    const char *model_path;
    const char *text;
    const char *input_json_path;
    const char *output_json_path;
    const char *db_path;
    const char *strategy;
    int dim;
    int dim_specified;
    double threshold;
    int max_new_tokens;
    char **token_arguments;
    int token_argument_count;
    char *allocated_text;
} CliOptions;

static char *trim_string(char *str) {
    while (*str == ' ' || *str == '\t' || *str == '\r' || *str == '\n') {
        str++;
    }
    if (*str == 0) {
        return str;
    }
    char *end = str + strlen(str) - 1;
    while (end > str && (*end == ' ' || *end == '\t' || *end == '\r' || *end == '\n')) {
        *end = '\0';
        end--;
    }
    return str;
}

static int is_valid_nonnegative_integer(const char *text, const char *end, long value,
                                        int parse_error) {
    return !parse_error && end != text && !*end && value >= 0 && value <= INT_MAX;
}

static int parse_nonnegative_integer(const char *text) {
    char *end;
    errno = 0;
    long value = strtol(text, &end, 10);
    if (!is_valid_nonnegative_integer(text, end, value, errno)) {
        fail("expected nonnegative integer");
    }
    return (int)value;
}

static void validate_strategy_and_dim(const char *strategy, int dim,
                                      int dim_specified) {
    if (strategy && strstr(strategy, "semantic") != NULL) {
        if (!dim_specified) {
            fail("--dim is required when 'semantic' strategy is used (choose 50, 100, "
                 "200, 300)");
        }
        if (dim != 50 && dim != 100 && dim != 200 && dim != 300) {
            fail("invalid --dim value: must be 50, 100, 200, or 300");
        }
    }
}

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

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

static char *read_file_to_string(const char *path) {
    if (!path) {
        return NULL;
    }
    FILE *f = open_file_utf8(path, "rb");
    if (!f) {
        return NULL;
    }
    if (fseek(f, 0, SEEK_END) != 0) {
        fclose(f);
        return NULL;
    }
    long size = ftell(f);
    if (size < 0) {
        fclose(f);
        return NULL;
    }
    if (fseek(f, 0, SEEK_SET) != 0) {
        fclose(f);
        return NULL;
    }
    char *buf = (char *)malloc((size_t)size + 1);
    if (!buf) {
        fclose(f);
        return NULL;
    }
    size_t read_bytes = fread(buf, 1, (size_t)size, f);
    buf[read_bytes] = '\0';
    fclose(f);
    return buf;
}

static char **extract_all_json_questions(const char *json, size_t *out_count,
                                         int *out_is_array) {
    *out_count = 0;
    if (out_is_array) {
        *out_is_array = 0;
    }
    if (!json) {
        return NULL;
    }

    const char *first_non_ws = json;
    while (*first_non_ws == ' ' || *first_non_ws == '\t' || *first_non_ws == '\r' ||
           *first_non_ws == '\n') {
        first_non_ws++;
    }
    if (*first_non_ws == '[') {
        if (out_is_array) {
            *out_is_array = 1;
        }
    }

    size_t capacity = 16;
    char **questions = (char **)malloc(capacity * sizeof(char *));
    if (!questions) {
        return NULL;
    }
    size_t count = 0;

    const char *p = json;
    while (*p) {
        const char *match = strstr(p, "question");
        if (!match) {
            break;
        }

        if (match > json) {
            char prev = match[-1];
            if (isalnum((unsigned char)prev) || prev == '_') {
                p = match + 8;
                continue;
            }
        }

        const char *after_key = match + 8;
        int quoted_key = (match > json && (match[-1] == '\"' || match[-1] == '\''));
        if (quoted_key) {
            if (*after_key != '\"' && *after_key != '\'') {
                p = after_key;
                continue;
            }
            after_key++;
        } else {
            if (isalnum((unsigned char)*after_key) || *after_key == '_') {
                p = after_key;
                continue;
            }
        }

        while (*after_key == ' ' || *after_key == '\t' || *after_key == '\r' ||
               *after_key == '\n') {
            after_key++;
        }
        if (*after_key != ':') {
            p = after_key;
            continue;
        }
        after_key++;

        while (*after_key == ' ' || *after_key == '\t' || *after_key == '\r' ||
               *after_key == '\n') {
            after_key++;
        }
        if (*after_key != '\"') {
            p = after_key;
            continue;
        }
        after_key++;

        const char *val_start = after_key;
        int escaped = 0;
        while (*after_key) {
            if (*after_key == '\\' && !escaped) {
                escaped = 1;
                after_key++;
                continue;
            }
            if (*after_key == '\"' && !escaped) {
                break;
            }
            escaped = 0;
            after_key++;
        }
        if (*after_key != '\"') {
            p = after_key;
            break;
        }

        size_t val_len = (size_t)(after_key - val_start);
        char *extracted = (char *)malloc(val_len + 1);
        if (!extracted) {
            break;
        }

        size_t out_idx = 0;
        for (size_t i = 0; i < val_len; i++) {
            if (val_start[i] == '\\' && i + 1 < val_len) {
                i++;
                switch (val_start[i]) {
                case '\"':
                    extracted[out_idx++] = '\"';
                    break;
                case '\\':
                    extracted[out_idx++] = '\\';
                    break;
                case '/':
                    extracted[out_idx++] = '/';
                    break;
                case 'b':
                    extracted[out_idx++] = '\b';
                    break;
                case 'f':
                    extracted[out_idx++] = '\f';
                    break;
                case 'n':
                    extracted[out_idx++] = '\n';
                    break;
                case 'r':
                    extracted[out_idx++] = '\r';
                    break;
                case 't':
                    extracted[out_idx++] = '\t';
                    break;
                default:
                    extracted[out_idx++] = val_start[i];
                    break;
                }
            } else {
                extracted[out_idx++] = val_start[i];
            }
        }
        extracted[out_idx] = '\0';

        if (count >= capacity) {
            capacity *= 2;
            char **new_q = (char **)realloc(questions, capacity * sizeof(char *));
            if (!new_q) {
                free(extracted);
                break;
            }
            questions = new_q;
        }
        questions[count++] = extracted;

        p = after_key + 1;
    }

    *out_count = count;
    return questions;
}

static void print_json_escaped_string(FILE *out, const char *str) {
    if (!str) {
        fputs("\"\"", out);
        return;
    }
    fputc('\"', out);
    const unsigned char *p = (const unsigned char *)str;
    while (*p) {
        if (*p == '\"') {
            fputs("\\\"", out);
            p++;
        } else if (*p == '\\') {
            fputs("\\\\", out);
            p++;
        } else if (*p == '\b') {
            fputs("\\b", out);
            p++;
        } else if (*p == '\f') {
            fputs("\\f", out);
            p++;
        } else if (*p == '\n') {
            fputs("\\n", out);
            p++;
        } else if (*p == '\r') {
            fputs("\\r", out);
            p++;
        } else if (*p == '\t') {
            fputs("\\t", out);
            p++;
        } else if (*p < 0x20 || *p == 0x7F) {
            fprintf(out, "\\u%04x", *p);
            p++;
        } else if (*p < 0x80) {
            fputc(*p, out);
            p++;
        } else if ((*p & 0xE0) == 0xC0 && (p[1] & 0xC0) == 0x80) {
            fputc(p[0], out);
            fputc(p[1], out);
            p += 2;
        } else if ((*p & 0xF0) == 0xE0 && (p[1] & 0xC0) == 0x80 &&
                   (p[2] & 0xC0) == 0x80) {
            fputc(p[0], out);
            fputc(p[1], out);
            fputc(p[2], out);
            p += 3;
        } else if ((*p & 0xF8) == 0xF0 && (p[1] & 0xC0) == 0x80 &&
                   (p[2] & 0xC0) == 0x80 && (p[3] & 0xC0) == 0x80) {
            fputc(p[0], out);
            fputc(p[1], out);
            fputc(p[2], out);
            fputc(p[3], out);
            p += 4;
        } else {
            fprintf(out, "\\u00%02x", *p);
            p++;
        }
    }
    fputc('\"', out);
}

static void print_usage(const char *program_name) {
    fprintf(
        stderr,
        "usage:\n"
        "  %s MODEL.gguf [mode] [arguments...]\n"
        "  %s --postprocess SQL [options...]\n"
        "  %s [MODEL.gguf] -i\n\n"
        "modes:\n"
        "  --query QUESTION [--db PATH] [--strategy STRATS] [--dim DIM] "
        "[--max-tokens N]\n"
        "      (alias: --pipeline) Generate and correct SQL using schema\n"
        "  --json INPUT.json OUTPUT.json [--db PATH] [--strategy STRATS] [--dim DIM] "
        "[--max-tokens N]\n"
        "      Read questions from INPUT.json and write questions with SQL outputs to "
        "OUTPUT.json\n"
        "  --generate QUESTION [max_new_tokens]\n"
        "      Generate raw SQL from question without post-processing\n"
        "  --postprocess SQL [--db PATH] [--strategy STRATS] [--dim DIM]\n"
        "      Directly correct SQL without running neural model\n"
        "  --encode TEXT\n"
        "      Encode text into BPE token IDs\n"
        "  --logits ID...\n"
        "      Compute logits for token sequence\n"
        "  -i, --interactive\n"
        "      Start interactive REPL session\n\n"
        "options:\n"
        "  -b, --db PATH           Path to SQLite database for schema dictionary\n"
        "  -s, --strategy STRATS   Correction strategies (e.g. "
        "typo,ngram,semantic. Default: typo,ngram)\n"
        "  -d, --dim <50|100|200|300> GloVe vector dimension (required when using "
        "'semantic')\n"
        "  -t, --threshold FLOAT   Semantic similarity threshold (default: 0.0)\n"
        "  --max-tokens N          Maximum new tokens to generate (default: 32)\n",
        program_name, program_name, program_name);
}

static int is_encode_request(int argc, char **argv) {
    return !strcmp(argv[2], "--encode") && argc == 4;
}

static int is_logits_request(int argc, char **argv) {
    (void)argc;
    return !strcmp(argv[2], "--logits");
}

static CliOptions parse_cli_options(int argc, char **argv) {
    CliOptions options = {0};
    options.strategy = "typo,ngram";
    options.max_new_tokens = DEFAULT_MAX_NEW_TOKENS;
    options.threshold = 0.0;

    if (is_logits_request(argc, argv)) {
        options.mode = CLI_MODE_LOGITS;
        options.token_arguments = argv + 3;
        options.token_argument_count = argc - 3;
        return options;
    }
    if (!strcmp(argv[2], "--encode")) {
        if (!is_encode_request(argc, argv)) {
            fail("unknown mode or invalid arguments");
        }
        options.mode = CLI_MODE_ENCODE;
        options.text = argv[3];
        return options;
    }
    if (!strcmp(argv[2], "-i") || !strcmp(argv[2], "--interactive")) {
        options.mode = CLI_MODE_INTERACTIVE;
        return options;
    }

    if (!strcmp(argv[2], "--json")) {
        options.mode = CLI_MODE_JSON;
        int idx = 3;
        if (idx >= argc) {
            fail("expected input and output json file paths: --json <input.json> "
                 "<output.json>");
        }
        options.input_json_path = argv[idx++];

        /* Check if next argument is output file path (positional) */
        if (idx < argc && argv[idx][0] != '-') {
            options.output_json_path = argv[idx++];
        }

        while (idx < argc) {
            if (!strcmp(argv[idx], "-o") || !strcmp(argv[idx], "--out") ||
                !strcmp(argv[idx], "--output")) {
                if (++idx >= argc) {
                    fail("expected output file path after -o/--output");
                }
                options.output_json_path = argv[idx++];
            } else if (!strcmp(argv[idx], "--db") || !strcmp(argv[idx], "-b")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.db_path = argv[idx++];
            } else if (!strcmp(argv[idx], "--strategy") || !strcmp(argv[idx], "-s")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.strategy = argv[idx++];
            } else if (!strcmp(argv[idx], "--dim") || !strcmp(argv[idx], "-d")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.dim = parse_nonnegative_integer(argv[idx++]);
                options.dim_specified = 1;
            } else if (!strcmp(argv[idx], "--threshold") || !strcmp(argv[idx], "-t")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.threshold = atof(argv[idx++]);
            } else if (!strcmp(argv[idx], "--max-tokens") ||
                       !strcmp(argv[idx], "--max-new-tokens")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.max_new_tokens = parse_nonnegative_integer(argv[idx++]);
            } else {
                fail("unknown mode or invalid arguments");
            }
        }

        if (!options.output_json_path) {
            fail("expected output json file path: --json <input.json> <output.json>");
        }

        validate_strategy_and_dim(options.strategy, options.dim, options.dim_specified);
        return options;
    }

    int is_gen = !strcmp(argv[2], "--generate");
    int is_query = !strcmp(argv[2], "--query") || !strcmp(argv[2], "-q") ||
                   !strcmp(argv[2], "--pipeline");

    if (is_gen || is_query) {
        options.mode = is_gen ? CLI_MODE_GENERATE : CLI_MODE_QUERY;
        if (argc < 4) {
            fail("unknown mode or invalid arguments");
        }
        options.text = argv[3];

        int idx = 4;
        /* Support classic positional arguments first for backward compatibility */
        if (idx < argc && argv[idx][0] != '-') {
            if (is_gen) {
                if (argc == 5) {
                    options.max_new_tokens = parse_nonnegative_integer(argv[4]);
                    return options;
                } else {
                    fail("unknown mode or invalid arguments");
                }
            } else {
                /* is_query */
                char *end;
                errno = 0;
                long val = strtol(argv[4], &end, 10);
                if (*end == '\0' && val >= 0) {
                    options.max_new_tokens = (int)val;
                    idx++;
                } else {
                    options.db_path = argv[4];
                    idx++;
                    if (idx < argc && argv[idx][0] != '-') {
                        options.max_new_tokens = parse_nonnegative_integer(argv[idx]);
                        idx++;
                    }
                }
                if (idx == argc) {
                    return options;
                }
            }
        }

        /* Parse explicit flag arguments */
        while (idx < argc) {
            if (!strcmp(argv[idx], "--db") || !strcmp(argv[idx], "-b")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.db_path = argv[idx++];
            } else if (!strcmp(argv[idx], "--strategy") || !strcmp(argv[idx], "-s")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.strategy = argv[idx++];
            } else if (!strcmp(argv[idx], "--dim") || !strcmp(argv[idx], "-d")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.dim = parse_nonnegative_integer(argv[idx++]);
                options.dim_specified = 1;
            } else if (!strcmp(argv[idx], "--threshold") || !strcmp(argv[idx], "-t")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.threshold = atof(argv[idx++]);
            } else if (!strcmp(argv[idx], "--max-tokens") ||
                       !strcmp(argv[idx], "--max-new-tokens")) {
                if (++idx >= argc) {
                    fail("unknown mode or invalid arguments");
                }
                options.max_new_tokens = parse_nonnegative_integer(argv[idx++]);
            } else {
                fail("unknown mode or invalid arguments");
            }
        }

        validate_strategy_and_dim(options.strategy, options.dim, options.dim_specified);
        return options;
    }

    fail("unknown mode or invalid arguments");
    return options;
}

static int run_standalone_postprocess(int argc, char **argv) {
    if (argc < 2) {
        return 0;
    }
    if (strcmp(argv[1], "--postprocess") != 0 && strcmp(argv[1], "--correct") != 0) {
        return 0;
    }
    if (argc < 3) {
        fprintf(
            stderr,
            "usage: %s --postprocess SQL [--db PATH] [--strategy STRATS] [--dim DIM]\n",
            argv[0]);
        exit(2);
    }
    const char *sql = argv[2];
    const char *db_path = NULL;
    const char *strategy = "typo,ngram";
    int dim = 0;
    int dim_specified = 0;
    double threshold = 0.0;

    int idx = 3;
    /* Legacy positional: [db_path] [strategy] */
    if (idx < argc && argv[idx][0] != '-') {
        db_path = argv[idx++];
        if (idx < argc && argv[idx][0] != '-') {
            strategy = argv[idx++];
        }
    }
    while (idx < argc) {
        if (!strcmp(argv[idx], "--db") || !strcmp(argv[idx], "-b")) {
            if (++idx >= argc) {
                fail("expected path after --db");
            }
            db_path = argv[idx++];
        } else if (!strcmp(argv[idx], "--strategy") || !strcmp(argv[idx], "-s")) {
            if (++idx >= argc) {
                fail("expected strategy list after --strategy");
            }
            strategy = argv[idx++];
        } else if (!strcmp(argv[idx], "--dim") || !strcmp(argv[idx], "-d")) {
            if (++idx >= argc) {
                fail("expected dimension after --dim");
            }
            dim = parse_nonnegative_integer(argv[idx++]);
            dim_specified = 1;
        } else if (!strcmp(argv[idx], "--threshold") || !strcmp(argv[idx], "-t")) {
            if (++idx >= argc) {
                fail("expected threshold after --threshold");
            }
            threshold = atof(argv[idx++]);
        } else {
            fail("unknown option in postprocess");
        }
    }

    validate_strategy_and_dim(strategy, dim, dim_specified);
    try_configure_semantic_path(db_path, dim);

    char **dictionary = NULL;
    size_t dict_size = 0;
    if (db_path) {
        if (load_dictionary_from_sqlite(db_path, &dictionary, &dict_size) != 0) {
            fprintf(stderr, "warning: cannot load schema from %s\n", db_path);
        }
    }

    char *fixed_sql = process_sql(sql, strategy, (const char **)dictionary, dict_size,
                                  dim, threshold);
    if (fixed_sql) {
        printf("%s\n", fixed_sql);
        free(fixed_sql);
    } else {
        printf("%s\n", sql);
    }

    if (dictionary) {
        free_dictionary(dictionary, dict_size);
    }
    free_semantic_cache();
    return 1;
}

static void run_logits_command(const Model *model, const CliOptions *options) {
    int capacity = model->config.context_length;
    int *token_ids = allocate_array(capacity, sizeof(int));
    int token_count = 0;
    for (int index = 0; index < options->token_argument_count; index++) {
        int token_id = parse_nonnegative_integer(options->token_arguments[index]);
        append_token_id(token_ids, &token_count, capacity, token_id);
    }
    if (!token_count) {
        fail("empty token sequence");
    }
    float *logits = forward(model, token_ids, token_count);
    for (int token_id = 0; token_id < model->config.vocab_size; token_id++) {
        printf("%s%.9g", token_id ? " " : "", logits[token_id]);
    }
    puts("");
    free(logits);
    free(token_ids);
}

static void run_encode_command(const Model *model, const CliOptions *options) {
    int capacity = model->config.context_length;
    int *token_ids = allocate_array(capacity, sizeof(int));
    int token_count = encode_text(model, options->text, token_ids, capacity);
    for (int index = 0; index < token_count; index++) {
        printf("%s%d", index ? " " : "", token_ids[index]);
    }
    puts("");
    free(token_ids);
}

static void run_generation_command(const Model *model, const CliOptions *options) {
    int *token_ids = allocate_array(model->config.context_length, sizeof(int));
    int prompt_count = prepare_prompt_tokens(model, options->text, token_ids);
    int token_count = prompt_count;
    const char *stop_reason =
        generate_tokens(model, token_ids, &token_count, options->max_new_tokens);
    for (int index = prompt_count; index < token_count; index++) {
        int token_id = token_ids[index];
        fwrite(model->tokenizer.token_bytes[token_id], 1,
               model->tokenizer.token_byte_lengths[token_id], stdout);
    }
    puts("");
    fprintf(stderr, "stop=%s (untrained weights produce arbitrary text)\n",
            stop_reason);
    free(token_ids);
}

static void run_query_command(const Model *model, const CliOptions *options) {
    int *token_ids = allocate_array(model->config.context_length, sizeof(int));
    int prompt_count = prepare_prompt_tokens(model, options->text, token_ids);
    int token_count = prompt_count;
    const char *stop_reason =
        generate_tokens(model, token_ids, &token_count, options->max_new_tokens);

    size_t raw_sql_len = 0;
    for (int index = prompt_count; index < token_count; index++) {
        int token_id = token_ids[index];
        raw_sql_len += model->tokenizer.token_byte_lengths[token_id];
    }
    char *raw_sql = allocate_array(raw_sql_len + 1, 1);
    size_t offset = 0;
    for (int index = prompt_count; index < token_count; index++) {
        int token_id = token_ids[index];
        size_t len = model->tokenizer.token_byte_lengths[token_id];
        memcpy(raw_sql + offset, model->tokenizer.token_bytes[token_id], len);
        offset += len;
    }
    raw_sql[raw_sql_len] = '\0';
    free(token_ids);

    char **dictionary = NULL;
    size_t dict_size = 0;
    if (options->db_path) {
        if (load_dictionary_from_sqlite(options->db_path, &dictionary, &dict_size) !=
            0) {
            fprintf(stderr, "warning: cannot load schema from %s\n", options->db_path);
        }
    }

    try_configure_semantic_path(
        options->db_path ? options->db_path : options->model_path, options->dim);
    const char *strategy = options->strategy ? options->strategy : "typo,ngram";
    char *fixed_sql = process_sql(raw_sql, strategy, (const char **)dictionary,
                                  dict_size, options->dim, options->threshold);

    fprintf(stderr, "[model] %s\n", raw_sql);
    if (fixed_sql) {
        printf("%s\n", fixed_sql);
        free(fixed_sql);
    } else {
        printf("%s\n", raw_sql);
    }
    fprintf(stderr, "stop=%s\n", stop_reason);

    if (dictionary) {
        free_dictionary(dictionary, dict_size);
    }
    free(raw_sql);
}

static void run_json_command(const Model *model, const CliOptions *options) {
    if (!options->input_json_path || !options->output_json_path) {
        fail("both input and output json file paths are required: --json <input.json> "
             "<output.json>");
    }

    char *json_text = read_file_to_string(options->input_json_path);
    if (!json_text) {
        fail("failed to open or read input JSON file");
    }

    int is_array = 0;
    size_t question_count = 0;
    char **questions =
        extract_all_json_questions(json_text, &question_count, &is_array);
    free(json_text);

    if (question_count == 0) {
        if (questions) {
            free(questions);
        }
        fail("no 'question' field found in input JSON file");
    }

    FILE *out = open_file_utf8(options->output_json_path, "wb");
    if (!out) {
        for (size_t i = 0; i < question_count; i++) {
            free(questions[i]);
        }
        free(questions);
        fail("failed to open output JSON file for writing");
    }

    char **dictionary = NULL;
    size_t dict_size = 0;
    if (options->db_path) {
        if (load_dictionary_from_sqlite(options->db_path, &dictionary, &dict_size) !=
            0) {
            fprintf(stderr, "warning: cannot load schema from %s\n", options->db_path);
        }
    }

    try_configure_semantic_path(
        options->db_path ? options->db_path : options->model_path, options->dim);
    const char *strategy = options->strategy ? options->strategy : "typo,ngram";

    int use_array = (is_array || question_count > 1);
    if (use_array) {
        fputs("[\n", out);
    }

    for (size_t q_idx = 0; q_idx < question_count; q_idx++) {
        const char *question = questions[q_idx];

        int *token_ids = allocate_array(model->config.context_length, sizeof(int));
        int prompt_count = prepare_prompt_tokens(model, question, token_ids);
        int token_count = prompt_count;
        const char *stop_reason =
            generate_tokens(model, token_ids, &token_count, options->max_new_tokens);
        (void)stop_reason;

        size_t raw_sql_len = 0;
        for (int index = prompt_count; index < token_count; index++) {
            int token_id = token_ids[index];
            raw_sql_len += model->tokenizer.token_byte_lengths[token_id];
        }
        char *raw_sql = allocate_array(raw_sql_len + 1, 1);
        size_t offset = 0;
        for (int index = prompt_count; index < token_count; index++) {
            int token_id = token_ids[index];
            size_t len = model->tokenizer.token_byte_lengths[token_id];
            memcpy(raw_sql + offset, model->tokenizer.token_bytes[token_id], len);
            offset += len;
        }
        raw_sql[raw_sql_len] = '\0';
        free(token_ids);

        char *fixed_sql = NULL;
        if (dictionary && dict_size > 0) {
            fixed_sql = process_sql(raw_sql, strategy, (const char **)dictionary,
                                    dict_size, options->dim, options->threshold);
        }
        const char *final_sql =
            (fixed_sql && strncmp(fixed_sql, "Error:", 6) != 0) ? fixed_sql : raw_sql;

        const char *indent = use_array ? "  " : "";
        if (use_array) {
            fputs("  {\n", out);
        } else {
            fputs("{\n", out);
        }
        fprintf(out, "%s  \"question\": ", indent);
        print_json_escaped_string(out, question);
        fprintf(out, ",\n%s  \"output\": ", indent);
        print_json_escaped_string(out, final_sql);
        fprintf(out, "\n%s}", indent);

        if (use_array) {
            if (q_idx + 1 < question_count) {
                fputs(",\n", out);
            } else {
                fputs("\n", out);
            }
        } else {
            fputs("\n", out);
        }

        if (fixed_sql) {
            free(fixed_sql);
        }
        free(raw_sql);
        free(questions[q_idx]);
    }

    if (use_array) {
        fputs("]\n", out);
    }

    fclose(out);
    free(questions);

    if (dictionary) {
        free_dictionary(dictionary, dict_size);
    }
    free_semantic_cache();
}

/* ====================================================================== */
/* Interactive REPL Shell Implementation                                  */
/* ====================================================================== */

static void print_shell_status(const char *model_path, int model_loaded,
                               const char *db_path, size_t dict_size,
                               const char *strategy, int dim, double threshold,
                               int max_tokens, CliMode mode) {
    printf("\n--- Current Configuration ---\n");
    printf("  Model:       %s %s\n", model_path[0] ? model_path : "(none)",
           model_loaded ? "[Loaded]" : "[Not loaded]");
    printf("  Database:    %s (Schema items: %zu)\n", db_path[0] ? db_path : "(none)",
           dict_size);
    printf("  Strategy:    %s\n", strategy);
    printf("  Dimension:   %d\n", dim);
    printf("  Threshold:   %.2f\n", threshold);
    printf("  Max Tokens:  %d\n", max_tokens);
    const char *mode_str = "query";
    if (mode == CLI_MODE_GENERATE) {
        mode_str = "generate";
    } else if (mode == CLI_MODE_POSTPROCESS) {
        mode_str = "postprocess";
    } else if (mode == CLI_MODE_ENCODE) {
        mode_str = "encode";
    }
    printf("  Mode:        %s\n", mode_str);
    printf("-----------------------------\n\n");
}

#define MAX_DIR_ENTRIES 256

static int has_extension(const char *filename, const char *ext) {
    size_t flen = strlen(filename);
    size_t elen = strlen(ext);
    if (flen < elen) {
        return 0;
    }
    const char *dot = filename + (flen - elen);
#ifdef _WIN32
    return _stricmp(dot, ext) == 0;
#else
    return strcasecmp(dot, ext) == 0;
#endif
}

static int extract_dim_from_filename(const char *name) {
    if (strstr(name, "50d.bin")) {
        return 50;
    }
    if (strstr(name, "100d.bin")) {
        return 100;
    }
    if (strstr(name, "200d.bin")) {
        return 200;
    }
    if (strstr(name, "300d.bin")) {
        return 300;
    }
    return 0;
}

static int list_directory_entries(const char *dir_path, char names[][256],
                                  int is_dirs[], int max_entries) {
    int count = 0;
#ifdef _WIN32
    char search_pattern[512];
    snprintf(search_pattern, sizeof(search_pattern), "%s/*", dir_path);
    struct _finddata_t data;
    intptr_t h = _findfirst(search_pattern, &data);
    if (h != -1) {
        do {
            if (!strcmp(data.name, ".") || !strcmp(data.name, "..")) {
                continue;
            }
            if (count < max_entries) {
                strncpy(names[count], data.name, 255);
                names[count][255] = '\0';
                is_dirs[count] = (data.attrib & _A_SUBDIR) ? 1 : 0;
                count++;
            }
        } while (_findnext(h, &data) == 0);
        _findclose(h);
    }
#else
    DIR *d = opendir(dir_path);
    if (d) {
        struct dirent *ent;
        while ((ent = readdir(d)) != NULL && count < max_entries) {
            if (!strcmp(ent->d_name, ".") || !strcmp(ent->d_name, "..")) {
                continue;
            }
            strncpy(names[count], ent->d_name, 255);
            names[count][255] = '\0';
            char full_path[512];
            snprintf(full_path, sizeof(full_path), "%s/%s", dir_path, ent->d_name);
            struct stat st;
            is_dirs[count] = (stat(full_path, &st) == 0 && S_ISDIR(st.st_mode));
            count++;
        }
        closedir(d);
    }
#endif
    return count;
}

static void print_assets_in_dir(const char *dir, const char *filter, int *p_models,
                                int *p_dbs, int *p_embs) {
    char names[MAX_DIR_ENTRIES][256];
    int is_dirs[MAX_DIR_ENTRIES];
    int n = list_directory_entries(dir, names, is_dirs, MAX_DIR_ENTRIES);

    int show_models = (!filter || !strcmp(filter, "all") || !strcmp(filter, "model") ||
                       !strcmp(filter, "models"));
    int show_dbs = (!filter || !strcmp(filter, "all") || !strcmp(filter, "db") ||
                    !strcmp(filter, "dbs"));
    int show_embs = (!filter || !strcmp(filter, "all") ||
                     !strcmp(filter, "embedding") || !strcmp(filter, "embeddings") ||
                     !strcmp(filter, "dim") || !strcmp(filter, "dims"));

    for (int i = 0; i < n; i++) {
        if (!is_dirs[i]) {
            if (show_models && has_extension(names[i], ".gguf")) {
                if (*p_models == 0) {
                    printf("\n  [Models (*.gguf)]\n");
                }
                printf("    %s/%s\n      -> /set model %s/%s\n", dir, names[i], dir,
                       names[i]);
                (*p_models)++;
            } else if (show_dbs && (has_extension(names[i], ".db") ||
                                    has_extension(names[i], ".sqlite") ||
                                    has_extension(names[i], ".sqlite3"))) {
                if (*p_dbs == 0) {
                    printf("\n  [Databases (*.db, *.sqlite)]\n");
                }
                printf("    %s/%s\n      -> /set db %s/%s\n", dir, names[i], dir,
                       names[i]);
                (*p_dbs)++;
            } else if (show_embs && has_extension(names[i], ".bin")) {
                int dim = extract_dim_from_filename(names[i]);
                if (*p_embs == 0) {
                    printf("\n  [Embedding Dictionaries (*.bin)]\n");
                }
                if (dim > 0) {
                    printf("    %s/%s (Dim: %d)\n      -> /set embedding %d\n", dir,
                           names[i], dim, dim);
                } else {
                    printf("    %s/%s\n", dir, names[i]);
                }
                (*p_embs)++;
            }
        }
    }
}

static int is_directory(const char *path) {
    if (!path || !*path) {
        return 0;
    }
#ifdef _WIN32
    struct _stat st;
    if (_stat(path, &st) == 0) {
        return (st.st_mode & _S_IFDIR) != 0;
    }
    return 0;
#else
    struct stat st;
    if (stat(path, &st) == 0) {
        return S_ISDIR(st.st_mode);
    }
    return 0;
#endif
}

static void list_available_assets(const char *arg) {
    const char *filter = "all";
    const char *target_dir = NULL;

    if (arg && *arg) {
        if (!strcmp(arg, "model") || !strcmp(arg, "models") || !strcmp(arg, "db") ||
            !strcmp(arg, "dbs") || !strcmp(arg, "embedding") ||
            !strcmp(arg, "embeddings") || !strcmp(arg, "dim") || !strcmp(arg, "dims") ||
            !strcmp(arg, "asset") || !strcmp(arg, "assets")) {
            filter = arg;
        } else {
            target_dir = arg;
        }
    }

    printf("\n=== Available Assets & Selectable Files ===\n");

    int show_bundles = (!filter || !strcmp(filter, "all") || !strcmp(filter, "asset") ||
                        !strcmp(filter, "assets"));
    int found_bundles = 0, found_models = 0, found_dbs = 0, found_embs = 0;

    if (target_dir) {
        /* If user specified a specific directory, suggest /set asset for it */
        if (show_bundles && is_directory(target_dir)) {
            printf("\n  [Asset Bundle (Batch Load: Model + DB + Embedding)]\n");
            printf("    %s\n      -> /set asset %s  (or /asset %s)\n", target_dir,
                   target_dir, target_dir);
            found_bundles++;
        }
        print_assets_in_dir(target_dir, filter, &found_models, &found_dbs, &found_embs);
    } else {
        /* Default: scan assets/ and its subdirectories */
        char names[MAX_DIR_ENTRIES][256];
        int is_dirs[MAX_DIR_ENTRIES];
        int n = list_directory_entries("assets", names, is_dirs, MAX_DIR_ENTRIES);

        /* 1. Show Asset Bundles (Directories inside assets/) */
        if (show_bundles && n > 0) {
            for (int i = 0; i < n; i++) {
                if (is_dirs[i]) {
                    if (found_bundles == 0) {
                        printf("\n  [Asset Bundles (Batch Load: Model + DB + "
                               "Embedding)]\n");
                    }
                    printf("    assets/%s\n      -> /set asset %s  (or /asset %s)\n",
                           names[i], names[i], names[i]);
                    found_bundles++;
                }
            }
        }

        /* 2. Show individual files if not filtering exclusively for bundles */
        if (strcmp(filter, "asset") != 0 && strcmp(filter, "assets") != 0) {
            if (n > 0) {
                print_assets_in_dir("assets", filter, &found_models, &found_dbs,
                                    &found_embs);
                for (int i = 0; i < n; i++) {
                    if (is_dirs[i]) {
                        char sub[512];
                        snprintf(sub, sizeof(sub), "assets/%s", names[i]);
                        print_assets_in_dir(sub, filter, &found_models, &found_dbs,
                                            &found_embs);
                    }
                }
            }
            /* Also check current directory */
            print_assets_in_dir(".", filter, &found_models, &found_dbs, &found_embs);
        }
    }

    if (found_bundles == 0 && found_models == 0 && found_dbs == 0 && found_embs == 0) {
        printf("  (No matching model, database, embedding, or asset bundle found in "
               "'%s')\n",
               target_dir ? target_dir : "assets/");
    }
    printf("===========================================\n\n");
}

static int load_asset_folder(const char *dir, Model *model, int *model_loaded,
                             char *model_path, size_t model_path_size, char *db_path,
                             size_t db_path_size, char ***dictionary, size_t *dict_size,
                             int *dim, int *dim_specified, int is_auto) {
    char names[MAX_DIR_ENTRIES][256];
    int is_dirs[MAX_DIR_ENTRIES];
    int n = list_directory_entries(dir, names, is_dirs, MAX_DIR_ENTRIES);
    if (n <= 0) {
        return 0;
    }

    int found_model = 0, found_db = 0, found_emb = 0;

    /* 1. Discover model (*.gguf) */
    if (!is_auto || !*model_loaded) {
        for (int i = 0; i < n; i++) {
            if (!is_dirs[i] && has_extension(names[i], ".gguf")) {
                char new_model_path[512];
                snprintf(new_model_path, sizeof(new_model_path), "%s/%s", dir,
                         names[i]);
                if (*model_loaded) {
                    release_model(model);
                    *model_loaded = 0;
                }
                printf("%s Loading model: %s ...\n",
                       is_auto ? "[Auto-discovered]" : "[Asset]", new_model_path);
                *model = load_model(new_model_path);
                *model_loaded = 1;
                strncpy(model_path, new_model_path, model_path_size - 1);
                model_path[model_path_size - 1] = '\0';
                found_model = 1;
                break;
            }
        }
    }

    /* 2. Discover database schema (*.db, *.sqlite, *.sqlite3) */
    if (!is_auto || db_path[0] == '\0') {
        for (int i = 0; i < n; i++) {
            if (!is_dirs[i] &&
                (has_extension(names[i], ".db") || has_extension(names[i], ".sqlite") ||
                 has_extension(names[i], ".sqlite3"))) {
                char new_db_path[512];
                snprintf(new_db_path, sizeof(new_db_path), "%s/%s", dir, names[i]);
                if (*dictionary) {
                    free_dictionary(*dictionary, *dict_size);
                    *dictionary = NULL;
                    *dict_size = 0;
                }
                strncpy(db_path, new_db_path, db_path_size - 1);
                db_path[db_path_size - 1] = '\0';
                if (load_dictionary_from_sqlite(db_path, dictionary, dict_size) == 0) {
                    printf("%s Loaded database: %s (%zu schema items)\n",
                           is_auto ? "[Auto-discovered]" : "[Asset]", db_path,
                           *dict_size);
                } else {
                    printf("Warning: failed to load schema from %s\n", db_path);
                }
                found_db = 1;
                break;
            }
        }
    }

    /* 3. Discover embedding dictionary (*.bin) */
    for (int i = 0; i < n; i++) {
        if (!is_dirs[i] && has_extension(names[i], ".bin")) {
            int detected_dim = extract_dim_from_filename(names[i]);
            char bin_path[512];
            snprintf(bin_path, sizeof(bin_path), "%s/%s", dir, names[i]);
            set_semantic_embedding_path(bin_path);
            if (detected_dim > 0) {
                *dim = detected_dim;
                *dim_specified = 1;
            }
            printf("%s Configured embedding: %s (Dim: %d)\n",
                   is_auto ? "[Auto-discovered]" : "[Asset]", bin_path, *dim);
            found_emb = 1;
            break;
        }
    }

    return found_model || found_db || found_emb;
}

static void auto_discover_interactive_assets(Model *model, int *model_loaded,
                                             char *model_path, size_t model_path_size,
                                             char *db_path, size_t db_path_size,
                                             char ***dictionary, size_t *dict_size,
                                             int *dim, int *dim_specified) {
    if (*model_loaded && db_path[0] != '\0') {
        return;
    }

    /* Check subdirectories of assets/ first */
    char names[MAX_DIR_ENTRIES][256];
    int is_dirs[MAX_DIR_ENTRIES];
    int n = list_directory_entries("assets", names, is_dirs, MAX_DIR_ENTRIES);
    if (n > 0) {
        for (int i = 0; i < n; i++) {
            if (is_dirs[i]) {
                char sub_path[512];
                snprintf(sub_path, sizeof(sub_path), "assets/%s", names[i]);
                if (load_asset_folder(sub_path, model, model_loaded, model_path,
                                      model_path_size, db_path, db_path_size,
                                      dictionary, dict_size, dim, dim_specified, 1)) {
                    return;
                }
            }
        }
        /* Check assets/ directly */
        if (load_asset_folder("assets", model, model_loaded, model_path,
                              model_path_size, db_path, db_path_size, dictionary,
                              dict_size, dim, dim_specified, 1)) {
            return;
        }
    }

    /* Check current directory */
    load_asset_folder(".", model, model_loaded, model_path, model_path_size, db_path,
                      db_path_size, dictionary, dict_size, dim, dim_specified, 1);
}

static void handle_set_asset_command(const char *arg, Model *model, int *model_loaded,
                                     char *model_path, size_t model_path_size,
                                     char *db_path, size_t db_path_size,
                                     char ***dictionary, size_t *dict_size, int *dim,
                                     int *dim_specified, const char *strategy,
                                     double threshold, int max_tokens, CliMode mode) {
    if (!arg || !*arg) {
        printf("\nUsage: /set asset <tag_or_directory>  (Alias: /asset <tag>)\n");
        printf("Available asset directories:\n");
        char names[MAX_DIR_ENTRIES][256];
        int is_dirs[MAX_DIR_ENTRIES];
        int n = list_directory_entries("assets", names, is_dirs, MAX_DIR_ENTRIES);
        int found = 0;
        for (int i = 0; i < n; i++) {
            if (is_dirs[i]) {
                printf("  assets/%s  (or '%s')\n", names[i], names[i]);
                found++;
            }
        }
        if (!found) {
            printf("  (No asset directories found under 'assets/')\n");
        }
        printf("Tip: Use '/ls' to inspect all available files.\n\n");
        return;
    }

    char target_dir[512] = "";
    if (is_directory(arg)) {
        strncpy(target_dir, arg, sizeof(target_dir) - 1);
    } else {
        char candidate[512];
        snprintf(candidate, sizeof(candidate), "assets/%s", arg);
        if (is_directory(candidate)) {
            strncpy(target_dir, candidate, sizeof(target_dir) - 1);
        }
    }

    if (!target_dir[0]) {
        printf("Error: Directory '%s' (or 'assets/%s') not found.\n", arg, arg);
        printf("Tip: Use '/ls' to check available asset folders.\n\n");
        return;
    }

    printf("\nLoading asset bundle from '%s' ...\n", target_dir);
    int loaded = load_asset_folder(target_dir, model, model_loaded, model_path,
                                   model_path_size, db_path, db_path_size, dictionary,
                                   dict_size, dim, dim_specified, 0);
    if (!loaded) {
        printf("Warning: No .gguf, .db, or .bin files found in '%s'.\n", target_dir);
    }
    print_shell_status(model_path, *model_loaded, db_path, *dict_size, strategy, *dim,
                       threshold, max_tokens, mode);
}

static void print_shell_help(void) {
    printf(
        "\n--- Interactive Commands ---\n"
        "  /set asset <tag|dir>   Batch load all assets (.gguf, .db, .bin) from "
        "folder\n"
        "                         (Alias: /asset <tag>. e.g. /set asset "
        "stage1-contract2-rc1)\n"
        "  /set db <path>         Load SQLite database schema (e.g. /set db shop.db)\n"
        "  /set model <path>      Load GGUF neural model (e.g. /set model model.gguf)\n"
        "  /set embedding <dim>   Set GloVe vector dimension (50, 100, 200, 300. "
        "Default: 300)\n"
        "  /set strategy <strats> Set correction strategies (e.g. "
        "typo,ngram,semantic)\n"
        "  /set threshold <float> Set semantic similarity threshold (default: 0.5)\n"
        "  /set tokens <number>   Set max new tokens to generate (default: 32)\n"
        "  /set mode <mode>       Switch mode: query (default), gen, post, enc\n"
        "  /ls [path|type]        Inspect directory and list asset bundles (/set "
        "asset),\n"
        "                         models (*.gguf), databases (*.db), and embeddings "
        "(*.bin)\n"
        "                         (Aliases: /list, /dir, /files, /assets. Examples: "
        "/ls, /ls asset, /ls model)\n"
        "  /status                Display active model, database, and configuration\n"
        "  /info (or /config, /state) Alias for /status command\n"
        "  /help                  Show this help message\n"
        "  /exit (or /quit, /q)   Exit the interactive shell\n"
        "----------------------------\n\n");
}

static void run_interactive_shell(const char *initial_model_path,
                                  const char *initial_db_path,
                                  const char *initial_strategy, int initial_dim,
                                  double initial_threshold, int initial_max_tokens) {
    Model model = {0};
    int model_loaded = 0;
    char model_path[512] = "";
    char db_path[512] = "";
    char strategy[128] = "typo,ngram";
    int dim = initial_dim ? initial_dim : 300;
    int dim_specified = 1;
    double threshold = initial_threshold > 0.0 ? initial_threshold : 0.5;
    int max_tokens =
        initial_max_tokens > 0 ? initial_max_tokens : DEFAULT_MAX_NEW_TOKENS;
    CliMode mode = CLI_MODE_QUERY;

    char **dictionary = NULL;
    size_t dict_size = 0;

    if (initial_strategy) {
        strncpy(strategy, initial_strategy, sizeof(strategy) - 1);
    }

    printf(
        "\n======================================================================\n");
    printf("            SQL Engine & Post-processor Interactive Shell             \n");
    printf("======================================================================\n");
    printf(" Type your question/query or a meta-command (e.g. '/set db shop.db', "
           "'/help').\n");

    /* Load initial model if specified */
    if (initial_model_path && initial_model_path[0]) {
        strncpy(model_path, initial_model_path, sizeof(model_path) - 1);
        printf("\nLoading model: %s ...\n", model_path);
        model = load_model(model_path);
        model_loaded = 1;
        printf("Model loaded successfully.\n");
    }

    /* Load initial DB if specified */
    if (initial_db_path && initial_db_path[0]) {
        strncpy(db_path, initial_db_path, sizeof(db_path) - 1);
        if (load_dictionary_from_sqlite(db_path, &dictionary, &dict_size) == 0) {
            printf("Loaded schema dictionary (%zu items) from %s\n", dict_size,
                   db_path);
        } else {
            printf("Warning: failed to load schema from %s\n", db_path);
        }
    }

    /* Auto-discover existing assets if not explicitly specified */
    auto_discover_interactive_assets(&model, &model_loaded, model_path,
                                     sizeof(model_path), db_path, sizeof(db_path),
                                     &dictionary, &dict_size, &dim, &dim_specified);

    print_shell_status(model_path, model_loaded, db_path, dict_size, strategy, dim,
                       threshold, max_tokens, mode);

    char input_buffer[2048];
    while (1) {
        const char *prompt = "sql-query> ";
        if (mode == CLI_MODE_GENERATE) {
            prompt = "sql-gen> ";
        } else if (mode == CLI_MODE_POSTPROCESS) {
            prompt = "sql-post> ";
        } else if (mode == CLI_MODE_ENCODE) {
            prompt = "sql-enc> ";
        }

        printf("%s", prompt);
        fflush(stdout);

        if (!fgets(input_buffer, sizeof(input_buffer), stdin)) {
            puts("");
            break; /* EOF (Ctrl+D / Ctrl+Z) */
        }

        char *line = trim_string(input_buffer);
        if (!*line) {
            continue;
        }

        /* Meta-commands starting with '/' */
        if (line[0] == '/') {
            char *cmd = line + 1;
            while (*cmd == ' ') {
                cmd++;
            }

            if (!strcmp(cmd, "exit") || !strcmp(cmd, "quit") || !strcmp(cmd, "q")) {
                printf("Bye!\n");
                break;
            } else if (!strcmp(cmd, "help") || !strcmp(cmd, "h") || !strcmp(cmd, "?")) {
                print_shell_help();
            } else if (!strcmp(cmd, "status") || !strcmp(cmd, "info") ||
                       !strcmp(cmd, "config") || !strcmp(cmd, "conf") ||
                       !strcmp(cmd, "state") || !strcmp(cmd, "show") ||
                       !strcmp(cmd, "get")) {
                print_shell_status(model_path, model_loaded, db_path, dict_size,
                                   strategy, dim, threshold, max_tokens, mode);
            } else if (!strcmp(cmd, "ls") || !strncmp(cmd, "ls ", 3) ||
                       !strcmp(cmd, "list") || !strncmp(cmd, "list ", 5) ||
                       !strcmp(cmd, "dir") || !strncmp(cmd, "dir ", 4) ||
                       !strcmp(cmd, "files") || !strncmp(cmd, "files ", 6) ||
                       !strcmp(cmd, "models") || !strcmp(cmd, "dbs") ||
                       !strcmp(cmd, "embeddings") || !strcmp(cmd, "dims") ||
                       !strcmp(cmd, "assets") || !strncmp(cmd, "assets ", 7)) {
                const char *arg = "";
                if (!strncmp(cmd, "ls ", 3)) {
                    arg = trim_string(cmd + 3);
                } else if (!strncmp(cmd, "list ", 5)) {
                    arg = trim_string(cmd + 5);
                } else if (!strncmp(cmd, "dir ", 4)) {
                    arg = trim_string(cmd + 4);
                } else if (!strncmp(cmd, "files ", 6)) {
                    arg = trim_string(cmd + 6);
                } else if (!strcmp(cmd, "models")) {
                    arg = "model";
                } else if (!strcmp(cmd, "dbs")) {
                    arg = "db";
                } else if (!strcmp(cmd, "embeddings") || !strcmp(cmd, "dims")) {
                    arg = "embedding";
                } else if (!strcmp(cmd, "assets")) {
                    arg = "asset";
                } else if (!strncmp(cmd, "assets ", 7)) {
                    arg = trim_string(cmd + 7);
                }
                list_available_assets(arg);
            } else if (!strncmp(cmd, "asset ", 6) || !strcmp(cmd, "asset")) {
                const char *asset_arg =
                    trim_string(cmd + (!strncmp(cmd, "asset ", 6) ? 6 : 5));
                handle_set_asset_command(asset_arg, &model, &model_loaded, model_path,
                                         sizeof(model_path), db_path, sizeof(db_path),
                                         &dictionary, &dict_size, &dim, &dim_specified,
                                         strategy, threshold, max_tokens, mode);
            } else if (!strncmp(cmd, "set ", 4)) {
                char *sub = trim_string(cmd + 4);
                if (!strncmp(sub, "asset ", 6) || !strcmp(sub, "asset")) {
                    const char *asset_arg =
                        trim_string(sub + (!strncmp(sub, "asset ", 6) ? 6 : 5));
                    handle_set_asset_command(asset_arg, &model, &model_loaded,
                                             model_path, sizeof(model_path), db_path,
                                             sizeof(db_path), &dictionary, &dict_size,
                                             &dim, &dim_specified, strategy, threshold,
                                             max_tokens, mode);
                } else if (!strncmp(sub, "db ", 3)) {
                    char *new_db = trim_string(sub + 3);
                    if (*new_db) {
                        if (dictionary) {
                            free_dictionary(dictionary, dict_size);
                            dictionary = NULL;
                            dict_size = 0;
                        }
                        strncpy(db_path, new_db, sizeof(db_path) - 1);
                        if (load_dictionary_from_sqlite(db_path, &dictionary,
                                                        &dict_size) == 0) {
                            printf("Database schema loaded (%zu items) from %s\n",
                                   dict_size, db_path);
                        } else {
                            printf("Warning: failed to load schema from %s\n", db_path);
                        }
                    } else {
                        printf("Usage: /set db <path_to_sqlite.db>\n"
                               "Tip: Use '/ls db' to inspect available databases.\n");
                    }
                } else if (!strncmp(sub, "model ", 6)) {
                    char *new_path = trim_string(sub + 6);
                    if (*new_path) {
                        if (model_loaded) {
                            release_model(&model);
                            model_loaded = 0;
                        }
                        strncpy(model_path, new_path, sizeof(model_path) - 1);
                        printf("Loading model: %s ...\n", model_path);
                        model = load_model(model_path);
                        model_loaded = 1;
                        printf("Model loaded successfully.\n");
                    } else {
                        printf("Usage: /set model <path_to_model.gguf>\n"
                               "Tip: Use '/ls model' to inspect available models.\n");
                    }
                } else if (!strncmp(sub, "embedding ", 10) ||
                           !strncmp(sub, "emb ", 4) || !strncmp(sub, "dim ", 4)) {
                    char *val_str =
                        trim_string(sub + (!strncmp(sub, "embedding ", 10) ? 10 : 4));
                    int new_dim = atoi(val_str);
                    if (new_dim == 50 || new_dim == 100 || new_dim == 200 ||
                        new_dim == 300) {
                        dim = new_dim;
                        dim_specified = 1;
                        printf("Embedding dimension set to: %d\n", dim);
                    } else {
                        printf("Invalid dimension '%s'. Choose 50, 100, 200, or 300.\n"
                               "Tip: Use '/ls embedding' to inspect available files.\n",
                               val_str);
                    }
                } else if (!strncmp(sub, "strategy ", 9) ||
                           !strncmp(sub, "strat ", 6)) {
                    char *new_strat =
                        trim_string(sub + (!strncmp(sub, "strategy ", 9) ? 9 : 6));
                    if (*new_strat) {
                        strncpy(strategy, new_strat, sizeof(strategy) - 1);
                        printf("Strategy set to: %s\n", strategy);
                        if (strstr(strategy, "semantic") != NULL && !dim_specified) {
                            printf("Note: 'semantic' strategy requires embedding "
                                   "dimension. Set using '/set embedding "
                                   "<50|100|200|300>'.\n");
                        }
                    } else {
                        printf("Usage: /set strategy "
                               "<typo|ngram|semantic|comma-separated>\n");
                    }
                } else if (!strncmp(sub, "threshold ", 10) || !strncmp(sub, "th ", 3)) {
                    char *val_str =
                        trim_string(sub + (!strncmp(sub, "threshold ", 10) ? 10 : 3));
                    threshold = atof(val_str);
                    printf("Semantic threshold set to: %.4f\n", threshold);
                } else if (!strncmp(sub, "tokens ", 7)) {
                    int tok = atoi(sub + 7);
                    if (tok > 0) {
                        max_tokens = tok;
                        printf("Max tokens set to: %d\n", max_tokens);
                    } else {
                        printf("Tokens must be a positive integer.\n");
                    }
                } else if (!strncmp(sub, "mode ", 5)) {
                    char *m = trim_string(sub + 5);
                    if (!strcmp(m, "query") || !strcmp(m, "pipeline")) {
                        mode = CLI_MODE_QUERY;
                        printf("Mode switched to: query (generate + post-process)\n");
                    } else if (!strcmp(m, "gen") || !strcmp(m, "generate")) {
                        mode = CLI_MODE_GENERATE;
                        printf("Mode switched to: generate (raw model output)\n");
                    } else if (!strcmp(m, "post") || !strcmp(m, "postprocess")) {
                        mode = CLI_MODE_POSTPROCESS;
                        printf("Mode switched to: postprocess (SQL correction only)\n");
                    } else if (!strcmp(m, "enc") || !strcmp(m, "encode")) {
                        mode = CLI_MODE_ENCODE;
                        printf("Mode switched to: encode (BPE tokenizer)\n");
                    } else {
                        printf("Unknown mode '%s'. Choose: query, gen, post, encode\n",
                               m);
                    }
                } else {
                    printf("Unknown setting '%s'. Supported: /set asset, /set db, /set "
                           "model, /set embedding, /set strategy, /set tokens, /set "
                           "mode\n",
                           sub);
                }
            } else if (!strncmp(cmd, "model ", 6)) {
                char *new_path = trim_string(cmd + 6);
                if (*new_path) {
                    if (model_loaded) {
                        release_model(&model);
                        model_loaded = 0;
                    }
                    strncpy(model_path, new_path, sizeof(model_path) - 1);
                    printf("Loading model: %s ...\n", model_path);
                    model = load_model(model_path);
                    model_loaded = 1;
                    printf("Model loaded successfully.\n");
                } else {
                    printf("Usage: /model <path_to_model.gguf>\n"
                           "Tip: Use '/ls model' to inspect available models.\n");
                }
            } else if (!strncmp(cmd, "db ", 3)) {
                char *new_db = trim_string(cmd + 3);
                if (*new_db) {
                    if (dictionary) {
                        free_dictionary(dictionary, dict_size);
                        dictionary = NULL;
                        dict_size = 0;
                    }
                    strncpy(db_path, new_db, sizeof(db_path) - 1);
                    if (load_dictionary_from_sqlite(db_path, &dictionary, &dict_size) ==
                        0) {
                        printf("Database schema loaded (%zu items) from %s\n",
                               dict_size, db_path);
                    } else {
                        printf("Warning: failed to load schema from %s\n", db_path);
                    }
                } else {
                    printf("Usage: /db <path_to_sqlite.db>\n"
                           "Tip: Use '/ls db' to inspect available databases.\n");
                }
            } else if (!strncmp(cmd, "embedding ", 10) || !strncmp(cmd, "emb ", 4) ||
                       !strncmp(cmd, "dim ", 4)) {
                char *val_str =
                    trim_string(cmd + (!strncmp(cmd, "embedding ", 10) ? 10 : 4));
                int new_dim = atoi(val_str);
                if (new_dim == 50 || new_dim == 100 || new_dim == 200 ||
                    new_dim == 300) {
                    dim = new_dim;
                    dim_specified = 1;
                    printf("Embedding dimension set to: %d\n", dim);
                } else {
                    printf("Invalid dimension '%s'. Choose 50, 100, 200, or 300.\n"
                           "Tip: Use '/ls embedding' to inspect available files.\n",
                           val_str);
                }
            } else if (!strncmp(cmd, "strategy ", 9)) {
                char *new_strat = trim_string(cmd + 9);
                if (*new_strat) {
                    strncpy(strategy, new_strat, sizeof(strategy) - 1);
                    printf("Strategy set to: %s\n", strategy);
                }
            } else if (!strncmp(cmd, "tokens ", 7)) {
                int tok = atoi(cmd + 7);
                if (tok > 0) {
                    max_tokens = tok;
                    printf("Max tokens set to: %d\n", max_tokens);
                }
            } else if (!strncmp(cmd, "mode ", 5)) {
                char *m = trim_string(cmd + 5);
                if (!strcmp(m, "query") || !strcmp(m, "pipeline")) {
                    mode = CLI_MODE_QUERY;
                } else if (!strcmp(m, "gen") || !strcmp(m, "generate")) {
                    mode = CLI_MODE_GENERATE;
                } else if (!strcmp(m, "post") || !strcmp(m, "postprocess")) {
                    mode = CLI_MODE_POSTPROCESS;
                } else if (!strcmp(m, "enc") || !strcmp(m, "encode")) {
                    mode = CLI_MODE_ENCODE;
                }
                printf("Mode set to: %s\n", m);
            } else {
                printf("Unknown command '%s'. Type '/help' for available commands.\n",
                       line);
            }
            continue;
        }

        /* Execute prompt according to active mode */
        if (mode == CLI_MODE_POSTPROCESS) {
            if (strstr(strategy, "semantic") != NULL && !dim_specified) {
                printf("Error: 'semantic' strategy requires dim. Use '/set embedding "
                       "<50|100|200|300>'.\n");
                continue;
            }
            try_configure_semantic_path(db_path[0] ? db_path : model_path, dim);
            char *fixed = process_sql(line, strategy, (const char **)dictionary,
                                      dict_size, dim, threshold);
            printf("===> %s\n\n", fixed ? fixed : line);
            if (fixed) {
                free(fixed);
            }
            continue;
        }

        if (!model_loaded) {
            printf("Error: Model is not loaded. Use '/set model <path>' to load a GGUF "
                   "model first.\n");
            continue;
        }

        if (mode == CLI_MODE_ENCODE) {
            int capacity = model.config.context_length;
            int *token_ids = allocate_array(capacity, sizeof(int));
            int token_count = encode_text(&model, line, token_ids, capacity);
            printf("Tokens (%d): ", token_count);
            for (int i = 0; i < token_count; i++) {
                printf("%s%d", i ? " " : "", token_ids[i]);
            }
            puts("\n");
            free(token_ids);
            continue;
        }

        if (mode == CLI_MODE_GENERATE) {
            int *token_ids = allocate_array(model.config.context_length, sizeof(int));
            int prompt_count = prepare_prompt_tokens(&model, line, token_ids);
            int token_count = prompt_count;
            const char *stop_reason =
                generate_tokens(&model, token_ids, &token_count, max_tokens);
            printf("Generated SQL: ");
            for (int i = prompt_count; i < token_count; i++) {
                int token_id = token_ids[i];
                fwrite(model.tokenizer.token_bytes[token_id], 1,
                       model.tokenizer.token_byte_lengths[token_id], stdout);
            }
            printf(" (stop=%s)\n\n", stop_reason);
            free(token_ids);
            continue;
        }

        if (mode == CLI_MODE_QUERY) {
            if (strstr(strategy, "semantic") != NULL && !dim_specified) {
                printf("Error: 'semantic' strategy requires dim. Use '/set embedding "
                       "<50|100|200|300>'.\n");
                continue;
            }
            int *token_ids = allocate_array(model.config.context_length, sizeof(int));
            int prompt_count = prepare_prompt_tokens(&model, line, token_ids);
            int token_count = prompt_count;
            const char *stop_reason =
                generate_tokens(&model, token_ids, &token_count, max_tokens);

            size_t raw_sql_len = 0;
            for (int i = prompt_count; i < token_count; i++) {
                int token_id = token_ids[i];
                raw_sql_len += model.tokenizer.token_byte_lengths[token_id];
            }
            char *raw_sql = allocate_array(raw_sql_len + 1, 1);
            size_t offset = 0;
            for (int i = prompt_count; i < token_count; i++) {
                int token_id = token_ids[i];
                size_t len = model.tokenizer.token_byte_lengths[token_id];
                memcpy(raw_sql + offset, model.tokenizer.token_bytes[token_id], len);
                offset += len;
            }
            raw_sql[raw_sql_len] = '\0';
            free(token_ids);

            try_configure_semantic_path(db_path[0] ? db_path : model_path, dim);
            char *fixed_sql = process_sql(raw_sql, strategy, (const char **)dictionary,
                                          dict_size, dim, threshold);
            printf("[model] %s (stop=%s)\n", raw_sql, stop_reason);
            printf("===> %s\n\n", fixed_sql ? fixed_sql : raw_sql);

            if (fixed_sql) {
                free(fixed_sql);
            }
            free(raw_sql);
        }
    }

    if (model_loaded) {
        release_model(&model);
    }
    if (dictionary) {
        free_dictionary(dictionary, dict_size);
    }
    free_semantic_cache();
}

static void dispatch_cli_command(const Model *model, const CliOptions *options) {
    switch (options->mode) {
    case CLI_MODE_LOGITS:
        run_logits_command(model, options);
        break;
    case CLI_MODE_ENCODE:
        run_encode_command(model, options);
        break;
    case CLI_MODE_GENERATE:
        run_generation_command(model, options);
        break;
    case CLI_MODE_QUERY:
        run_query_command(model, options);
        break;
    case CLI_MODE_JSON:
        run_json_command(model, options);
        break;
    case CLI_MODE_POSTPROCESS:
    case CLI_MODE_INTERACTIVE:
        break;
    }
}

int main(int argc, char **argv) {
#ifdef _WIN32
    /* Windows text mode must not rewrite generated token bytes as CRLF. */
    _setmode(1, _O_BINARY);
#endif

    /* 1. No arguments: launch interactive shell if running in terminal, else print
     * usage */
    if (argc == 1) {
        if (IS_TERMINAL()) {
            run_interactive_shell(NULL, NULL, "typo,ngram", 300, 0.5,
                                  DEFAULT_MAX_NEW_TOKENS);
            return 0;
        }
        print_usage(argv[0]);
        return 2;
    }

    /* 2. Help request */
    if (!strcmp(argv[1], "-h") || !strcmp(argv[1], "--help")) {
        print_usage(argv[0]);
        return 0;
    }

    /* 3. Standalone interactive flags without model */
    if (!strcmp(argv[1], "-i") || !strcmp(argv[1], "--interactive")) {
        const char *db = NULL;
        for (int i = 2; i < argc; i++) {
            if ((!strcmp(argv[i], "--db") || !strcmp(argv[i], "-b")) && i + 1 < argc) {
                db = argv[++i];
            }
        }
        run_interactive_shell(NULL, db, "typo,ngram", 300, 0.5, DEFAULT_MAX_NEW_TOKENS);
        return 0;
    }

    /* 4. Standalone postprocess request without model */
    if (run_standalone_postprocess(argc, argv)) {
        return 0;
    }

    /* 5. Check if single model argument was provided without mode */
    if (argc == 2) {
        print_usage(argv[0]);
        return 2;
    }

    /* 6. Model + interactive flag: sql-cli MODEL.gguf -i */
    if (!strcmp(argv[2], "-i") || !strcmp(argv[2], "--interactive")) {
        const char *db = NULL;
        for (int i = 3; i < argc; i++) {
            if ((!strcmp(argv[i], "--db") || !strcmp(argv[i], "-b")) && i + 1 < argc) {
                db = argv[++i];
            }
        }
        run_interactive_shell(argv[1], db, "typo,ngram", 300, 0.5,
                              DEFAULT_MAX_NEW_TOKENS);
        return 0;
    }

    /* 7. Load model before checking mode to preserve error_test specification order */
    Model model = load_model(argv[1]);
    if (!model.tensors) {
        fprintf(stderr, "error: %s\n", sql_get_last_error());
        return 1;
    }
    CliOptions options = parse_cli_options(argc, argv);
    dispatch_cli_command(&model, &options);
    if (options.allocated_text) {
        free(options.allocated_text);
    }
    release_model(&model);
    free_semantic_cache();
    return 0;
}
