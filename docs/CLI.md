# 명령줄 (CLI)

화면 프로그램(GUI)과 같은 일을 명령줄로 한다. 단계별 실행, 정보 조회, 자동화용. 동작의 자세한 설명은 [동작 원리](HowItWorks.md).

## 빠른 시작

1. **.NET 10 SDK** 로 이 저장소를 빌드한다(`build.ps1` 이 해 준다). 배포본의 `cli\ActPluginRepack.exe` 도 같은 명령을 쓰지만,
   저장소 밖에서는 작업 폴더를 `--root` 로 준다(GUI 와 같은 데이터: `--root %LOCALAPPDATA%\ActPluginRepack`).
2. 게임 클라이언트 경로를 설정한다: `detect --save` 가 설치된 클라이언트를 찾아 `config.json` 에 적는다(기본 설치 경로,
   설치 기록, XIVLauncher, Steam). 못 찾으면 `config.example.json` 을 `config.json` 으로 복사해 직접 적는다.
3. **ACT 와 게임을 끄고** 실행한다:
   ```powershell
   .\build.ps1 -Prepare -Install
   ```
   최신 공식 플러그인을 받고 → 시그니처·opcode·게임 데이터를 준비하고 → 패치한 플러그인을 `output\` 에 만들고 →
   ACT 의 FFXIV 플러그인 폴더에 설치한다(원래 파일은 `backups\` 에). 관리자 권한이 필요하면 설치 단계만 UAC 로 올라간다.
4. ACT 를 실행한다(해루봇 사용자는 해루봇에서 "ACT 실행").

```powershell
$cli = 'src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.dll'   # build.ps1 이 빌드해 둠
dotnet $cli status      # 지금 상태와 다음에 할 일 (공식 플러그인이 이미 맞으면 그렇다고 알려 줌)
dotnet $cli restore     # 마지막 설치 전으로
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

`build.ps1` 은 아래의 `prepare` / `build` / `install` 을 부르는 런처다. 경로 기본값은 작업 폴더 기준, 모든 옵션은 `help`.

```powershell
$cli = 'src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.dll'   # 배포본은 cli\ActPluginRepack.exe --root <작업 폴더>
dotnet $cli status                                   # 지금 상태와 다음에 할 일
dotnet $cli detect --save                            # 설치된 게임 클라이언트를 찾아 config.json 에
dotnet $cli prepare                                  # 재료 준비 + 패치 필요 여부 판단
dotnet $cli build                                    # output\ 에 플러그인 만들기
dotnet $cli install                                  # ACT 에 설치 (원래 파일은 backups\)
dotnet $cli install --official                       # 패치본 대신 공식 최신본 설치 (공식이 이미 맞을 때)
dotnet $cli restore                                  # 마지막 설치 전으로 (--backup <폴더>)
dotnet $cli rsv                                      # ACT 로그의 RSV -> input\overrides\global_rsv.txt
dotnet $cli github                                   # GitHub API 사용량과 쓰는 토큰
dotnet $cli github --token -                         # 토큰을 표준 입력으로 받아 확인 후 저장 (--clear 로 지움)
dotnet $cli sigcheck                                 # 게임 클라이언트에 시그니처가 맞는지 점검 (--game <폴더>)
dotnet $cli repack                                   # opcode/override/data 주입만
dotnet $cli csv --csv-dir input\csv\ko               # CSV -> data\ 변환만
dotnet $cli export --what opcodes                    # 내장 리소스 추출 (data / overrides / opcodes)
dotnet $cli fetch-exe --region korean --version 7.57 # 특정 버전 exe 를 공식 패치 서버에서 snapshots\ 로
dotnet $cli fetch-exe --patch <패치 파일 버전> --out a.exe   # 패치 파일 직접 지정
dotnet $cli sigregen --old a.exe --new b.exe --pattern "..."   # 시그니처 재생성
dotnet $cli hotfix                                   # 패치본 배포 묶음 (배포하는 사람용)
```

**prepare 옵션**: `--plugin <경로>` / `--no-plugin` / `--no-csv` / `--no-opcodes` / `--no-snapshot` / `--no-sigcheck` /
`--no-repair` / `--ida` · `--localdiff` (공개본이 있어도 직접 추출해 대조) / `--ida-target <지역>` / `--overwrite`
(SharpVision·oxidizer·IDA 캐시와 받은 파일 기억을 무시하고 다시) / `--sources a,b` (쓸 출처 제한, 검증용 — CSV:
`oxidizer`·`sharpvision`·`datamining`·`xivdata`, opcode: `yarukon`·`opcodediff`·`localdiff`·`ida`·`karashiiro`, 시그니처 복구:
`upstream`·`sigregen`; 한 분류의 이름이 없으면 그 분류는 전부 사용).

**install 옵션**: `--target <플러그인 폴더>` (여러 번 줄 수 있음 — 준 폴더에만. 생략하면 ACT 설정에서 찾고, 해루봇이면
한국·글로벌 서버용 폴더 모두 — [ACT 에 설치](HowItWorks.md#act-에-설치--되돌리기)) /
`--overlay <OverlayPlugin resources 폴더 | ->` (직접 지정하거나 건너뜀) /
`--official`. **rsv 옵션**: `--days <일>` / `--logs <파일|폴더,...>`.

작업 폴더에 쓰는 명령(prepare / build / install / restore / hotfix / rsv / repack / csv / export / `fetch-exe --version` /
`detect --save`)은 작업 폴더 잠금(`.actrepack.lock`)을 잡는다 — 다른 창이나 GUI 가 같은 폴더에서 작업 중이면 바로 멈춘다.
`export` 는 이미 있는 파일을 덮어쓰지 않는다(`--force`, 또는 `--out` 으로 다른 폴더).

## 종료 코드

Ctrl+C 는 진행 중인 작업을 취소한다(받던 파일, 외부 프로그램도 정리).

| 코드 | 뜻 |
|---|---|
| `0` | 정상 |
| `1` | 실패 (모든 명령). `sigregen` 은 패턴을 만들지 못했거나 `--truth` 와 다른 곳을 가리킬 때도 |
| `2` | `prepare`: 패킷 후크가 깨졌거나 시그니처 점검 자체가 실패 / `sigcheck`: 패킷 후크가 깨짐 / `rsv`: 공식·직접 넣은 override 와 값이 다른 키가 있음 |
| `3` | `prepare`: 준비 미완료 (opcode 를 못 구한 클라이언트가 있음 — 공개 전일 수 있으니 나중에 다시) |
| `4` | `install` / `restore`: ACT 나 게임이 실행 중 (관리자 권한으로 다시 실행한 쪽에서 났어도 같은 코드) |
| `130` | Ctrl+C 로 취소 |

`build.ps1 -Prepare` 는 prepare 가 0 이 아니면 빌드하지 않는다.

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
| `ACTREPACK_BACKTEST_DIR=<폴더>` | SigRegen 백테스트용 exe 폴더 ([시험](Development.md#시험)) |

예상하지 못한 오류는 짧은 안내만 보이고 자세한 내용은 `%TEMP%\ActPluginRepack-error.log` 에 남는다(`ACTREPACK_DEBUG=1` 이면 화면에도).
