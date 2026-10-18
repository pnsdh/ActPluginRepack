# 명령줄 (CLI)

화면 프로그램(GUI)과 같은 일을 명령줄로 한다. 단계별 실행, 정보 조회, 자동화용. 동작의 자세한 설명은 [동작 원리](HowItWorks.md).

## 빠른 시작

1. **.NET 10 SDK** 로 이 저장소를 빌드한다(`build.ps1` 이 해 준다). 배포본의 `cli\ActPluginRepack.exe` 도 같은 명령을 쓰지만,
   저장소 밖에서는 작업 폴더를 `--root` 로 준다(GUI 와 같은 데이터: PowerShell 에서 `--root $env:LOCALAPPDATA\ActPluginRepack`).
   `--root` 를 줘도 받은 파일의 HTTP 캐시와 GitHub 토큰은 사용자 폴더(`%LOCALAPPDATA%\ActPluginRepack\cache\http` 등)를 같이 쓴다.
2. 게임 클라이언트 경로를 설정한다: `detect --save` 가 설치된 클라이언트를 찾아 `config.json` 에 적는다(기본 설치 경로,
   설치 기록, XIVLauncher, Steam). 못 찾으면 `config.example.json` 을 `config.json` 으로 복사해 직접 적는다.
3. **ACT 와 게임을 끄고** 실행한다:
   ```powershell
   .\build.ps1 -Prepare -Install
   ```
   최신 공식 플러그인을 받고 → 시그니처·opcode·게임 데이터를 준비하고 → 패치한 플러그인을 `output\` 에 만들고 →
   ACT 의 FFXIV 플러그인 폴더에 설치한다(원래 파일은 `backups\` 에). 관리자 권한이 필요하면 설치 단계만 UAC 로 올라간다.
   공식 플러그인이 이미 지금 게임과 맞으면 패치본을 만들지 않고 공식 최신본을 설치한다.
4. ACT 를 실행한다(해루봇 사용자는 해루봇에서 "ACT 실행").

```powershell
$cli = 'src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.dll'   # build.ps1 이 빌드해 둠
dotnet $cli status      # 지금 상태와 다음에 할 일 (공식 플러그인이 이미 맞으면 그렇다고 알려 줌)
dotnet $cli restore     # 마지막 설치 하나를 그 직전으로 (개발·비상용 — 화면의 되돌리기는 install --official)
```

`status` 는 게임·플러그인·설치 상태를 파일로만 읽고, GitHub API 사용량과 이 도구의 새 버전만 인터넷으로 확인한다.
`build.ps1` 옵션: `-Prepare` / `-Install` / `-NoCsv` (CSV 재변환 생략) / `-Plugin <경로>` (원본 플러그인 지정).

## 설정 — `config.json`

작업 폴더의 `config.json` 에 **게임 클라이언트 경로**와 선택 도구를 적는다(git 에 올라가지 않음). 없어도 직접 넣은
재료로 빌드는 된다.

```jsonc
{
  // IDA 를 Program Files 밖에 설치했을 때만
  // "ida": "D:\\Tools\\IDA Professional 9.4",
  "clients": {
    "korean": "C:\\Program Files (x86)\\FINAL FANTASY XIV - KOREA",
    "global": "C:\\Program Files (x86)\\SquareEnix\\FINAL FANTASY XIV - A Realm Reborn"
  }
}
```

- `clients` 의 키는 지역: `global` / `korean` / `simplified-chinese` / `traditional-chinese`. 가진 클라이언트만 적는다.
  경로는 설치 폴더 / `game` 폴더 / `ffxiv_dx11.exe` 아무거나. `\` 는 `\\` 로 쓴다.
- `sharpVision` (선택): 직접 빌드한 [SharpVision](https://github.com/pnsdh/SharpVision) 실행 파일(.exe 또는 .dll).
  보통은 **적지 않는다** — `prepare` 가 GitHub 최신 릴리스를 `tools\SharpVision\<버전>\` 에 받아 쓴다(새 릴리스가 나오면
  새로 받고 이전 것은 지움).
- `oxidizer` (선택): SharpVision 대신 [xiv-data-oxidizer](https://github.com/fae-industries/xiv-data-oxidizer) 로 CSV 를
  추출한다(결과는 SharpVision 과 바이트 단위로 같음). 릴리스 바이너리가 없어 소스를 빌드하므로 **git 과 Rust**
  ([rustup](https://rustup.rs))가 필요하다. `true` 면 `tools\xiv-data-oxidizer\` 에 받아 실행마다 소스·스키마
  ([EXDSchema](https://github.com/xivdev/EXDSchema))를 갱신하고, 문자열이면 직접 관리하는 clone 폴더(빌드만 하고 git 은
  건드리지 않음). 실패하면 SharpVision 으로 넘어간다.
- `localDiff` (선택, 기본 켜짐): opcode 공개 전에 opcodediff 를 로컬에서 실행한다
  ([opcodediff 로컬 실행](HowItWorks.md#opcodediff-로컬-실행)). 끄려면 `false`. `python` 으로 쓸 Python(3.12 이상) 경로를
  지정할 수 있다.
- `ida` (선택): IDA Pro 설치 폴더(IDA 9 + Hex-Rays). 라이선스 때문에 받아 쓸 수 없어 설치된 것을 쓴다 — 적지 않으면
  Program Files 의 `IDA*` 폴더(9 이상)를 찾고, `false` 면 쓰지 않는다.
- `idaTarget` (선택): IDA 로 분석할 exe 의 지역. 기본 `simplified-chinese` — [IDA 직접 추출](HowItWorks.md#ida-직접-추출).

`detect --save` 와 GUI(게임 위치, 설정 화면)는 `config.json` 을 다시 쓴다 — 바꾼 항목 말고는 그대로 두지만 **주석은
사라진다**. JSON 이 깨진 `config.json` 은 화면에서 새로 만들 때 `config.json.broken-<시각>` 으로 옮겨 보관한다.

## 명령

`build.ps1` 은 아래의 `prepare` / `build` / `install` 을 부르는 런처다. 경로 기본값은 작업 폴더 기준, 모든 옵션은 `help`
(`<명령> --help` 도 같은 도움말).

```powershell
$cli = 'src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.dll'   # 배포본은 cli\ActPluginRepack.exe --root <작업 폴더>
dotnet $cli status                                   # 지금 상태와 다음에 할 일
dotnet $cli detect --save                            # 설치된 게임 클라이언트를 찾아 config.json 에
dotnet $cli prepare                                  # 재료 준비 + 패치 필요 여부 판단
dotnet $cli build                                    # output\ 에 플러그인 만들기
dotnet $cli install                                  # ACT 에 설치 (원래 파일은 backups\)
dotnet $cli install --official                       # 패치본 대신 공식 최신본 설치 (공식이 이미 맞을 때)
dotnet $cli restore                                  # 마지막 설치 하나를 그 직전으로 (--backup <폴더>)
dotnet $cli rsv                                      # ACT 로그의 RSV -> input\overrides\global_rsv.txt
dotnet $cli github                                   # GitHub API 사용량과 쓰는 토큰
dotnet $cli github --token -                         # 토큰을 표준 입력으로 받아 확인 후 저장 (--clear 로 지움)
dotnet $cli sigcheck                                 # 게임 클라이언트에 시그니처가 맞는지 점검 (--game <폴더>)
dotnet $cli repack                                   # opcode/override/data/시그니처 주입만 (시그니처를 바꾸면 패치한 deucalion 도 출력 폴더에)
dotnet $cli csv --csv-dir input\csv\ko               # CSV -> data\ 변환만
dotnet $cli export --what opcodes                    # 내장 리소스 추출 (data / overrides / opcodes)
dotnet $cli fetch-exe --region korean --version 7.57 # 특정 버전 exe 를 공식 패치 서버에서 snapshots\ 로
dotnet $cli fetch-exe --patch <패치 파일 버전> --out a.exe   # 패치 파일 직접 지정
dotnet $cli sigregen --old a.exe --new b.exe --pattern "..."   # 시그니처 재생성
dotnet $cli sigscan --patterns p.json --exe a.exe --json       # 패턴 목록을 exe 에서 찾기 (다른 도구용, 아래)
dotnet $cli sigregen --patterns p.json --old a.exe --new b.exe --json   # 여러 개 재생성 (다른 도구용, 아래)
dotnet $cli hotfix                                   # 패치본 배포 묶음 (배포하는 사람용)
dotnet $cli report                                   # 문제 보고용 기록 묶음 zip (--out <zip | 폴더>, 생략 시 현재 폴더)
dotnet $cli replay --version 2026.10.06.0000.0000    # (개발용) 지난 패치 재현 — 공식 플러그인(정답)과 비교 (생략 시 최근 출시 목록)
```

**prepare 옵션**: `--plugin <경로>` / `--no-plugin` / `--no-csv` / `--no-opcodes` / `--no-snapshot` / `--no-sigcheck` /
`--no-repair` / `--ida` (IDA 로 직접 추출해 그 결과를 우선) / `--ida-target <지역>` / `--overwrite`
(SharpVision·oxidizer·IDA 캐시와 받은 파일 기억을 무시하고 다시) / `--sources a,b` (쓸 출처 제한, 검증용 — CSV:
`oxidizer`·`sharpvision`·`datamining`·`xivdata`, opcode: `yarukon`·`opcodediff`·`localdiff`·`ida`·`karashiiro`, 시그니처 복구:
`upstream`·`sigregen`; 한 분류의 이름이 없으면 그 분류는 전부 사용).

**install 옵션**: `--target <플러그인 폴더>` (여러 번 줄 수 있음 — 준 폴더에만. 생략하면 ACT 설정에서 찾고, 해루봇이면
한국·글로벌 서버용 폴더 모두 — [ACT 에 설치](HowItWorks.md#act-에-설치--되돌리기)) /
`--overlay <OverlayPlugin resources 폴더 | ->` (직접 지정하거나 건너뜀) /
`--official`. **rsv 옵션**: `--days <일>` / `--logs <파일|폴더,...>`.
**repack 옵션**: `--plugin <dll>` / `--out <dll>` (예전 이름 `--output` 도 받음) / `--opcodes` · `--overrides` · `--data` ·
`--signatures <폴더 | ->` (`-` 는 그 단계 생략, 기본 폴더가 없으면 자동 생략).

**replay 옵션** (개발용 — [시험](Development.md#시험)): `--version <게임 버전>` / `--region <지역>` (기본 `global`) /
`--base <태그>` · `--ref <태그>` (베이스·정답 공식 릴리스, 생략 시 출시 시각으로) / `--sources a,b` (기본 `localdiff,sigregen`) /
`--work <폴더>` (기본 `%TEMP%\actrepack-replay\<지역>-<버전>`) / `--tools <tools 폴더>` (받아 둔 `python` · `radare2` ·
`opcodediff-venv` 를 복사해 씀) / `--keep` (끝나도 작업 폴더를 남김). 모두 정답과 같으면 0, 다른 것이 있으면 1. 사용자 작업
폴더는 쓰지 않으므로 `--root` 가 없다.

여러 값을 주는 방법: `install --target` 은 옵션을 여러 번 주고, 나머지(`--sources`, `--logs`, `--game`, `--filter`,
`--lists`)는 쉼표로 잇는다(`--sources yarukon,karashiiro`).

작업 폴더에 쓰는 명령(prepare / build / install / restore / hotfix / rsv / repack / csv / export / `fetch-exe --version` /
`detect --save`)은 작업 폴더 잠금(`.actrepack.lock`)을 잡는다 — 다른 창이나 GUI 가 같은 폴더에서 작업 중이면 바로 멈춘다.
`sigscan` / `sigregen` 은 읽기만 하므로 잡지 않는다(`--region` 으로 준 버전의 exe 를 받아야 할 때만 잡는다).
`export` 는 이미 있는 파일을 덮어쓰지 않는다(`--force`, 또는 `--out` 으로 다른 폴더).

## 종료 코드

Ctrl+C 는 진행 중인 작업을 취소한다(받던 파일, 외부 프로그램도 정리).

| 코드 | 이름 | 뜻 |
|---|---|---|
| `0` | `Ok` | 정상 |
| `1` | `Error` | 실패 (모든 명령). `sigregen` 은 패턴을 만들지 못했거나 `--truth` 와 다른 곳을 가리킬 때도, `sigscan` 은 값 해석에 실패한 것이 있을 때도 |
| `2` | `HooksBroken` / `RsvConflicts` / `ScanNoMatch` / `RegenFailed` | `prepare`: 패킷 후크가 깨졌거나 시그니처 점검 자체가 실패 / `sigcheck`: 패킷 후크가 깨짐 / `rsv`: 공식·직접 넣은 override 와 값이 다른 키가 있음 / `sigscan`: 0곳에 맞는 패턴이 있음 / `sigregen --patterns`: 만들지 못한 패턴이 있음 |
| `3` | `Incomplete` / `ScanManyMatches` | `prepare`: 준비 미완료 (opcode 를 못 구한 클라이언트가 있음 — 공개 전일 수 있으니 나중에 다시) / `sigscan`: 0곳은 없고 여러 곳에 맞는 패턴이 있음 |
| `4` | `AppsRunning` | `install` / `restore`: ACT 나 게임이 실행 중 (관리자 권한으로 다시 실행한 쪽에서 났어도 같은 코드) |
| `5` | `NotNeeded` | `prepare`: 패치 불필요 — 공식 플러그인이 이미 지금 게임과 맞음 (`install --official` 로 공식 최신본을) |
| `6` | `Undecided` | `prepare`: 패치가 필요한지 판단할 수 없음 (opcode 출처에 빠진 항목, 공식 플러그인을 확인하지 못함 등 — 나중에 다시). `--no-sigcheck` / `--no-opcodes` 로 판단에 필요한 단계를 직접 껐으면 0 |
| `7` | `NewerPlugin` | `install`: ACT 에 받아 둔 공식 플러그인(또는 패치본의 베이스)보다 새 플러그인이 있어 그대로 둠 (해루봇의 한국 임시본 등 — 공식 플러그인이 갱신된 뒤 다시) |
| `130` | `Cancelled` | Ctrl+C 로 취소 |

`build.ps1 -Prepare` 는 prepare 가 0(패치본을 만들 차례)이 아니면 빌드하지 않는다. 5(패치 불필요)면 `-Install` 일 때
공식 최신본을 설치하고, 6(판단할 수 없음)이면 멈춘다. install 이 7(ACT 에 더 새 플러그인)이면 실패로 보지 않는다.
이름은 코드 안의 상수 이름이다 — 값은 `src\ActPluginRepack.Core\Infrastructure\ExitCodes.cs` 한 곳에 있고(`sigscan` /
`sigregen` 만의 `ScanNoMatch` · `ScanManyMatches` · `RegenFailed` 는 `src\ActPluginRepack.Cli\SigCommands.cs`), `build.ps1`
맨 위의 이름 표도 같은 값을 쓴다.

## 환경 변수

모두 선택이다.

| 이름 | 하는 일 |
|---|---|
| `GITHUB_TOKEN` | GitHub API 토큰 ([GitHub API 한도](HowItWorks.md#github-api-한도)) |
| `ACTREPACK_DEBUG=1` | 예상 못 한 오류의 전체 내용을 화면에도, SigRegen 의 자세한 진단 출력 |
| `ACTREPACK_NO_ELEVATE=1` | 관리자 권한 확인 창을 띄우지 않고 실패 (자동화·시험) |
| `ACTREPACK_ACT_CONFIG=<파일>` | 다른 ACT 설정 파일을 씀 (시험 — 진짜 ACT 를 건드리지 않고 설치까지) |
| `ACTREPACK_UPDATE_FEED=<폴더>` | 자동 업데이트를 GitHub 대신 그 폴더의 zip·`.sha256`·`.sig` 로 (시험) |
| `ACTREPACK_SIGNING_KEY=<파일>` | 배포 서명 개인키 위치 (`release-keygen` / `release-sign` 의 기본값) |
| `ACTREPACK_PORTABLE_PYTHON=1` | (시험) 설치된 Python 이 없는 것으로 보고 휴대용 Python 을 받아 씀 |
| `FFXIV_SIGNATURES_BACKTEST_DIR=<폴더>` | SigRegen 백테스트용 exe 폴더 ([시험](Development.md#시험)) |
| `ACTREPACK_REPLAY=1` 또는 `<tools 폴더>` | 지난 패치 재현 선택 실행 시험(`ReplayBacktests`)을 돌림 — 폴더면 그 안의 도구를 복사해 씀 ([시험](Development.md#시험)) |

예상하지 못한 오류는 짧은 안내만 보이고 자세한 내용은 `%TEMP%\ActPluginRepack-error.log` 에 남는다(`ACTREPACK_DEBUG=1` 이면 화면에도).
이 파일은 이어 쓰며(한 건마다 맨 앞에 시각과 명령), 256KB 를 넘으면 최근 것만 남긴다.

## 작업 기록과 문제 보고

작업 폴더를 바꾸는 명령(prepare / build / install / restore / hotfix / rsv / repack)과 GUI 의 진행 화면 작업은 작업 폴더의
`logs\<yyyyMMdd-HHmmss>-<작업>.log` 에 화면·콘솔과 같은 줄을 시각과 함께 남긴다(끝에 종료 코드, GUI 는 단계와 결과).
최근 30개만 두고 오래된 것부터 지운다. 관리자 권한으로 다시 실행된 쪽의 출력은 원래 쪽 기록에 들어간다. 기록을 쓰지
못해도 작업은 그대로 한다.

`report [--out <zip | 폴더>]` 는 문제 보고용 기록을 zip 하나로 묶는다(GUI 도움말 화면, 실패·경고로 끝난 진행 화면의
**문제 보고용 기록 저장** 과 같은 것 — GUI 는 바탕화면에 `ActPluginRepack-기록-<시각>.zip`).

- `summary.txt`: 프로그램·Windows·.NET 버전, 64비트·관리자 여부, 작업 폴더, `status` 와 같은 상태 줄과 다음에 할 일, 넣지 못한 것
- `logs\`: 작업 기록 최근 10개, 오류 기록(`error.log`)
- `state\`: `input\.state.json`, `output\.build.json`, `config.json`, `gui.json`, 자동 업데이트 일지, 최근 백업 기록
  5개(`backups\<시각>\backup.json`), `input\signatures\*.txt`, `input\opcodes` · `input\overrides` 파일 목록(이름·크기·SHA256)
- `act.txt`: ACT 설정의 플러그인 목록, 설치 대상, FFXIV 플러그인 폴더(해루봇 언어 폴더 포함)의 파일 목록(크기·수정 시각·버전·SHA256),
  OverlayPlugin `opcodes.jsonc`

GitHub 토큰, HTTP 캐시, dll·exe 자체, `snapshots\`, `tools\` 는 넣지 않는다. 폴더 경로가 들어 있어 Windows 사용자 이름이
보일 수 있다. 항목 하나를 읽지 못해도 묶기는 계속하고 `summary.txt` 끝에 적는다.

## 시그니처 명령 — 다른 도구용 (`sigscan`, `sigregen --patterns`)

OverlayPlugin / cactbot 의 패치 점검 도구가 부르는 약속이다(같은 기능을 라이브러리로 직접 쓰려면
`lib\FFXIV.Signatures` — [README](../lib/FFXIV.Signatures/README.md)). 도구는 CLI 위치를 환경 변수 `ACT_PLUGIN_CLI` 로 받고,
`--region` 을 쓸 때는 `--root <작업 폴더>` 를 꼭 준다(GUI 의 작업 폴더는 PowerShell 에서 `$env:LOCALAPPDATA\ActPluginRepack`).
`snapshots\` 의 파일을 직접 읽지 말고 `--region` / `--version` 으로 받는다 — prepare 가 옛 보관본을 지우고, 없으면 받으며 서명도 확인한다.

**패턴 목록 파일** (JSON 배열. 모르는 필드는 무시, 이름은 겹치면 안 됨):

```json
[ { "name": "CombatantMemory.charmap", "pattern": "488B5720B8000000E0483BD00F84????????488D0D",
    "syntax": "hex", "offset": 0, "rip": true, "ripOffset": 0 } ]
```

- `syntax`: `hex`(기본 — 공백 없이 두 글자가 1바이트, `??` 가 와일드카드 1바이트, 대소문자 상관없음) 또는 `pelite`(이 도구의 표기).
- 값 해석 (OverlayPlugin / cactbot SigScan 규칙): 일치 시작 A, 길이 L 일 때 `rip` 이면 `[A+L+offset]` 의 int32 v 로
  `A+L+offset+4+ripOffset+v`, 아니면 `[A+L+offset]` 의 int64 에서 ImageBase 를 뺀 값. `offset` 이 없으면 위치만 찾는다.

**sigscan** — `--patterns <json> (--exe <exe> | --region <지역> --version <게임 버전 | 패치 번호>) [--all-sections] [--json]`.
기본은 `.text` 만 훑는다. `--all-sections` 는 파일에 데이터가 있는 모든 섹션(프로세스 메모리의 모듈 전체를 훑는 런타임
스캐너가 볼 개수 — 섹션 경계에 걸친 일치·PE 헤더·파일에 없는 bss 는 빼고). 출력(`--json`, stdout — 진행 메시지는 stderr,
UTF-8):

```json
{ "format": 1, "apiVersion": 1, "exe": "...", "gameVersion": "2026.10.06.0000.0000", "scope": "text", "imageBase": "0x140000000",
  "results": [ { "name": "...", "pattern": "<받은 그대로>", "matches": 1,
                 "hits": [ { "rva": "0x60c41d", "matchSection": ".text", "crosses64k": false, "missedByChunkScan": false,
                             "readRva": "0x60c432", "resolvedRva": "0x28ad410", "section": ".data", "error": null } ] } ] }
```

- 주소는 `0x` 를 붙인 16진수 RVA. `hits` 는 패턴마다 16개까지, `matches` 는 실제 개수.
- `section` 은 결과 주소가 속한 섹션(어디에도 없으면 null — 엉뚱한 곳일 가능성), `error` 는 값을 읽지 못한 까닭.
- `crosses64k`: 일치 구간이 64KB 경계를 넘음.
- `missedByChunkScan`: OverlayPlugin / cactbot 의 SigScan 이 이 일치를 놓침 (`offset` 이 없으면 null). 두 스캐너는 모듈을 64KB
  덩어리로 읽고 "일치 + max(0, offset) + 읽을 값(rip 4 / 아니면 8바이트)" 이 덩어리 안에 다 들어오는 것만 본다 — 경계 바로 앞에서
  끝나는 일치도 읽을 값이 경계를 넘으면 놓친다(`crosses64k` 보다 넓다).
- 읽을 자리가 이미지 밖이면 `readRva` 는 null.
- 고정 바이트가 없거나(`????`, `[4]`) 0바이트에도 맞는 패턴(`(4C|)` 등)은 받지 않는다. 일치 하나를 찾는 데 30초가 넘으면
  (건너뛰기를 겹친 패턴의 되짚기 폭증) 오류로 멈춘다.
- `gameVersion` 은 `--exe` 면 exe 옆 `ffxivgame.ver` (없으면 null).

**sigregen --patterns** — `--patterns <json> (--old <exe> | --region <지역> --old-version <버전>) (--new <exe> | --new-version <버전>) [--json]`.
패턴마다 옛 exe 에서 맞던 것을 새 exe 용으로 다시 만든다. `offset` 이 있으면 옛 exe 에서 그 자리가 어느 명령어의 어느
필드(RIP 변위 / 변위 / rel32 / 즉치)인지 보고, 새 패턴에서 같은 필드의 자리로 새 `offset` 을 낸다(명령어 모양·필드 크기가
같고 주소성 필드면 가리키는 섹션도 같아야 함 — 아니면 실패). 출력:

```json
{ "format": 1, "apiVersion": 1, "old": "...", "oldVersion": "...", "new": "...", "newVersion": "...",
  "results": [ { "name": "...", "ok": true, "unchanged": false, "pattern": "<입력과 같은 표기>", "pelitePattern": "...",
                 "offset": -15, "ripOffset": 1, "readField": "rip disp of cmp byte ptr [142AE960Ah],0", "detail": "..." } ] }
```

- `unchanged`: 옛 패턴이 새 exe 에서도 같은 개수로 맞고 `offset`·`ripOffset` 도 받은 값 그대로 — 고칠 것이 없음 (format 1 안에서
  판단 기준을 바로잡았다: 예전에는 패턴만 같으면 true 여서 offset 이 바뀐 것을 놓쳤다). 패턴은 그대로 맞지만
  읽는 자리가 바뀌었으면 `ok: true, unchanged: false` 에 `pattern` 은 받은 그대로, `offset`/`ripOffset` 은 새 값이다. call 형은 call 대상이
  옛 대상과 대응해야 하고, 대응하는 함수를 찾지 못해 확인하지 못했으면 `callTargetUnverified: true` (`detail` 에도 적음).
- `ripOffset`: 읽는 필드가 RIP 변위일 때 새 명령어에서 변위 뒤로 남는 바이트 수(예: `cmp byte ptr [rip+x],0` 은 1).
- call 은 hex 표기에서 `E8????????` 로 낸다. 대안·건너뛰기가 든 패턴은 hex 로 나타낼 수 없어 `ok: false`(`pelitePattern` 에 있음).
- 전역 변수 주소 자체가 두 exe 에서 같은 대상인지는 확인할 방법이 없다 — 만든 패턴은 `sigscan` 으로 다시 보고, 게임에서 값을 확인할 것.

두 형식 모두 필드는 추가만 하고 빼거나 의미를 바꾸지 않는다(바꿔야 하면 `format` 을 올린다).
