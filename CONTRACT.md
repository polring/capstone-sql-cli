# capstone_sql GGUF 계약 (버전 2)

GUI가 읽는 파일은 GGUF v3 컨테이너에 `capstone_sql` 계약 v2의 가중치와 어휘를 담는다. 단일 모델, 앞모델, 뒷모델은 같은 C 추론기를 사용한다. C#은 각 역할의 GGUF에서 토크나이저 메타데이터를 읽고 C는 같은 파일에서 모델 구조와 가중치를 읽는다. 파일 형식의 출처는 학습 저장소의 `src/exporter.py`이며, 이 저장소의 판독기와 fixture가 현재 앱의 동작을 정의한다. [학습 저장소](https://github.com/polring/capstone-design-1) · [C# 메타데이터 판독기](app/Interop/SqlEngine/GgufContract.cs) · [C 추론기](native/sql_engine/sql_engine.c)

## 1. 버전 규칙

- 내보낸 파일의 `capstone_sql.contract_version`은 C# 판독기와 C 추론기가 지원하는 값과 일치해야 한다. 이중 모델의 단계 준비 검사에서는 아키텍처와 계약 버전을 먼저 읽고, 그 역할을 처음 실행할 때 전체 메타데이터와 텐서를 검사한다.
- 메타데이터 키나 텐서 이름, RoPE 회전 규칙, 정규화 위치, 토큰 분할 방식 등 기존 파일의 해석이 달라지면 계약 버전을 올린다. 지원 범위 안에서 모델 크기, 어휘 크기, seed·merge 내용, eps·theta 값만 바꾸면 같은 버전을 사용한다.
- 새 버전을 지원할 때는 그 버전의 exporter가 생성한 GGUF와 기대값을 `fixtures/v<새 버전>/`에 함께 둔다. 지원 중인 각 버전은 해당 fixture로 검증한다.

| 버전 | 내용 |
| --- | --- |
| 1 | 이전 계약. 지금은 거부하므로 원래 체크포인트에서 다시 내보낸다 |
| 2 | 특수 토큰 ID, seed·merge 시작 ID를 메타데이터로 전달 (현재) |

## 2. 파일 형식

little-endian GGUF v3 컨테이너, F32 텐서(`GGML_TYPE_F32=0`), 32바이트 정렬을 사용한다. 현재 C 로더의 모델 파일 크기 상한은 1 GiB다. [GGUF 명세](https://github.com/ggml-org/ggml/blob/master/docs/gguf.md)

**메타데이터 19개** (전부 필수)

| 키 | 타입 | 내용 |
| --- | --- | --- |
| `general.architecture` | string | `capstone_sql` |
| `general.alignment` | uint32 | 32 |
| `capstone_sql.contract_version` | uint32 | 2 |
| `capstone_sql.vocab_size`, `dim`, `layers`, `heads`, `ffn_dim`, `context` | uint32 | 구조 |
| `capstone_sql.eps`, `theta` | float32 | RMSNorm epsilon, RoPE theta |
| `capstone_sql.tokenizer.seeds` | string 배열 | seed 토큰 (ID = `seed_base` + 순서) |
| `capstone_sql.tokenizer.merges` | uint32 배열 | `[a, b, new_id, ...]`, 학습 순서 |
| `capstone_sql.tokenizer.pad_id`, `bos_id`, `eos_id`, `sep_id` | uint32 | 특수 토큰 ID |
| `capstone_sql.tokenizer.seed_base`, `merge_base` | uint32 | seed·merge 시작 ID |

표의 같은 행에서 축약한 키는 첫 키의 접두사를 공유한다. 예를 들어 `heads`의 전체 키는 `capstone_sql.heads`이고 `bos_id`는 `capstone_sql.tokenizer.bos_id`다. C# 토크나이저 판독기는 표의 19개 키와 타입을 검사한다. C 로더는 구조와 계산에 쓰는 필드를 검증하고 토크나이저 메타데이터는 건너뛴다.

- 구조 차원은 1~131072 범위의 정수다. 어휘 크기는 256 이상, 층 수는 256 이하, 문맥 길이는 4096 이하이며, `dim`은 `heads`로 나누어떨어지고 head 차원은 짝수여야 한다. `eps`와 `theta`는 유한한 양수다.
- ID 0~255는 바이트다. 네 특수 ID는 256 이상 `merge_base` 미만이며 서로 다르다. seed와 겹치지 않아야 한다. seed는 `seed_base`부터, merge는 `merge_base`부터 연속으로 배정하며 각 범위는 어휘 안에 있어야 한다.
- seed는 두 글자 이상의 서로 다른 ASCII 문자열이다. 앞뒤 공백과 NUL은 허용하지 않는다. 각 merge의 `new_id`는 `merge_base`부터 1씩 증가하고, 피연산자는 그보다 앞서 정의되어 바이트로 복원할 수 있는 ID여야 한다. 한 토큰의 복원 길이는 최대 1 MiB다.

**텐서** (이름은 PyTorch `state_dict` 그대로. 데이터는 `[out, in]` row-major이고 GGUF 차원 표기만 fastest-first로 뒤집는다)

| 텐서 | shape |
| --- | --- |
| `embedding.embedding.weight` | `[vocab_size, dim]` |
| `blocks.N.attn_norm.weight`, `blocks.N.ffn_norm.weight` | `[dim]` |
| `blocks.N.attn.qkv_proj.weight` | `[3*dim, dim]`, 행 순서 Q → K → V |
| `blocks.N.attn.out_proj.weight` | `[dim, dim]` |
| `blocks.N.ffn.w1.weight` (gate), `blocks.N.ffn.w3.weight` (up) | `[ffn_dim, dim]` |
| `blocks.N.ffn.w2.weight` (down) | `[dim, ffn_dim]` |
| `final_norm.weight` | `[dim]` |
| `lm_head.weight` | `[vocab_size, dim]` |

파일에는 정확히 `3 + 7 × layers`개의 텐서가 들어간다. 각 텐서의 이름은 고유하고 데이터 범위는 파일 안에서 서로 겹치지 않으며 모든 가중치는 유한해야 한다. 임베딩과 출력층의 가중치가 같아도 파일에는 두 텐서로 따로 들어간다.

## 3. 모델 계산

선형층에 bias는 없다. `x`는 위치별 `dim` 벡터, `W x`는 `[out, in]` 행렬 곱이다.

```text
h = embedding[token_id]                                     # 위치 0..T-1
각 블록 N = 0..layers-1 (Pre-LN):
    a = RMSNorm(h, attn_norm)
    q, k, v = split(qkv_proj · a, 3)                        # Q, K, V 순서로 dim 씩
    q, k = RoPE(q, position), RoPE(k, position)             # V 는 회전하지 않음
    head 별: softmax(q·kᵀ / sqrt(head_dim), causal) · v      # 위치 i 는 0..i 만 참조
    h = h + out_proj · concat(heads)
    f = RMSNorm(h, ffn_norm)
    h = h + w2 · (silu(w1 · f) * (w3 · f))                  # SwiGLU, silu(x) = x · sigmoid(x)
logits = lm_head · RMSNorm(h[T-1], final_norm)              # 마지막 위치만
```

- **RMSNorm**은 `x / sqrt(mean(x²) + eps) * weight`를 계산한다. `eps`는 `capstone_sql.eps` 값이다.
- **head 분할**은 `dim`을 head 0부터 순서대로 `head_dim`씩 자른다.
- **RoPE**는 head 안에서 인접한 두 차원 `(2i, 2i+1)`을 한 쌍으로 회전한다.
  각도는 `position / theta^(2i / head_dim)`, `position`은 0부터 센다.
  `(x0, x1) → (x0·cos − x1·sin, x0·sin + x1·cos)`.

## 4. 토크나이저 (byte-level BPE)

C#은 해당 GGUF의 seed·merge와 특수 토큰 ID를 이용해 ASCII 입력을 앞에서부터 조각낸다. 문자와 seed는 대소문자를 구분해 비교한다. [토크나이저 구현](app/Interop/SqlEngine/SqlTokenizer.cs)

1. 현재 위치에서 시작하면서 앞뒤가 입력 경계이거나 `[A-Za-z0-9_]` 이외의 문자인 seed 중 가장 긴 것을 토큰 하나로 쓴다.
2. 조건에 맞는 seed가 없으면 연속된 ASCII 알파벳을 한 조각으로 자른다. 숫자·공백·그 밖의 문자는 각각 한 글자씩 자른다.
3. 조각 전체가 seed와 같으면 그 seed를 쓴다. 예를 들어 `stock_`의 `stock`은 1번의 경계 조건에서 제외되지만 3번에서 seed가 된다.
4. 그 외 조각은 바이트 ID로 바꾼 뒤 **merge를 학습 순서대로 하나씩** 조각 전체에 적용한다. 한 merge는 왼쪽부터
   겹치지 않게 적용한다. merge는 조각 경계를 넘지 않는다.

바이트 ID는 그 바이트로, seed는 등록된 문자열의 바이트로 복원한다. merge는 두 피연산자의 바이트를 이어 붙인다. 특수 토큰과 미사용 ID에는 복원할 바이트가 없다.

## 5. 입력 형식

```text
[bos_id] encode(model_input) [sep_id]
```

GUI에서 `model_input`은 해당 단계에 전달된 문자열이다. 단일 모델과 앞모델은 사용자의 원문을, 뒷모델은 직접 입력한 질문 또는 매퍼가 만든 일반화 질문을 대소문자 그대로 인코딩한다. 예를 들어 `show <TABLE1>`의 태그는 대문자로 전달된다. 입력에 NUL이나 비ASCII 문자가 있거나 BOS와 SEP를 포함한 길이가 `context`를 넘으면 오류를 반환한다.

`base` fixture에서 `hello hello`의 본문 토큰은 `[301, 32, 301]`이고, 실제로 C에 전달하는 프롬프트는 `[257, 301, 32, 301, 259]`다. 같은 fixture의 과거 생성 기준 중 `HELLO STOCK` 행은 소문자 `hello stock`을 넣었을 때의 기대 바이트를 담고 있다. 테스트는 이 행과 대문자 원문으로 생성한 결과를 각각 검증한다. [base 사례](fixtures/v2/base/cases.txt) · [C# 계약 테스트](tests/SqlApp.Tests/ContractV2Checks.cs)

## 6. 디코딩

- C는 greedy 방식으로 다음 토큰을 선택한다. 입력을 순서대로 한 번 처리해 각 층의 키(K)와 값(V)을 캐시에 저장하고 마지막 위치에서 첫 출력 토큰을 예측한다. 이후 예측이 필요할 때는 직전에 생성한 토큰 한 개만 모델에 입력한다. 그 위치의 쿼리(Q)는 캐시에 있는 이전 위치의 키·값까지 참조한다.
- 선택 대상은 `eos_id`와 바이트 복원 마스크가 허용하는 ID다. 최대 점수가 같으면 EOS가 우선이고, 그 외의 동점에서는 가장 작은 ID가 우선이다.
- EOS를 선택하면 종료 사유는 `eos`다. EOS 없이 한도에 도달하면 `max_new_tokens`와 남은 문맥 길이 중 먼저 소진되는 쪽에 따라 `max_new_tokens` 또는 `context`를 반환한다. 두 한도가 같으면 `max_new_tokens`가 우선이다.
- C#은 생성된 토큰의 바이트를 이어 붙이고(`eos_id` 제외), GUI와 파이프라인에는 UTF-8로 디코딩한 문자열과 종료 사유를 전달한다.

### 네이티브 호출과 시간 측정

C#은 모델 역할별로 토크나이저와 `sql_model_open`이 반환한 네이티브 핸들을 보관한다. C#이 메타데이터를 읽은 뒤 C는 파일을 임시 버퍼로 읽어 F32 가중치를 적재하고 그 버퍼를 해제한다. 가중치는 네이티브 핸들이 소유한다. 같은 역할의 파일 경로·수정 시각·크기가 같으면 다음 질문에서도 그 핸들을 재사용한다. 핸들을 교체하거나 서비스를 종료할 때 `sql_model_close`를 호출한다. [모델 로딩과 수명](app/Services/InferenceService.cs)

모델 한 단계마다 C#은 `sql_model_generate_ids`를 한 번 호출한다.

| 방향 | 전달하는 값 |
| --- | --- |
| C# → C | 모델 핸들, 프롬프트 ID 배열과 개수, 어휘 크기만큼의 복원 마스크와 길이, EOS ID, 생성 한도, 출력 ID 버퍼와 용량 |
| C → C# | 출력 버퍼에 기록한 생성 ID, 실제 기록 개수, 종료 사유, 네이티브 계산 시간(ms) |

C#이 확보하는 출력 용량은 `min(max_new_tokens, context - prompt_count)`이고, 반환된 개수만큼의 ID만 디코딩한다. 예를 들어 용량이 48이고 생성한 ID가 17개면 앞의 17개를 읽는다. 이중 모델에서 매핑 후보 수가 늘어도 뒷모델에는 공통 일반화 질문 한 개가 들어간다. C# 후처리기가 반환된 태그 SQL을 각 후보에 적용한다. [C API](native/sql_engine/sql_engine.h) · [생성 호출](app/Services/TokenGeneration.cs) · [구간 실행](app/Services/Pipeline/PipelineRunner.cs)

C는 단조 시계로 해당 호출의 KV 캐시와 계산 버퍼 준비, 프롬프트 계산, 후속 토큰 계산을 측정한다. EOS를 예측한 점수 계산도 포함한다. greedy 토큰 선택, 모델 로딩, C# 토큰화·디코딩, 매핑·복원, SQLite 조회는 측정에서 제외한다. 호출이 끝나면 KV 캐시를 해제한다. 생성 한도가 0이거나 입력이 문맥을 이미 채웠으면 모델을 계산하지 않고 0 ms를 반환한다. 앱에 표시하는 이중 모델 추론 시간은 실행한 모델 단계의 계산 시간 합계다.

`sql_model_forward`는 주어진 전체 토큰 구간을 계산해 마지막 위치의 점수를 반환하는 검증용 API다. 테스트에서 같은 위치의 캐시 기반 점수와 비교한다.

## 7. fixture

`fixtures/v2/`에는 학습 저장소의 exporter에서 가져온 기준 파일이 들어 있다. 세트마다 작은 모델 `model.gguf`와 Python으로 계산한 기대값 `cases.txt`가 있다. manifest에는 계약 버전과 각 모델·사례 파일의 SHA-256이 기록돼 있다. [v2 manifest](fixtures/v2/manifest.json)

| 세트 | 확인하는 것 |
| --- | --- |
| `base` | 기본 배치(특수 256~259, seed 260~297, merge 298~305), 토큰화 경계, 문맥 길이 종료 |
| `custom_layout` | 특수 ID 266~269, seed ID 270·271, merge ID 272를 사용하는 모델 |
| `no_seeds` | seed 0개 |
| `many_seeds` | seed 45개 |

`cases.txt`는 탭으로 필드를 구분한다. 입력 텍스트와 생성 결과는 바이트를 16진수로 적고, 빈 바이트열은 빈 칸으로 둔다. 생성 결과의 기대값은 UTF-8 문자열로 디코딩하기 전의 바이트와 비교한다.

```text
encode    <text>    <token id ...>
logits    <token id ...>    <logit ...>                       # vocab_size 개
generate  <max_new_tokens>    <question>    <output>    <eos | max_new_tokens | context>
```

네이티브 Release 빌드를 `build`에 만든 뒤 저장소 루트에서 실행한다.

```text
cmake -E chdir build ctest -C Release --output-on-failure
dotnet run --project tests/SqlApp.Tests -c Release
```

CTest는 네 세트의 마지막 위치 logits를 fixture와 대조하고, 각 위치에서 전체 구간 계산과 KV 캐시 계산을 비교한다. 잘못된 입력에 대한 C API 오류 처리도 검사한다. logits의 절대·상대 허용 오차는 각각 `2e-5`다. 로컬에서 실행하는 C# 테스트는 토큰 ID, 생성 바이트와 종료 사유의 정확한 일치, 대소문자 보존 및 네이티브 계산 시간을 검사한다. CI는 Linux에서 C 추론기를 빌드하고 CTest와 clang-format 검사를 실행한다. [네이티브 테스트 등록](native/CMakeLists.txt) · [CI 설정](.github/workflows/test.yml)

이중 모델 연동에는 `fixtures/dual/`의 원문·태그·SQL 예시를 사용한다. 테스트가 지정한 문자열을 생성하는 합성 GGUF 두 개를 만들어 실제 C 엔진을 호출하고, `home kitchen`의 두 매핑에서 뒷모델 호출 한 번으로 최종 SQL 두 개가 복원되는지 검사한다. 이 테스트의 검증 범위는 모델 단계 연결과 C# 복원 동작이다. [이중 모델 예시](fixtures/dual/category.json) · [네이티브 연동 테스트](tests/SqlApp.Tests/NativePipelineSmoke.cs)
