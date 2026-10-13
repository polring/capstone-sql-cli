#include "../core/include/engine.h"
#include "../core/postprocessor/core.h"
#include "../core/postprocessor/schema.h"
#include "../core/postprocessor/replacement.h"
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

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
    CLI_MODE_INTERACTIVE  /* Interactive REPL */
} CliMode;

typedef struct {
    CliMode mode;
    const char *model_path;
    const char *text;
    const char *db_path;
    const char *strategy;
    int dim;
    int dim_specified;
    double threshold;
    int max_new_tokens;
    char **token_arguments;
    int token_argument_count;
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

static void print_usage(const char *program_name) {
    fprintf(stderr,
            "usage:\n"
            "  %s MODEL.gguf [mode] [arguments...]\n"
            "  %s --postprocess SQL [options...]\n"
            "  %s [MODEL.gguf] -i\n\n"
            "modes:\n"
            "  --query QUESTION [--db PATH] [--strategy STRATS] [--dim DIM] "
            "[--max-tokens N]\n"
            "      (alias: --pipeline) Generate and correct SQL using schema\n"
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
            "  -t, --threshold FLOAT   Semantic similarity threshold (default: 0.5)\n"
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
    options.threshold = 0.5;

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
    double threshold = 0.5;

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

static void list_available_assets(const char *arg) {
    const char *filter = "all";
    const char *target_dir = NULL;

    if (arg && *arg) {
        if (!strcmp(arg, "model") || !strcmp(arg, "models") || !strcmp(arg, "db") ||
            !strcmp(arg, "dbs") || !strcmp(arg, "embedding") ||
            !strcmp(arg, "embeddings") || !strcmp(arg, "dim") || !strcmp(arg, "dims")) {
            filter = arg;
        } else {
            target_dir = arg;
        }
    }

    printf("\n=== Available Assets & Selectable Files ===\n");

    int found_models = 0, found_dbs = 0, found_embs = 0;

    if (target_dir) {
        print_assets_in_dir(target_dir, filter, &found_models, &found_dbs, &found_embs);
    } else {
        /* Default: scan assets/ and its subdirectories */
        char names[MAX_DIR_ENTRIES][256];
        int is_dirs[MAX_DIR_ENTRIES];
        int n = list_directory_entries("assets", names, is_dirs, MAX_DIR_ENTRIES);
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

    if (found_models == 0 && found_dbs == 0 && found_embs == 0) {
        printf("  (No matching model, database, or embedding files found in '%s')\n",
               target_dir ? target_dir : "assets/");
    }
    printf("===========================================\n\n");
}

static void print_shell_help(void) {
    printf(
        "\n--- Interactive Commands ---\n"
        "  /set db <path>         Load SQLite database schema (e.g. /set db shop.db)\n"
        "  /set model <path>      Load GGUF neural model (e.g. /set model model.gguf)\n"
        "  /set embedding <dim>   Set GloVe vector dimension (50, 100, 200, 300. "
        "Default: 300)\n"
        "  /set strategy <strats> Set correction strategies (e.g. "
        "typo,ngram,semantic)\n"
        "  /set threshold <float> Set semantic similarity threshold (default: 0.5)\n"
        "  /set tokens <number>   Set max new tokens to generate (default: 32)\n"
        "  /set mode <mode>       Switch mode: query (default), gen, post, enc\n"
        "  /ls [path|type]        Inspect directory and list selectable models "
        "(*.gguf),\n"
        "                         databases (*.db), and embeddings (*.bin)\n"
        "                         (Aliases: /list, /dir, /files. Examples: /ls, /ls "
        "model, /ls assets/tag)\n"
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
                       !strcmp(cmd, "embeddings") || !strcmp(cmd, "dims")) {
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
                }
                list_available_assets(arg);
            } else if (!strncmp(cmd, "set ", 4)) {
                char *sub = trim_string(cmd + 4);
                if (!strncmp(sub, "db ", 3)) {
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
                        printf("Usage: /set db <path_to_sqlite.db>\n");
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
                        printf("Usage: /set model <path_to_model.gguf>\n");
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
                        printf("Invalid dimension '%s'. Choose 50, 100, 200, or 300.\n",
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
                    printf("Unknown setting '%s'. Supported: /set db, /set model, /set "
                           "embedding, /set strategy, /set tokens, /set mode\n",
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
                    printf("Invalid dimension. Choose 50, 100, 200, or 300.\n");
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

    /* 5. Check if single model argument was provided */
    if (argc == 2) {
        /* If running interactively in terminal, start shell with this model */
        if (IS_TERMINAL() && argv[1][0] != '-') {
            run_interactive_shell(argv[1], NULL, "typo,ngram", 300, 0.5,
                                  DEFAULT_MAX_NEW_TOKENS);
            return 0;
        }
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
    CliOptions options = parse_cli_options(argc, argv);
    dispatch_cli_command(&model, &options);
    release_model(&model);
    free_semantic_cache();
    return 0;
}
