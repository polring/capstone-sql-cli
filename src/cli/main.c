/* Standalone CLI for capstone_sql inference engine */
#include "../core/include/engine.h"
#include <errno.h>
#include <limits.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#ifdef _WIN32
#include <io.h>
#include <fcntl.h>
#endif

typedef enum { CLI_LOGITS, CLI_ENCODE, CLI_GENERATE } CliMode;

typedef struct {
    CliMode mode;
    const char *text;
    int max_new_tokens;
    char **token_arguments;
    int token_argument_count;
} CliOptions;

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

static int is_encode_request(int argc, char **argv) {
    return !strcmp(argv[2], "--encode") && argc == 4;
}

static int is_generation_request(int argc, char **argv) {
    return !strcmp(argv[2], "--generate") && (argc == 4 || argc == 5);
}

static CliOptions parse_cli_options(int argc, char **argv) {
    CliOptions options = {0};
    if (!strcmp(argv[2], "--logits")) {
        options.mode = CLI_LOGITS;
        options.token_arguments = argv + 3;
        options.token_argument_count = argc - 3;
    } else if (is_encode_request(argc, argv)) {
        options.mode = CLI_ENCODE;
        options.text = argv[3];
    } else if (is_generation_request(argc, argv)) {
        options.mode = CLI_GENERATE;
        options.text = argv[3];
        options.max_new_tokens =
            argc == 5 ? parse_nonnegative_integer(argv[4]) : DEFAULT_MAX_NEW_TOKENS;
    } else {
        fail("unknown mode or invalid arguments");
    }
    return options;
}

static void print_usage(const char *program_name) {
    fprintf(stderr,
            "usage: %s MODEL.gguf --logits ID... | --encode TEXT | --generate "
            "QUESTION [max_new_tokens]\n",
            program_name);
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

static void dispatch_cli_command(const Model *model, const CliOptions *options) {
    switch (options->mode) {
    case CLI_LOGITS:
        run_logits_command(model, options);
        break;
    case CLI_ENCODE:
        run_encode_command(model, options);
        break;
    case CLI_GENERATE:
        run_generation_command(model, options);
        break;
    }
}

int main(int argc, char **argv) {
#ifdef _WIN32
    /* Windows text mode must not rewrite generated token bytes as CRLF. */
    _setmode(1, _O_BINARY);
#endif
    if (argc < 3) {
        print_usage(argv[0]);
        return 2;
    }
    /* Load before validating the mode to preserve CLI error ordering. */
    Model model = load_model(argv[1]);
    CliOptions options = parse_cli_options(argc, argv);
    dispatch_cli_command(&model, &options);
    release_model(&model);
    return 0;
}
