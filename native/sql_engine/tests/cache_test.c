/* Exercise the request-private cache without exporting it from the shared library. */
#include "../sql_engine.c"

static int check_cached_logits(const Model *model) {
    int length = model->config.context_length;
    int vocab = model->config.vocab_size;
    int32_t *ids = allocate_array(length, sizeof(*ids));
    float *reference = allocate_array(vocab, sizeof(*reference));
    float *actual = allocate_array(vocab, sizeof(*actual));
    KvCache cache = create_kv_cache(model, length);
    int ok = 1;
    for (int position = 0; ok && position < length; ++position) {
        ids[position] = (position * 37 + 17) % vocab;
        cached_forward_token(model, &cache, ids[position], actual);
        double time_ms;
        ok = sql_model_forward(model, ids, position + 1, reference, vocab, &time_ms);
        for (int id = 0; ok && id < vocab; ++id) {
            ok = fabs(actual[id] - reference[id]) <= 2e-5 + 2e-5 * fabs(reference[id]);
            if (!ok) {
                fprintf(stderr, "position %d id %d: cached %g, full-prefix %g\n",
                        position, id, actual[id], reference[id]);
            }
        }
    }
    release_kv_cache(&cache);
    free_array(actual);
    free_array(reference);
    free_array(ids);
    return ok;
}

static int check_selection(void) {
    const uint8_t mask[] = {1, 0, 1, 0, 1};
    float logits[] = {1, 50, 1, 1, 1};
    int eos = 3;
    if (select_greedy_token(logits, mask, 5, eos) != eos) {
        return 0;
    }
    logits[eos] = 0;
    return select_greedy_token(logits, mask, 5, eos) == 0;
}

int main(int argc, char **argv) {
    if (argc != 2 || !check_selection()) {
        fprintf(stderr, "expected fixture model and valid greedy tie handling\n");
        return 1;
    }
    Model *model = sql_model_open(argv[1]);
    if (!model) {
        fprintf(stderr, "%s\n", sql_get_last_error());
        return 1;
    }
    int ok = check_cached_logits(model);
    sql_model_close(model);
    return !ok;
}
