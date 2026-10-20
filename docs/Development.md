# 개발과 배포

## 빌드

**.NET 10 SDK** 가 필요하다(`dotnet --list-sdks` 에 `10.` 이 보이면 있음). `.\build.ps1` 이 CLI 를 빌드하고 build 를 돌린다
([CLI](CLI.md)). GUI 는 `dotnet build src\ActPluginRepack.Gui -c Release`. 버전은 루트의 `Directory.Build.props` 한 곳.

## 코드 구조

```
ActPluginRepack.slnx
src\ActPluginRepack.Core\     기능 전부 (라이브러리) — CLI 와 GUI 가 같이 쓴다
    Infrastructure\           Workspace(폴더 경로) · Operation/Log(진행 보고·취소) · Http · GitHub(토큰·사용량) · Proc · 자동 업데이트 · 오류
    Config\                   config.json, 지역(Region), 게임 클라이언트
    Pipeline\                 Prepare(결론은 PrepareOutcome) · Build · Install · Status · MaterialState(재료 기록) · Hotfix ·
                              FixFlow(간편 모드 [확인하고 고치기]의 판단) · HomeScreen(간편 모드 첫 화면의 판단) ·
                              ReportBundle(문제 보고용 기록 묶음) · TaskLog(작업 기록 logs\) · Replay(지난 패치 재현) ·
                              SourceReport(출처 보고 — 화면의 [출처], sources)
    Plugin\                   Costura 리소스 · Repacker(주입) · 플러그인 릴리스 · 메모리/Oodle 시그니처 · RSV · Exporter
    GameData\                 CSV 변환 · CSV 수집(oxidizer / SharpVision / 공개 저장소) · CSV 교차 확인(CsvCrossCheck)
    Opcodes\                  opcode 수집(Yarukon / karashiiro / opcodediff / 로컬 / IDA) · 부가 출력
    Signatures\               deucalion · sigcheck · 조용한 어긋남(sigdrift) · 자동 복구 · 교차 확인(SignatureCrossCheck) · ClientStructs 검문 · Thaliak(옛 exe) · SigLib(라이브러리 오류 -> ToolException)
src\ActPluginRepack.Cli\      명령줄 (옵션 해석 → Core 호출 → 콘솔 출력)
src\ActPluginRepack.Gui\      화면 (WPF + WPF-UI, MVVM) — 간편/고급 모드, 관리자 권한 설치는 CLI 를 UAC 로 띄워서
tests\ActPluginRepack.Tests\  Core 회귀 시험 (xUnit)
tests\ActPluginRepack.Gui.Tests\  GUI 화면 열기 시험 (xUnit, WPF)
lib\FFXIV.Signatures\         시그니처 라이브러리 (PE · 패턴 · 스캔 · 값 해석 · SigRegen) — OverlayPlugin/cactbot 점검 도구도 쓴다.
                              이 저장소의 다른 코드를 참조하지 않고 자체 Directory.Build.props·시험을 갖고 있어 폴더째 떼어 낼 수 있다
packaging\                    배포 묶음에 넣는 사용법.txt
```

Core 는 화면을 모른다: 메시지는 `Log` 로 보내고, 작업을 시작한 쪽이 `Operation.Begin(보고자, 취소 토큰)` 으로 보고 대상과
취소를 정한다. 결과는 `PrepareResult` / `BuildResult` / `StatusReport` 같은 객체로 돌려준다. `PrepareResult.Outcome`
(`PrepareOutcome`: PatchNeeded / NotNeeded / Undecided / HooksBroken / OpcodesMissing)이 prepare 의 결론이고, CLI 종료 코드와
GUI 간편 모드의 판단(`FixFlow.Decide`)이 모두 이것을 쓴다. 화면은 `FixFlow` · `HomeScreen` 의 결론을 문구와 버튼으로 바꾸기만
한다(판단을 시험할 수 있게 Core 에).

- 화면 문구와 Core 의 오류 문구는 모두 합니다체(명령은 "~하세요")이고 진행 기록 줄만 짧은 메모체다. CLI 와 화면이 같은 문장을 쓴다.
  화면으로는 할 수 없는 안내(옵션 이름, `input\plugin` 같은 작업 폴더 안의 경로)는 `Errors.CliOnly` / `Errors.CliOr` 로
  명령줄에서만 붙인다(`Errors.CommandLine` 은 CLI 의 Main 이 켠다). 간편 모드 화면 문구는 `Gui\Texts.cs` 의 `Titles` · `Notes` 에
  모으고, 자주 묻는 질문은 `HelpViewModel` · README · `packaging\사용법.txt` 가 같은 문장을 쓴다.
- 한글이 든 `.ps1` 은 UTF-8 **BOM** 으로 저장한다(PowerShell 5.1 인코딩 문제).
- GUI 는 항상 어두운 테마, 한국어는 왼쪽 정렬·어절 단위 줄바꿈(`KeepAll` 변환기).

## 시험

`dotnet test` (솔루션 전체 — `tests\ActPluginRepack.Tests` · `tests\ActPluginRepack.Gui.Tests` 와 `lib\FFXIV.Signatures\FFXIV.Signatures.Tests`, 몇 초. 네트워크
시험은 로컬 가짜 서버와 바로 실패하는 연결로, 캐시와 받는 폴더는 임시 폴더에).

- `tests\ActPluginRepack.Gui.Tests`: 화면(Views 의 XAML 전부와 MainWindow)을 모델 없이 만들어 배치까지 해 XAML · `x:Static` · `StaticResource` 오류를 잡는다 (창은 띄우지 않고 작업 폴더·gui.json·ACT 는 건드리지 않음). `AdvancedOptionsTests` 는 고급 모드에서 고른 것의 저장(gui.json)·"기본과 다른 선택" 줄을 본다. 간편 모드의 판단은 Core 의 `FixFlow` · `HomeScreen` 에 있고 `FixFlowTests` · `HomeScreenTests` 가 시험한다.
- 시험 프로젝트가 CLI 도 같은 구성으로 같이 빌드하고, 관리자 셸이면 `ElevationTests` 가 그 CLI 를 실제로 관리자로 띄워
  보호된 폴더에 설치·복원·실패·ACT 실행 중(코드 4)을 확인한다. UAC 확인 창 자체는 보안 데스크톱이라 자동으로 누를 수 없다.
- SigRegen 백테스트는 실제 deucalion 수정 이력의 옛/새 exe 가 필요해서, 그 exe 들
  (`fetch-exe --patch <패치 파일 버전> --out <폴더>\<버전>.exe`)이 있는 폴더를 `FFXIV_SIGNATURES_BACKTEST_DIR` 로 알려 줄 때만
  돈다 (목록은 `lib\FFXIV.Signatures\FFXIV.Signatures.Tests\SigRegenBacktests.cs`).
- **지난 패치 재현** (`replay --version <게임 버전>`, `Pipeline\Replay.cs`): 지난 출시를 "공개 opcode 자료가 아직 없는 패치 당일
  오전" 조건(출처 `localdiff` + `sigregen`)으로 다시 세워 prepare → build 를 돌리고, 그 출시 뒤에 나온 공식 플러그인(정답)과
  opcode · 패치 필요 판단 · 후크(새 exe 에서 같은 곳을 가리키는지) · sigcheck 를 비교한다. 베이스는 출시(Thaliak 이 처음 내려준
  시각) 전 마지막 공식 릴리스, 정답은 출시 뒤 다음 출시 전에 처음 나온 것(없으면 베이스가 정답)이고 `--base` / `--ref` 로 바꿀 수
  있다. 작업 폴더와 받은 파일 기억은 `%TEMP%\actrepack-replay\<지역>-<버전>\` 에만 쓴다(`--keep` 이 없으면 끝나고 지움).
  Thaliak 의 시각이 실제 출시보다 늦게 기록된 출시(7.56 — 공식 3.0.3.0 이 Thaliak 시각보다 먼저 나옴)는 베이스를 `--base` 로 준다.
  선택 실행 시험 `tests\ActPluginRepack.Tests\ReplayBacktests.cs`(글로벌 7.55 / 7.56h / 7.57, 한국 7.56h / 7.57)는 `ACTREPACK_REPLAY` 가 있을 때만
  돈다 — 값이 폴더면 그 안의 `python` · `radare2` · `opcodediff-venv` 를 복사해 쓴다. radare2 때문에 PowerShell / cmd 에서 돌린다.
- **수동 시험** (`tests\manual\`, 탐색기에서 더블클릭 — 진짜 ACT 와 작업 폴더는 건드리지 않음):
  - `uac-test.cmd`: 보호된 가짜 ACT 폴더에 일반 권한으로 install(확인 창 '예') / restore('아니요').
  - `patchday-e2e.cmd [-Previous 3.0.3.1]`: 패치 당일 재현 — 직전 공식 릴리스를 베이스로(버전 표시만 최신으로), 가짜
    ACT(`ACTREPACK_ACT_CONFIG`)에 GUI 간편 모드로 확인 → 빌드 → 설치 → 되돌리기를 해 보고, 창을 닫으면 패치본 opcode 를
    최신 공식본과 비교한다.

## 배포 묶음 — `.\publish.ps1`

`dist\ActPluginRepack-<버전>-win-x64.zip` 과 `.sha256`, `.sig` 를 만든다. **릴리스에는 셋 다 올린다**(서명이 없으면 자동
업데이트가 거부한다). **릴리스 태그는 `v2.0.0` 형식**이다 — 업데이트 확인은 `releases.atom`(토큰이 있으면 API)에서 `v` 태그만
골라 가장 높은 것과 비교한다.

```
ActPluginRepack-<버전>\
  ActPluginRepack.Gui.exe   실행할 파일 (단일 파일, .NET 10 데스크톱 런타임 필요 — 용량 때문에 런타임은 넣지 않음)
  사용법.txt                일반 사용자용 안내 (packaging\사용법.txt)
  ida\                      IDA 직접 추출용 래퍼
  cli\ActPluginRepack.exe   명령줄 도구 (GUI 의 관리자 권한 설치도 이것으로)
  licenses\                 내장 글꼴 라이선스 (SIL OFL)
```

- 실행 파일이 하나만 보이게 CLI 는 `cli\` 로 뺐다. publish 는 GUI 와 CLI 를 따로 단일 파일로 만든 뒤 필요한 것만 담고,
  구성과 `cli\ActPluginRepack.exe --version` 을 점검한다.
- 실행 파일에는 코드 서명(Authenticode)이 없어 처음 실행 때 SmartScreen 이 뜬다(사용법.txt 에 안내).
- 아이콘: `src\ActPluginRepack.Gui\Assets\make-icon.ps1` 로 만든 `app.ico`.
- 이 저장소의 릴리스에는 이 도구만 올린다. 이 도구로 만든 패치된 플러그인은
  [FFXIV_ACT_Plugin_HotFix](https://github.com/pnsdh/FFXIV_ACT_Plugin_HotFix) 에 따로 올린다(아래 `hotfix`).

## 자동 업데이트와 서명

GUI 의 [지금 업데이트] (`Infrastructure\AppUpdate.cs`)는 새 릴리스의 zip · `.sha256` · `.sig` 를 받아 SHA256, **서명**, 구성,
GUI exe 버전을 확인하고, 지금 폴더의 파일을 바꿔 넣은 뒤 새 버전을 띄운다. 새 버전이 시작 신호(`Local\ActPluginRepack.Gui.Started.<이전 pid>`)를
보내면 끝내고, 신호 없이 끝나거나 45초 안에 신호가 없으면(런타임이 맞지 않아 안내 창에서 멈춤 등) 새 프로세스를 끝내고
원래 파일로 되돌린 채 계속 쓴다. 새 버전은 이전 프로세스가 끝나길 기다렸다가 창을 띄우고, 창이 그려진 뒤에 비켜 둔 `.old` 를
지운다. 그 전에 실패하면(창을 만들지 못함 등) 스스로 이전 버전으로 되돌리고 그 버전을 다시 띄운다.

- 확인이 끝나기 전에는 아무것도 바꾸지 않는다. 바꿔 넣을 때는 새 파일을 모두 `<이름>.new` 로 복사한 뒤 이름만 빠르게
  바꾸고(GUI exe 는 맨 끝), 무엇을 비켜 뒀는지 일지(`.actrepack-update.json`)에 적는다. 도중에 실패하거나 프로세스가 죽으면
  다음 시작 때 일지대로 원래 파일로 되돌린다(이름 바꾸기를 다 끝내고 "끝남"만 못 적었으면 끝난 업데이트로 본다).
  되돌리기·정리는 일지에 적힌 파일만 건드린다.
- 배포본 구성(`cli\` 가 옆에 있음)일 때만 — 개발 빌드나 쓸 수 없는 폴더(Program Files 등)는 [직접 받기].
- **서명** (`Infrastructure\UpdateSignature.cs`): `.sha256` 은 받다가 깨진 것만 막으므로(계정이 털리면 zip 과 함께 바뀜),
  `publish.ps1` 이 배포하는 사람의 PC 에만 있는 개인키로 "zip 이름 + SHA256" 에 ECDSA P-256 서명을 하고 앱은 내장 공개키로
  확인한다. 개인키는 `%USERPROFILE%\.actrepack\release-signing-key.pem`(저장소 밖, 따로 백업, `ACTREPACK_SIGNING_KEY` 로
  다른 위치) — 잃으면 새 키를 넣은 버전을 사용자가 [직접 받기]로 한 번 받아야 한다. CLI `release-keygen` / `release-sign --zip`.
- 시험: `ACTREPACK_UPDATE_FEED=<폴더>` 면 GitHub 대신 그 폴더의 `ActPluginRepack-<버전>-win-x64.zip`(+`.sha256`, `.sig`)을 새
  버전으로 본다. `.\publish.ps1 -Version 2.0.1` 로 시험용 새 버전을 만든다.

## 패치본 배포 — `hotfix`

prepare / build 뒤에 `hotfix` 를 실행하면 `hotfix\<패치>\` 에
[FFXIV_ACT_Plugin_HotFix](https://github.com/pnsdh/FFXIV_ACT_Plugin_HotFix) 릴리스용 파일을 만든다.

- `FFXIV_ACT_Plugin_<패치>_custom_fix.zip`: `output\` 의 플러그인·deucalion + `extras\OverlayPlugin\opcodes.jsonc`
- `release-notes.md`: 게임 버전, 공식 플러그인·deucalion 버전, 만든 도구 버전, opcode 출처와 바뀐 수, 교체한 후크·메모리
  시그니처와 넣은 override 파일을 `input\.state.json` · `output\.build.json` · `input\` 에서 채우고 출처마다 링크를 건다.
  "알려진 문제" 만 손으로 고친다.
- 패치 번호(태그)는 게임 버전에서 구하고(글로벌 먼저), 대응표에 없으면 `--tag 7.58` 로 준다(GUI 는 hotfix 아래의 패치 번호 칸).
- 배포 전 점검: 마지막 prepare 가 [교차 확인](HowItWorks.md#교차-확인)을 하지 않았거나, 다른 것·대조하지 못한 것이 있었으면 경고한다.
  모두 대조해 다른 것이 없었을 때만 release-notes.md 에 "확인" 절을 넣는다. 공식 패치 서버 exe 로 준비했으면 게임 버전 줄에 그렇게 적는다.
- prepare 이후 게임·플러그인이 바뀌었거나 빌드가 prepare 보다 먼저면 멈춘다(틀린 패치본을 올리지 않게).
- 올리는 `gh release create ...` 명령을 알려 줄 뿐 직접 올리지는 않는다. 공식 업데이트가 나오면
  `gh release delete <패치> --repo pnsdh/FFXIV_ACT_Plugin_HotFix --cleanup-tag` 로 내린다.
- GUI 는 설정의 "고친 플러그인 배포 기능"을 켜야(기본 꺼짐) 고급 모드에 "고친 플러그인 배포 묶음 만들기 (hotfix)" 가 보이고, 결과 화면의
  [올리기 명령 복사] 로 gh 명령을 복사한다.
