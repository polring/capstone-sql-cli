/* Compare final-position logits from the public C ABI to the exporter fixtures.
 * The managed suite consumes the encode/generate rows in these same files. */
#include "../sql_engine.h"
#include <ctype.h>
#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define LOGIT_ABSOLUTE_TOLERANCE 2e-5
#define LOGIT_RELATIVE_TOLERANCE 2e-5

static char *read_text_file(const char *path) {
    FILE *file = fopen(path, "rb");
    if (!file) {
        return NULL;
    }
    fseek(file, 0, SEEK_END);
    long size = ftell(file);
    rewind(file);
    char *text = size < 0 ? NULL : malloc((size_t)size + 1);
    if (text && fread(text, 1, (size_t)size, file) == (size_t)size) {
        text[size] = 0;
    } else {
        free(text);
        text = NULL;
    }
    fclose(file);
    return text;
}

static int count_values(const char *values) {
    int count = 0;
    int in_value = 0;
    for (; *values; values++) {
        int is_value = !isspace((unsigned char)*values);
        if (is_value && !in_value) {
            count++;
        }
        in_value = is_value;
    }
    return count;
}

static int check_logits_case(const SqlModel *model, char *ids, char *expected) {
    int count = count_values(ids);
    int vocab_size = count_values(expected);
    int32_t *token_ids = calloc(count, sizeof(*token_ids));
    float *logits = calloc(vocab_size, sizeof(*logits));
    int ok = token_ids && logits;
    for (int i = 0; ok && i < count; i++) {
        char *end;
        token_ids[i] = (int32_t)strtol(ids, &end, 10);
        ok = end != ids;
        ids = end;
    }
    double elapsed_ms = -1;
    ok = ok &&
         sql_model_forward(model, token_ids, count, logits, vocab_size, &elapsed_ms) &&
         isfinite(elapsed_ms) && elapsed_ms >= 0;
    for (int i = 0; ok && i < vocab_size; i++) {
        char *end;
        double value = strtod(expected, &end);
        double tolerance =
            LOGIT_ABSOLUTE_TOLERANCE + LOGIT_RELATIVE_TOLERANCE * fabs(value);
        ok = end != expected && fabs((double)logits[i] - value) <= tolerance;
        expected = end;
    }
    free(logits);
    free(token_ids);
    return ok;
}

int main(int argc, char **argv) {
    if (argc != 3) {
        fprintf(stderr, "usage: %s MODEL.gguf CASES.txt\n", argv[0]);
        return 2;
    }
    SqlModel *model = sql_model_open(argv[1]);
    if (!model) {
        fprintf(stderr, "%s\n", sql_get_last_error());
        return 1;
    }
    char *cases = read_text_file(argv[2]);
    if (!cases) {
        fprintf(stderr, "error reading %s\n", argv[2]);
        sql_model_close(model);
        return 1;
    }
    int passed = 0, failed = 0, line_number = 0;
    for (char *line = cases; line && *line;) {
        char *next = strchr(line, '\n');
        if (next) {
            *next++ = 0;
        }
        line_number++;
        if (!strncmp(line, "logits\t", 7)) {
            char *ids = line + 7;
            char *expected = strchr(ids, '\t');
            if (expected) {
                *expected++ = 0;
            }
            if (expected && check_logits_case(model, ids, expected)) {
                passed++;
            } else {
                fprintf(stderr, "FAIL logits at %s:%d %s\n", argv[2], line_number,
                        sql_get_last_error());
                failed++;
            }
        }
        line = next;
    }
    printf("%s: %d logits cases passed, %d failed\n", argv[2], passed, failed);
    free(cases);
    sql_model_close(model);
    return failed || !passed;
}
