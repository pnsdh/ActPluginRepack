# 동작 원리

이 도구는 소스를 다시 빌드하지 않고, **최신 공식 플러그인** 안의 리소스와 상수만 이번 게임 버전에 맞게 바꾼다. 바꾸는 것은
opcode / 스킬명 오버라이드 / 게임 데이터 / 패킷 후킹 시그니처다(파일 구조와 형식은 [재료 직접 넣기](ManualInputs.md)).

- [재료 자동 준비 — `prepare`](#재료-자동-준비--prepare)
- [패치가 필요한지 판단](#패치가-필요한지-판단)
- [opcode](#opcode) · [게임 데이터 (CSV)](#게임-데이터-csv) · [옛 exe 와 스냅샷](#옛-exe-와-스냅샷) · [시그니처 자동 복구](#시그니처-자동-복구)
- [ACT 에 설치 / 되돌리기](#act-에-설치--되돌리기)
- [네트워크](#네트워크) · [GitHub API 한도](#github-api-한도) · [그 밖의 동작](#그-밖의-동작)

## 재료 자동 준비 — `prepare`

GUI 의 확인 단계(또는 `prepare` 명령, `.\build.ps1 -Prepare`)가 하는 일. 실행 순서는 **빨리 알아야 하는 것부터**다:

1. **게임 버전 확인** — 클라이언트마다 공식 패치 서버의 최신 버전([Thaliak](https://thaliak.xiv.dev))과 비교한다.
   최신이 아닌 클라이언트는 이번 재료에서 빼고 알린다 — 옛 exe 로 분석하면 지금 서버와 맞지 않는 값이 나오고,
   업데이트는 런처 로그인(이용권 확인)이 필요해 이 도구가 할 수 없다. 모두 최신이 아니면 멈춘다. 뺀 클라이언트는
   `input\.state.json` 에 남아 build / status 가 건너뛴다. 확인하지 못하면(서버 응답 없음) 설치된 버전 그대로 진행한다.
2. **베이스 플러그인** — `input\plugin` 의 것이 없거나 [최신 공식 릴리스](https://github.com/ravahn/FFXIV_ACT_Plugin/releases)보다
   오래됐으면 받아서 바꾼다(이전 파일은 `input\plugin\_backup\<버전>\`). 이 도구는 "아직 업데이트되지 않은 지금 플러그인"을
   고치는 것이므로 베이스는 항상 최신 공식본이다.
3. **스냅샷 정리** — [옛 exe 와 스냅샷](#옛-exe-와-스냅샷).
4. **시그니처 점검 + 자동 복구** — [시그니처 자동 복구](#시그니처-자동-복구). 패킷 후크가 고쳐지지 않으면 빌드하지 않는다.
5. **opcode** — [opcode](#opcode).
6. **게임 데이터(CSV)** — [게임 데이터 (CSV)](#게임-데이터-csv).

단계마다 실패가 격리돼 하나가 실패해도(네트워크, 도구 미설치 등) 나머지는 계속하고, 끝에 단계별 결과를 요약한다.
CSV 는 이름 표시용이라 이번 버전 데이터를 구하지 못해도 경고만 하고 이전 CSV 로 빌드한다. 옵션과 종료 코드는 [CLI](CLI.md).

판단과 재료의 기준(게임 버전, 베이스 플러그인 버전, opcode 출처)은 `input\.state.json` 에 남고, `build` 는 이걸 지금 상태와
비교해 게임이 또 패치됐거나 플러그인이 바뀌었으면 "재료가 오래됨"을 알린다. 일부 단계만 다시 돌리면(`--no-opcodes` 등)
건너뛴 단계는 같은 버전의 이전 기록을 이어받는다.

**출처 우선순위의 원칙** — "지금 게임 버전과 정확히 일치함이 확인되는 것 중, 믿을 수 있고 준비 비용이 낮은 것 먼저".
같은 원칙인데 재료마다 결과가 다르다:

- **CSV 는 직접 추출이 먼저.** 설치된 클라이언트에서 뽑으면 버전이 정의상 일치하고 수 초~수십 초면 끝난다. 공개 저장소는
  갱신이 늦을 수 있고 버전 확인이 커밋 메시지·날짜 추정에 기대므로 뒤로 둔다.
- **opcode 는 버전이 확인된 공개본이 먼저, 직접 추출(로컬 opcodediff)은 항상 돌려 대조.** 로컬 opcodediff 는 지금 exe 와
  직전 출시 exe 만 있으면 되고(클라이언트당 약 5초) 패치 번호·대응표가 필요 없어, 공개본이 나오기 전(출시 직후)에는 이것으로
  고친다. 다만 vtable diff 는 큰 패치에서 가끔 틀리므로, IDA 로 항목마다 찾은 공개본(Yarukon)이나 확인된 karashiiro 가 있고
  값이 다르면 공개본을 쓴다(경고). 공개본 없이 고쳤으면 공개된 뒤 다시 확인하라고 안내한다. IDA 는 느리고 라이선스가 필요해
  다른 출처가 모두 없을 때만(간편 모드에서는 쓰지 않음), 먼저 쓰려면 `--ida`.

## 패치가 필요한지 판단

다음이 모두 참이면 "공식 플러그인이 이미 맞음 — 임시 패치가 필요 없음":

- 공식 플러그인/deucalion 의 원래 시그니처만으로(입력 없이) 패킷 후크가 모든 클라이언트에서 맞음
- 설정된 클라이언트마다, 이번 게임 버전용으로 구한 opcode 가 베이스 플러그인의 Machina opcode 표(26개)와 모두 같음 —
  출처에 빠진 이름이 있으면 비교할 수 없으므로 "판단할 수 없음"
- 공식 메모리 시그니처가 깨졌는데 교체 시그니처로 맞는 항목이 없음 (있으면 패치해야 그 기능이 산다)

게임 데이터는 표시용이라 판단에 넣지 않는다. 시그니처 점검 자체가 실패하면 "판단할 수 없음"으로 둔다 — 후크 상태를 모르는
채로 설치하지 않게. 공식 플러그인이 따라잡아 필요 없어진 교체 시그니처는, 자동 복구가 넣은 줄(`# auto:`)이면 지우고 직접
넣은 줄은 지우라고 알린다.

GUI 간편 모드는 이 판단에 따라: 필요하면 만들어 설치하고, 필요 없으면 ACT 에 옛 공식본이나 예전 패치본이 있을 때 공식
최신본을 설치하고(고급 모드는 결과 화면의 버튼으로만 — 일부러 낮은 버전을 둘 수 있으므로), 판단할 수 없으면 멈춘다.

## opcode

지역마다 출처를 골라 `input\opcodes` 와 [`extras\`](#부가-출력--extras) 에 쓴다. 출처 우선순위:

1. [Yarukon](https://github.com/Yarukon/FFXIVNetworkOpcodes) 공개본 — 클라이언트 버전 날짜(`output/CN_<날짜>/`)로 맞춘다.
   7.5 이후 중국/글로벌/한국 클라이언트는 같은 버전이면 opcode 가 같다.
2. [karashiiro](https://github.com/karashiiro/FFXIVOpcodes) — 지역별(대만 포함). 패치 번호(`7.57`)를 대응표로 게임 버전과
   맞추고, 번호가 대응표에 아직 없으면 그 지역 원본 파일(`FFXIVOpcodes/Ipcs*.cs`)의 마지막 커밋 시각이 이번 버전이 처음 나온
   시각(Thaliak `first_offered`) 뒤인지로 판단한다. 한 파일에 모든 지역이 모이므로 지역별 파일로 본다(대만판만 갱신한 커밋에
   속지 않게). 통합 클라이언트는 같은 버전을 쓰므로 어느 지역에서든 처음 나온 시각이 기준이다 — 7.57 은 글로벌 05:51 출시,
   karashiiro 06:25 갱신, 한국 08:15 출시였다. 확인할 수 없으면(대만은 Thaliak 이 없음) 베이스 플러그인과의 차이로 미갱신 여부만 걸러 맨 뒤에 둔다.
3. **opcodediff 로컬 실행** — 지금 exe 와 직전 출시 exe 를 맞대 베이스 플러그인 값을 바꾼다(기본 켜짐, 클라이언트당 약
   5초). 클라이언트가 있는 통합 지역(중국/글로벌/한국)에서 항상 돌려 1~2 와 대조하고, 클라이언트가 없는 통합 지역은 같은 버전의
   이 결과를 쓴다. [아래](#opcodediff-로컬-실행).
4. [opcodediff](https://github.com/xivdev/opcodediff) 공개 diff — `diffs/<패치>.diff.json`(직전 → 이번 버전 opcode 대응표)을
   직전 버전 opcode 목록(Yarukon 직전 버전, 없으면 베이스 플러그인)에 적용한다. 3 과 같은 스크립트라 3 을 못 돌렸을 때 쓴다.
5. **IDA 직접 추출** — 1~4 가 모두 없고 IDA 가 있을 때(간편 모드에서는 쓰지 않음). [아래](#ida-직접-추출).

공개 저장소(1·2·4)는 출시 당일 몇 시간 안에 올라오는 편이다. 그 전에는 로컬 실행 결과로 고치고, 공개 자료 없이 고쳤으면
결과에 "공개되면 다시 확인"을 안내한다(GUI 결과 화면, CLI 경고). 패치 번호 ↔ 게임 버전 대응표는
opcodediff 의 `automation/ffxiv_versions_global.json` 을 쓴다(공개 diff 이름, karashiiro 와 xivapi CSV 의 버전 확인). 대응표는
opcodediff 가 diff 와 같은 PR 로 갱신하므로 공개 diff 보다 먼저 오지 않는다 — 로컬 실행·시그니처 복구는 대응표를 쓰지 않는다.
7.57 에서 다섯 출처 모두 공식 3.0.3.3 과 일치했다(글로벌/한국/중국 26/26).

- 고른 출처에 없는 항목은 이번 버전 것으로 확인된 다른 출처로 보충하고, 출처끼리 값이 다르면 경고한다.
- 서로 다른 이름이 같은 opcode 를 가리키게 되면(베이스에 남은 옛 값이 새 값과 겹침) 그 결과는 쓰지 않는다.
- 클라이언트가 설정에 없는(또는 최신이 아니라 뺀) 지역은 검증된 출처로 Machina 26개가 다 채워질 때만 쓰고, 못 구하면
  예전 prepare 가 남긴 파일을 지워 베이스 값을 쓰게 한다.
- 베이스에 이 도구가 모르는 이름이 있으면(공식이 새로 추가) 베이스 값을 두고 크게 알린다. 베이스 플러그인의 원본 파일을
  틀로 값만 바꾸므로 이름 집합·형식은 원본 그대로다.
- 못 구했을 때는 그 클라이언트의 출처를 받다 실패한 원인(막힘 / 연결 / 서버 오류 / 거절 / 토큰)과 "아직 공개 전"을 나눠 알린다.

### opcodediff 로컬 실행

[xivdev/opcodediff](https://github.com/xivdev/opcodediff) 를 upstream CI 와 같은 조합으로 직접 돌려 직전 → 이번 버전
opcode 대응표를 만든다. 패킷 핸들러 시그니처 2개만 쓰고 나머지는 두 exe 의 핸들러를 맞대어 찾으므로, IDA(항목마다
시그니처) 쪽 시그니처가 어긋났을 때의 대안이다. 공개 저장소가 늦을 때의 마지막 수단이라 Python 이 없는 사람도 돌릴 수
있게 했다.

- **Python 3.12 이상**(Windows 에서 3.11 은 r2pipe 가 멈춤): `py` 런처로 찾거나 `config.json` 의 `"python"`. 없으면
  **휴대용 Python**(nuget.org 의 Python Software Foundation 공식 패키지 3.13.16, 약 14MB, 버전·SHA512 고정)을
  `tools\python\` 에 받아 압축만 풀어 쓴다 — 설치·관리자 권한 없음(Python 없는 PC 에서 처음부터 약 16초).
- 자동으로 준비하는 것(`tools\`): Python 가상환경(`click`/`r2pipe`/`semver` — 쓸 Python 이나 요구 패키지가 바뀌면 다시
  만듦), CI 와 같은 버전의 radare2(워크플로 파일에서 읽음), 스크립트. 터미널 변수 `TERM` 이 있으면(Git Bash 등) radare2
  출력이 r2pipe 와 어긋나므로 빼고 실행한다.
- 스크립트는 opcodediff 저장소의 **최신본(master)** 을 그대로 실행한다 — 고정하면 저쪽이 패치 날 고칠 때마다 이 도구를
  새로 내야 하므로. 대신 실행한 파일의 해시를 기록에 남긴다. IDA finder(Yarukon)도 같다.
- **맞댈 옛 exe = 직전 출시**: 공식 패치 목록([Thaliak](https://thaliak.xiv.dev))에서 처음 내려준 시각(`first_offered`)이
  같은 패치 묶음이 출시 한 번이고, 묶음의 가장 큰 버전이 그 출시의 게임 버전이다(7.57: 미리 만든 2026.09.29 / 10.02 와 10.06
  을 2026-10-13 에 함께 내려줌 → 출시 버전 10.06). 지금 exe 버전보다 앞선 출시 중 가장 최근 것을 받는다 — 미리 만든 패치나
  한국 전용 중간 패치는 묶음 안에 있어 저절로 빠지고, 지금 버전이 아직 Thaliak 에 없어도 된다. 버전 문자열의 날짜는 출시일이
  아니다(빌드 날짜로 보임). 패치 번호는 필요 없다(스크립트는 exe 파일 이름을 읽지 않음).
- **베이스 플러그인 값으로 판정**: 공식 플러그인은 항상 최신 출시용이거나 그 직전 출시용이다. 대응표(옛 값 → 새 값)에
  베이스의 Machina 값을 비춰 본다 — 전부 옛 값 쪽에 있으면 직전 출시용이라 바꾼 값을, 전부 새 값 쪽에만 있으면 이미 지금
  버전용이라 그대로를 쓴다. 어느 쪽도 아니거나 판단할 수 없으면(양쪽 다 맞는데 바꾼 값이 다름) 이 결과는 쓰지 않고 공개본으로
  넘어간다 — 대응표 오류를 "이미 맞음"으로 넘겨짚지 않게. 안전장치로, 직전 출시와 맞지 않으면 한 출시 더 앞의 exe 와도 맞대
  본다(거기서는 베이스가 그 옛 출시용일 때만 받아들인다 — 옛 exe 를 하나 더 받으므로 맞지 않을 때만). opcode 공간이 촘촘해(약 1024 중 775 사용) 값 하나는 우연히도
  맞으므로 전부 맞아야 인정한다. 베이스에 없는 이름(부가 출력용)은 직전 출시의 Yarukon 목록이 대응표와 맞으면 거기서 채운다.
- 7.57 확인: 7.56h 용 공식 3.0.3.1 을 대응표 없이 바꾼 결과가 7.57 용 공식 3.0.3.3 과 같았다(한국/글로벌/중국 26/26).
- 옛 exe 는 [옛 exe 와 스냅샷](#옛-exe-와-스냅샷). 결과는 `snapshots\<지역>\<버전>\localdiff\<옛 버전>\diff.json` 에
  캐시한다(`--overwrite` 로 다시).

### IDA 직접 추출

`snapshots\<분석 대상 지역>\<버전>\ida\` 에 [Yarukon](https://github.com/Yarukon/FFXIVNetworkOpcodes) 의 최신
`ffxiv_opcode_finder.py` / `signatures.json` / `machina.template` 을 받고, `src\ActPluginRepack.Core\Opcodes\ida\` 의 래퍼로
exe 복사본을 `idat` 헤드리스(창 없이)로 분석한다. 결과 `opcodes.json` 만 캐시하고 분석 DB(~700MB)는 지운다. IDA 9 가
필요하다(최신 finder 가 IDA 9 API 를 쓴다).

- **분석 대상**: 중국/글로벌/한국은 같은 버전이면 opcode 가 같으므로, 기본으로 **같은 버전의 중국판 exe** 를(설정에 없으면
  공식 패치 서버에서 받아) 한 번만 분석하고 결과를 모든 지역에 쓴다 — finder 시그니처가 중국판 기준이라 보정 없이 맞는다
  (7.57: 4분 16초, 공개본과 일치). `"idaTarget"` / `--ida-target global|korean` / 고급 모드 "IDA 분석 대상" 으로 바꿀 수
  있고, 받지 못하면 분석 중인 클라이언트 exe 로 대신한다. 대만판은 opcode 가 달라 항상 그 클라이언트 exe 로.
- 중국판이 아닌 exe 를 분석할 때는 래퍼가 지역을 CN 으로 강제하고, CN 시그니처가 안 맞는 항목만
  `src\ActPluginRepack.Core\Opcodes\ida\signatures.override.json` 으로 보정한다(7.57 기준 `EffectResult` /
  `EffectResultBasic` — 레지스터 하나 차이, `ActorMove` — 한국 exe 에서 엉뚱한 함수에 걸림). 다음 패치에서 어긋나는 항목이
  생기면 이 파일에 추가한다.

### 부가 출력 — `extras\`

ACT 플러그인 폴더에 넣는 `output\` 과 따로, 다른 도구용 opcode 파일을 만든다:

- `extras\lemegeton.xml` — Lemegeton blueprint (EN/DE/FR/JP, CN, KR). Yarukon finder 와 같은 형식.
- `extras\OverlayPlugin\opcodes.jsonc` — OverlayPlugin `resources\opcodes.jsonc` 교체용.
  [OverlayPlugin 저장소의 파일](https://github.com/OverlayPlugin/OverlayPlugin/blob/main/OverlayPlugin.Core/resources/opcodes.jsonc)을
  틀로 갱신한 지역의 opcode 만 바꾸고 `size` 와 나머지 지역은 그대로 둔다. `install` 이 OverlayPlugin 에도 넣는다(원래 파일은
  백업). 해루봇은 시작할 때마다 이 파일을 OverlayPlugin 배포본으로 덮어쓰므로 해루봇 사용자에게는 유지되지 않는다.

## 게임 데이터 (CSV)

설치된 클라이언트에서 직접 추출해(같은 버전 추출본이 있으면 재사용) 필요한 시트 7개만 `input\csv\<lang>` 로 복사한다.
global → en/ja/de/fr, korean → ko. 추출기는 `oxidizer` 를 설정했으면 xiv-data-oxidizer, 아니면 SharpVision(v1.1.0 이상은
그 7개 시트만 `--sheets` 로). 앞의 것이 실패하면 다음 것으로, 둘 다 쓸 수 없으면 공개 데이터마이닝 저장소에서 받는다.
클라이언트가 없는 cn/tc 도 Action/Status 만 받는다(플러그인의 cn/tc 리소스가 그 둘뿐).

| 언어 | 저장소 | 버전 확인 |
|---|---|---|
| en/de/fr/ja | [xivapi/ffxiv-datamining](https://github.com/xivapi/ffxiv-datamining) | 커밋 메시지의 패치 번호를 대응표로 (안 되면 커밋 날짜 ≥ 빌드 날짜로 추정) |
| ko | [Ra-Workspace/ffxiv-datamining-ko](https://github.com/Ra-Workspace/ffxiv-datamining-ko) | 커밋 메시지의 게임 버전 |
| cn | [thewakingsands/ffxiv-datamining-cn](https://github.com/thewakingsands/ffxiv-datamining-cn) | 커밋 메시지의 게임 버전 |
| tc | [thewakingsands/ffxiv-datamining-tc](https://github.com/thewakingsands/ffxiv-datamining-tc) | 최신본 |
| 전부 (뒤 순위) | [fae-industries/xiv-data](https://github.com/fae-industries/xiv-data) `exd/<lang>/` | 커밋 메시지의 패치 번호를 대응표로 |

언어마다 위에서부터 보고 버전이 맞는 첫 저장소에서 받는다. 어느 출처도 이번 버전이 없으면 `input\csv\<lang>` 은 그대로
두고 경고한다. en/ko 공개본은 SharpVision 추출본과 바이트 단위로 같다(7.57 확인). cn/tc 는 SaintCoinach 형식이며
변환기가 둘 다 읽는다.

## 옛 exe 와 스냅샷

opcodediff 로컬 실행과 시그니처 자동 복구는 직전 버전의 `ffxiv_dx11.exe` 가 필요하다.

- 글로벌/한국/중국은 필요할 때 **공식 패치 서버에서 받는다**([Thaliak](https://thaliak.xiv.dev) 이 패치 파일 위치를 알려
  줌). exe 는 패치 파일 맨 앞에 있어 앞부분 수십 MB 만 받고 멈춘다(대형 패치의 1.5GB 조각도 약 34MB, 몇 초).
- 패치 서버는 http 라 받은 exe 는 **SQUARE ENIX 의 Authenticode 서명**을 확인한 뒤에만 쓰고, 받아 둔 것도 쓸 때마다
  다시 확인한다(서명이 맞지 않으면 다시 받음).
- 받을 수 없는 **대만판만** 지금 exe 를 `snapshots\<지역>\<버전>\` 에 보관한다(약 50MB). 받아 둔 옛 exe 와 opcodediff/IDA
  캐시도 같은 폴더에 있으며, 지역마다 지금·직전 버전 폴더만 남기고 자동으로 정리한다.
- 직전 버전은 공식 패치 목록의 직전 출시다([위](#opcodediff-로컬-실행)) — 출시 전에 미리 만든 패치(예: 2026.09.29 / 10.02)는
  출시 버전이 아니므로 쓰지 않는다.

## 시그니처 자동 복구

플러그인이 게임 exe 에서 바이트 패턴으로 위치를 찾는 곳(deucalion 패킷 후크, 메모리 읽기, Oodle — 종류와 형식은
[시그니처](ManualInputs.md#시그니처--inputsignatures))이 깨졌으면 `prepare` 가 다음 순서로 고쳐 본다. 후보는 설정된 **모든**
클라이언트 exe 에서 1건 이상 맞아야 채택되고, `input\signatures\<종류>.txt` 에 출처 주석(`# auto: …`)과 함께 기록된다
(`--no-repair` 로 끔, 한쪽만 쓰려면 `--sources upstream` / `--sources sigregen` 또는 고급 모드의 데이터 소스).

1. **upstream deucalion** (deucalion 항목만) — [ff14wed/deucalion](https://github.com/ff14wed/deucalion) main 의
   `lib.rs` / `hook/create_target.rs` 상수, 그다음 최신 릴리스 dll 에 든 시그니처. 7.57 때 소스 수정이 공식 플러그인보다
   약 5시간 빨랐다.
2. **SigRegen** (모든 종류) — 원래 패턴이 맞던 **옛 exe** 와 새 exe 를 비교해 새 패턴을 만든다. 옛 exe 는 공식 패치 목록의
   직전 출시, 패킷 후크는 거기서도 원래 패턴이 안 맞으면 공식 패치 목록을 몇 개 더 거슬러 올라간다. 패치로 시그니처가 깨지는 흔한
   이유는 "같은 코드인데 레지스터 배정이 바뀜"이라, 패턴이 덮던 명령어들의 모양(레지스터·주소를 뺀 명령어)으로 새 위치를
   찾고 새 위치의 실제 바이트로 패턴을 다시 만든다. 새 exe 에서 정확히 그 위치들에만 맞는지, call 형은 대상 함수가 대응하는지
   검증하고, 통과하지 못하면 실패로 보고한다(틀린 패턴을 만들지 않음). 백테스트(2025.06 ~ 2026.10 의 실제 deucalion 수정
   6건): 전부 정답과 같은 곳을 가리켰고, 3건은 바이트까지 같았다.

어느 것도 안 되면 직접 구한다 — deucalion 커밋/이슈를 보거나 IDA / Ghidra 등에서 이전 버전과 비교한다. 수동으로 시험할 때:

```powershell
dotnet $cli fetch-exe --region global --version 7.56h      # snapshots\global\<버전>\ffxiv_dx11.exe
dotnet $cli sigregen --old <옛 exe> --new <새 exe> --pattern "E8 $ { ' } 4C 8B 4F 10 8B 47 1C 45"
#   --truth "<정답 패턴>" 을 주면 같은 곳을 가리키는지 비교
```

## ACT 에 설치 / 되돌리기

`install` 은 `output\` 의 결과를 ACT 의 FFXIV 플러그인 폴더에 넣고, `extras\OverlayPlugin\opcodes.jsonc` 가 있으면
OverlayPlugin 의 `resources\` 에도 넣는다. 위치는 ACT 설정
(`%APPDATA%\Advanced Combat Tracker\Config\Advanced Combat Tracker.config.xml`)에 등록된 `FFXIV_ACT_Plugin.dll` /
`OverlayPlugin.dll` 에서 찾는다. 해루봇의 언어별 폴더면 다른 언어 폴더도 함께 넣는다(아래 **해루봇**).

- **ACT 와 게임이 꺼져 있어야** 한다 — 플러그인 DLL 은 ACT 가, deucalion 은 게임이 잡고 있다.
- 바꾸거나 지우는 원래 파일은 `backups\<시각>\` 에 복사하고 무엇을 했는지 `backup.json` 에 남긴다(넣은 파일의 SHA256 포함).
  최근 10개만 보관하고, 이미 되돌린 백업부터 지운다 — 설치·되돌리기를 여러 번 해도 공식 플러그인이 든 백업이 남게.
- 넣을 파일이 이미 그대로 들어 있으면(같은 패치본을 다시 설치, 공식 최신본이 이미 있음) 아무것도 하지 않는다. 플러그인은
  이미 이 패치본이고 다른 대상(OverlayPlugin opcode)만 다르면 새 백업을 만들지 않고 그 패치본을 설치한 백업에 보탠다 —
  새 백업에는 패치본이 "원래 파일"로 들어가 되돌리기가 망가지므로.
- deucalion: 플러그인은 시작할 때 같은 폴더의 `deucalion-*.distrib.dll` 로 기존 `deucalion-*.dll` 을 바꿔 쓴다. 그래서
  패치본은 distrib 이름으로 넣고 옛 `.dll` 은 백업 후 치운다. 넣을 파일에 deucalion 이 없으면(백신 격리 등) 설치하지 않는다.
- ACT 에 패치본의 베이스보다 **더 새 플러그인**이 있으면 설치하지 않는다(prepare 를 다시). `--official` 이면 패치본 대신
  `input\plugin` 의 공식 최신본을 설치한다(OverlayPlugin 은 건드리지 않음). 이때도 ACT 에 그보다 새 버전이 있으면 낮추지 않는다.
- 쓰는 도중에 실패하면(잠김, 백신) 바로 원래대로 되돌린다. 되돌리는 것은 실제로 지우거나 바꾼 파일뿐이다. 일부를 되돌리지
  못했으면 백업에 "실패한 설치"로 표시해 다음 `restore` 가 확인 없이 원래 파일로 되돌린다.
- 관리자 권한이 필요하면 같은 명령을 관리자 권한으로 다시 실행한다(UAC 확인 창, 출력은 원래 창에). 대상 폴더는 미리 정해
  넘기고(다른 관리자 계정이면 `%APPDATA%` 가 다르므로), 확인 창에서 '아니요'면 아무것도 바꾸지 않는다. 관리자 권한으로 쓸
  때는 대상 경로에 링크(정션)가 있으면 거부한다.

`restore` 는 아직 되돌리지 않은 가장 최근 백업(또는 `--backup <폴더>` — 상대 경로는 작업 폴더 기준)으로 되돌린다.

- 설치한 뒤로 ACT 의 플러그인이 다른 것으로 바뀌었으면(해루봇의 플러그인 업데이트 등) 되돌리지 않는다 — 옛 버전으로
  내려가므로. 플러그인 폴더가 여럿이면 바뀐 폴더만 그대로 두고 나머지를 되돌린다(모두 바뀌었으면 되돌리지 않음). 이미 원래 파일로 돌아가 있는 것은 바뀐 것으로 보지 않는다(되돌리기가 도중에 막혔다면 다시 하면 마저 된다).
  해루봇이 덮어쓰는 OverlayPlugin opcode 처럼 그 뒤 바뀐 다른 파일은 그대로 둔다.
- 백업 기록은 사용자가 쓸 수 있는 곳에 있는데 되돌리기는 관리자 권한으로 돌 수 있으므로, 기록이 가리키는 곳이 실제
  ACT 플러그인 폴더 / OverlayPlugin `resources` 이고 파일 이름만 담았는지 먼저 검사한다. 원래 파일의 사본이 없어졌으면
  (백신 격리 등) 아무것도 바꾸기 전에 멈추고, 그 뒤 폴더째 없어진 대상(OverlayPlugin 을 지움 등)은 건너뛴다.
- 되돌릴 수 있는지는 관리자 권한을 청하기 전에 판단한다. GUI 는 되돌릴 수 없으면 버튼을 숨긴다.

**해루봇**: 플러그인은 `<ACT>\Plugins\FFXIV_ACT_Plugin_KR` (또는 `_Global` / `_CN`) 에 있고, 해루봇은 "ACT 실행" 때마다 ACT
설정이 고른 언어의 그 파일을 가리키게 되돌린다 — 그래서 이 도구는 그 파일을 제자리에서 바꾼다. 해루봇에서 FFXIV 플러그인을
업데이트하면 그 폴더를 통째로 지우고 공식본을 다시 받는다. 해루봇이 켜져 있는 동안은 ACT 를 강제로 끈다.

- `_KR` / `_Global` 은 같은 공식 플러그인(ravahn/FFXIV_ACT_Plugin)이라, ACT 설정이 둘 중 하나를 가리키면 `FFXIV_ACT_Plugin.dll`
  이 있는 다른 쪽도 함께 설치한다(설치한 뒤 해루봇에서 언어를 바꿔도 패치본이 쓰이게). 백업은 하나에 모두 담고, 되돌리기도
  한 번에 한다. 다른 쪽에 더 새 플러그인이 있으면(그 언어만 먼저 업데이트) 그 폴더는 뺀다.
- `_CN` 은 중국판에 맞춰 고친 다른 플러그인(TundraWork/FFXIV_ACT_Plugin_CN)이라 쓰지 않는다. ACT 설정이 `_CN` 을 가리키면
  `_KR` / `_Global` 만 설치하고, 둘 다 없으면 설치하지 않는다(`--target` 으로 직접 줘도 거부).
- 예전에 한 폴더에만 설치했으면, 다시 설치할 때 나머지 폴더를 그 백업에 보탠다.

## 네트워크

(`Infrastructure\Http.cs`, `HttpCache.cs`)

- 서버 오류(5xx)와 "잠시 천천히"(429, `Retry-After` 가 붙은 403)는 최대 3번 시도한다(2초 / 5초 뒤, `Retry-After` 가 있으면
  그만큼 — 10초보다 길면 바로 실패로). 연결 실패(연결 안 됨, 30초 동안 응답 없음)는 2초 뒤 1번, 본문을 받다 끊기면 3초 뒤
  1번만 다시 한다. 다시 해도 같은 거절(그 밖의 4xx)은 다시 하지 않는다. 기다리는 중에도 취소된다.
- 끝내 실패한 서버는 잠시 건너뛴다(연결 실패·서버 오류 1분, 막힘은 서버가 말한 만큼 1~10분). 사이에 성공한 응답 없이
  서로 다른 서버 두 곳(GitHub 주소들은 한 곳으로 셈)에 잇달아 연결이 안 되면 인터넷이 끊긴 것으로 보고 나머지 요청도 바로
  실패로 넘긴다. 어느 서버든 응답이 오면, 그리고 새 prepare 마다 이 기록을 지운다.
- 실패는 종류(연결 / 막힘 / 서버 오류 / 거절 / 토큰 거부)를 나눠 안내하고, 출처 하나가 실패하면 그 출처만 건너뛰고 다음
  출처로 간다(opcode, CSV 저장소, upstream deucalion → SigRegen). 처음 실행이라 공식 플러그인을 받지 못하면 원인을 알리고 멈춘다.
- GitHub 에서 받은 작은 파일(raw, 웹 페이지, API)은 `%LOCALAPPDATA%\ActPluginRepack\cache\http\` 에 3분 동안 기억하고, 지나면
  ETag 로 바뀌었는지만 묻는다. 404 와 실패, 데이터 자리에 온 HTML(로그인 페이지 가로채기), 릴리스 파일은 기억하지 않는다.
  `--overwrite`(고급 모드 "캐시 무시하고 다시 추출")는 이것도 무시한다.

### GitHub API 한도

비인증은 IP 당 시간당 60회(PC방·공유기처럼 IP 를 나눠 쓰면 합쳐짐), 토큰이 있으면 5000회다.

- 최신 릴리스와 최근 커밋은 **토큰이 없으면 웹 페이지 먼저**(`releases/latest` 이동 + `expanded_assets/<태그>`,
  `commits/<브랜치>[/<경로>].atom` — API 한도와 무관), **토큰이 있으면 API 먼저** 보고, 앞의 것이 실패하면 뒤의 것으로
  넘어간다. 토큰 없는 prepare 한 번이 쓰는 API 는 0회다. `raw.githubusercontent.com` 과 릴리스 파일은 원래 한도와 무관.
- 토큰은 환경 변수 `GITHUB_TOKEN` → 저장한 토큰(GUI 설정 / `github --token`, `%LOCALAPPDATA%\ActPluginRepack\github-token.bin`
  에 DPAPI 로 현재 Windows 사용자만 풀 수 있게) → GitHub CLI 로그인(`gh auth token`) 순서로 찾는다. 공개 저장소만 읽으므로
  **권한을 하나도 주지 않은 fine-grained 토큰이면 충분하다**. 토큰이 거부되면(만료 등) 그 토큰을 어디서 가져왔는지에 맞춰
  고칠 방법을 알린다.
- 사용량은 `status` / `github` / GUI 고급 모드에서 본다(`/rate_limit` 조회는 한도를 쓰지 않음).

## 그 밖의 동작

- **strong name**: 서명은 무효가 되지만 공개키 토큰을 유지하므로 ACT 의 full-trust 로컬 로드(.NET Framework strong-name
  bypass)로 정상 로드된다.
- **패치본의 버전 번호**는 원래 플러그인 번호 그대로다(FFLogs 업로드는 안 됨 — 공식 업데이트 전 임시용).
- `build` 는 결과를 임시 폴더(`output.tmp\`)에 다 만든 뒤 `output\` 의 `*.dll` 을 통째로 바꾼다 — 실패하면 이전 결과가
  그대로 남고, 성공하면 이전 빌드의 DLL(옛 deucalion 등)이 섞이지 않는다.
- **백신 오탐**: Windows Defender 가 플러그인/deucalion 을 풀자마자 격리하는 경우가 있다. 새 파일은 임시 폴더에 풀어 남아
  있고 읽히는지 확인한 뒤에만 바꾸고, 실패하면 기존 파일을 그대로 둔다. 작업 폴더를 제외 목록에 넣거나, 직접 받아
  `input\plugin` 에 넣고 `--no-plugin` 으로 실행한다. deucalion 배포본만 격리돼 없으면 플러그인이 최신이어도 공식 릴리스를
  다시 받는다(그대로 두면 `install` 이 계속 거부되므로).
- 외부 프로그램(Python 스크립트, IDA, radare2 등)에는 `GITHUB_TOKEN` / `GH_TOKEN` 을 넘기지 않고, 이름만 준 프로그램
  (`gh`, `git`, `py` 등)을 현재 폴더에서 찾지 않는다.
- 같은 작업 폴더에서 두 작업을 동시에 실행하면 두 번째는 바로 멈춘다(`.actrepack.lock`). GUI 는 한 번에 하나만 열린다.
