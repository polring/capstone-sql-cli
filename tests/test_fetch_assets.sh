#!/usr/bin/env bash
# Offline regression tests: curl copies tiny fixtures instead of using the network.
set -eu
set -o pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
fetch_script="${FETCH_ASSETS_SCRIPT:-$repository_root/scripts/fetch-assets}"
temporary_root=$(cd "${TMPDIR:-/tmp}" && pwd)
test_directory=$(mktemp -d "$temporary_root/fetch-assets-test.XXXXXX")

cleanup() {
    case "$test_directory" in
        "$temporary_root"/fetch-assets-test.*) rm -rf -- "$test_directory" ;;
    esac
}
trap cleanup EXIT

fail() {
    echo "FAIL: $scenario: $*" >&2
    cat "$output/run.log" >&2
    exit 1
}

mkdir -p "$test_directory/bin" "$test_directory/shasum-bin"
cat > "$test_directory/bin/curl" <<'MOCK'
#!/usr/bin/env bash
set -eu
destination=''
url=''
while [[ $# -gt 0 ]]; do
    case "$1" in
        -o) destination="$2"; shift 2 ;;
        -*) shift ;;
        *) url="$1"; shift ;;
    esac
done
filename="${url##*/}"
echo "$filename" >> "$REQUEST_LOG"
if [[ "${FAILED_FILE:-}" == "$filename" ]]; then
    printf 'partial download\n' > "$destination"
    exit 22
elif [[ "${CORRUPT_FILE:-}" == "$filename" ]]; then
    printf 'corrupted content\n' > "$destination"
else
    cp "$FIXTURE_DIRECTORY/$filename" "$destination"
fi
MOCK
chmod +x "$test_directory/bin/curl"

# A restricted PATH exercises the real shasum fallback even on GNU/Linux.
for command_name in bash awk mkdir mktemp mv rm rmdir cp; do
    ln -s "$(command -v "$command_name")" "$test_directory/shasum-bin/$command_name"
done
ln -s "$test_directory/bin/curl" "$test_directory/shasum-bin/curl"
if command -v shasum >/dev/null 2>&1; then
    ln -s "$(command -v shasum)" "$test_directory/shasum-bin/shasum"
    # Some installations use /usr/bin/env perl in the shasum shebang.
    if command -v perl >/dev/null 2>&1; then
        ln -s "$(command -v perl)" "$test_directory/shasum-bin/perl"
    fi
fi

write_checksums() {
    (
        cd "$FIXTURE_DIRECTORY"
        if command -v sha256sum >/dev/null 2>&1; then
            sha256sum model.gguf model_info.json shop.db semantic_dict_300d.bin
        else
            shasum -a 256 model.gguf model_info.json shop.db semantic_dict_300d.bin
        fi
    ) | awk '{ sub(/^\*/, "", $2); print $1 "  " $2 }' > "$FIXTURE_DIRECTORY/SHA256SUMS"
}

remove_checksum() {
    awk -v filename="$1" '$2 != filename' "$FIXTURE_DIRECTORY/SHA256SUMS" \
        > "$FIXTURE_DIRECTORY/updated.sha256"
    mv "$FIXTURE_DIRECTORY/updated.sha256" "$FIXTURE_DIRECTORY/SHA256SUMS"
}

assert_file_matches() {
    cmp -s "$FIXTURE_DIRECTORY/$1" "$output/$1" || fail "$1 differs from the release"
}

scenarios=(all model db embedding alias corrupt_model corrupt_metadata
    corrupt_db_all corrupt_embedding_all corrupt_db corrupt_embedding
    missing_db_checksum duplicate_db_checksum missing_model_checksum
    missing_dictionary_all missing_dictionary_explicit
    failed_manifest failed_db failed_embedding stale_unselected replace_stale_db
    binary_checksum crlf_checksum shasum_model shasum_db shasum_corrupt_db)
if [[ $# -gt 0 ]]; then scenarios=("$@"); fi

passed=0
for scenario in "${scenarios[@]}"; do
    if [[ "$scenario" == shasum_* ]] && ! command -v shasum >/dev/null 2>&1; then
        echo "SKIP: $scenario (shasum not installed)"
        continue
    fi

    export FIXTURE_DIRECTORY="$test_directory/$scenario/release"
    output="$test_directory/$scenario/output with spaces"
    export REQUEST_LOG="$test_directory/$scenario/requests.log"
    export CORRUPT_FILE='' FAILED_FILE=''
    mkdir -p "$FIXTURE_DIRECTORY" "$output"
    printf 'tiny model\n' > "$FIXTURE_DIRECTORY/model.gguf"
    printf '{}\n' > "$FIXTURE_DIRECTORY/model_info.json"
    printf 'tiny database\n' > "$FIXTURE_DIRECTORY/shop.db"
    printf 'tiny dictionary\n' > "$FIXTURE_DIRECTORY/semantic_dict_300d.bin"
    write_checksums

    target=all
    expected=success
    error_file=''
    preserve_existing=true
    execution_path="$test_directory/bin:$PATH"
    case "$scenario" in
        model|db|embedding) target="$scenario" ;;
        alias) target=emb ;;
        corrupt_model) export CORRUPT_FILE=model.gguf ;;
        corrupt_metadata) export CORRUPT_FILE=model_info.json ;;
        corrupt_db_all) export CORRUPT_FILE=shop.db; preserve_existing=false ;;
        corrupt_embedding_all) export CORRUPT_FILE=semantic_dict_300d.bin; preserve_existing=false ;;
        corrupt_db) target=db; export CORRUPT_FILE=shop.db ;;
        corrupt_embedding) target=embedding; export CORRUPT_FILE=semantic_dict_300d.bin ;;
        missing_db_checksum) target=db; remove_checksum shop.db; error_file=shop.db ;;
        duplicate_db_checksum)
            target=db
            awk '$2 == "shop.db"' "$FIXTURE_DIRECTORY/SHA256SUMS" > "$FIXTURE_DIRECTORY/duplicate"
            cat "$FIXTURE_DIRECTORY/duplicate" >> "$FIXTURE_DIRECTORY/SHA256SUMS"
            error_file=shop.db
            ;;
        missing_model_checksum) target=model; remove_checksum model.gguf; error_file=model.gguf ;;
        missing_dictionary_all|missing_dictionary_explicit)
            remove_checksum semantic_dict_300d.bin
            rm "$FIXTURE_DIRECTORY/semantic_dict_300d.bin"
            if [[ "$scenario" == missing_dictionary_explicit ]]; then
                target=embedding
                error_file=semantic_dict_300d.bin
            fi
            ;;
        failed_manifest) export FAILED_FILE=SHA256SUMS ;;
        failed_db) target=db; export FAILED_FILE=shop.db ;;
        failed_embedding) export FAILED_FILE=semantic_dict_300d.bin ;;
        stale_unselected)
            target=model
            printf 'old database\n' > "$output/shop.db"
            printf 'old dictionary\n' > "$output/semantic_dict_300d.bin"
            ;;
        replace_stale_db) target=db; printf 'old database\n' > "$output/shop.db" ;;
        binary_checksum)
            awk '{ print $1 " *" $2 }' "$FIXTURE_DIRECTORY/SHA256SUMS" > "$FIXTURE_DIRECTORY/binary"
            mv "$FIXTURE_DIRECTORY/binary" "$FIXTURE_DIRECTORY/SHA256SUMS"
            ;;
        crlf_checksum)
            awk '{ printf "%s\r\n", $0 }' "$FIXTURE_DIRECTORY/SHA256SUMS" > "$FIXTURE_DIRECTORY/crlf"
            mv "$FIXTURE_DIRECTORY/crlf" "$FIXTURE_DIRECTORY/SHA256SUMS"
            ;;
        shasum_model) target=model; execution_path="$test_directory/shasum-bin" ;;
        shasum_db) target=db; execution_path="$test_directory/shasum-bin" ;;
        shasum_corrupt_db)
            target=db
            execution_path="$test_directory/shasum-bin"
            export CORRUPT_FILE=shop.db
            ;;
    esac

    if [[ -n "$CORRUPT_FILE" || -n "$FAILED_FILE" || -n "$error_file" ]]; then
        expected=failure
        error_file="${error_file:-${CORRUPT_FILE:-$FAILED_FILE}}"
        if [[ "$preserve_existing" == true ]]; then
            printf 'keep existing file\n' > "$output/$error_file"
        fi
    fi

    status=0
    PATH="$execution_path" "$BASH" "$fetch_script" stage2-contract2 "$target" "$output" \
        > "$output/run.log" 2>&1 || status=$?

    if [[ "$expected" == failure ]]; then
        [[ "$status" -ne 0 ]] || fail 'expected a nonzero exit code'
        grep -Fq "$error_file" "$output/run.log" || fail 'error did not name the file'
        if grep -q '^Done\.' "$output/run.log"; then fail 'reported success after failure'; fi
        if [[ "$preserve_existing" == true ]]; then
            [[ "$(cat "$output/$error_file")" == 'keep existing file' ]] || fail 'overwrote existing file'
        else
            [[ ! -e "$output/$error_file" ]] || fail 'published an unverified file'
        fi
    else
        [[ "$status" -eq 0 ]] || fail "unexpected exit code $status"
        grep -q '^Done\.' "$output/run.log" || fail 'missing success message'
        assert_file_matches SHA256SUMS
        case "$target" in
            all)
                assert_file_matches model.gguf
                assert_file_matches model_info.json
                assert_file_matches shop.db
                if [[ "$scenario" == missing_dictionary_all ]]; then
                    grep -q 'skipping embedding' "$output/run.log" || fail 'missing skip warning'
                    [[ ! -e "$output/semantic_dict_300d.bin" ]] || fail 'created absent dictionary'
                    if grep -qx semantic_dict_300d.bin "$REQUEST_LOG"; then fail 'requested absent dictionary'; fi
                else
                    assert_file_matches semantic_dict_300d.bin
                fi
                ;;
            model)
                assert_file_matches model.gguf
                assert_file_matches model_info.json
                if grep -Eq '^(shop.db|semantic_dict_300d.bin)$' "$REQUEST_LOG"; then fail 'downloaded unselected files'; fi
                ;;
            db) assert_file_matches shop.db ;;
            embedding|emb) assert_file_matches semantic_dict_300d.bin ;;
        esac
        if [[ "$scenario" == stale_unselected ]]; then
            [[ "$(cat "$output/shop.db")" == 'old database' ]] || fail 'changed unselected DB'
            [[ "$(cat "$output/semantic_dict_300d.bin")" == 'old dictionary' ]] || fail 'changed unselected dictionary'
        fi
    fi
    if compgen -G "$output/.fetch-assets.*" >/dev/null; then fail 'left temporary downloads'; fi
    passed=$((passed + 1))
    echo "PASS: $scenario"
done

echo "$passed offline asset-download tests passed."
