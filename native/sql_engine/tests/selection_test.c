/* Exercise token selection without exporting engine internals from the library. */
#include "../sql_engine.c"

int main(void) {
    const uint8_t mask[] = {1, 0, 1, 0, 1};
    float logits[] = {1, 50, 1, 1, 1};
    int eos = 3;
    if (select_greedy_token(logits, mask, 5, eos) != eos) {
        return 1;
    }
    logits[eos] = 0;
    return select_greedy_token(logits, mask, 5, eos) != 0;
}
