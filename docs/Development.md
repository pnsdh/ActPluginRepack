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
    Pipeline\                 Prepare · Build · Install · Status · MaterialState(재료 기록) · Hotfix
    Plugin\                   Costura 리소스 · Repacker(주입) · 플러그인 릴리스 · 메모리/Oodle 시그니처 · RSV · Exporter
    GameData\                 CSV 변환 · CSV 수집(oxidizer / SharpVision / 공개 저장소)
    Opcodes\                  opcode 수집(Yarukon / karashiiro / opcodediff / 로컬 / IDA) · 부가 출력
    Signatures\               패턴 · PE · deucalion · sigcheck · 자동 복구 · SigRegen · Thaliak(옛 exe)
src\ActPluginRepack.Cli\      명령줄 (옵션 해석 → Core 호출 → 콘솔 출력)
src\ActPluginRepack.Gui\      화면 (WPF + WPF-UI, MVVM) — 간편/고급 모드, 관리자 권한 설치는 CLI 를 UAC 로 띄워서
tests\ActPluginRepack.Tests\  Core 회귀 시험 (xUnit)
packaging\                    배포 묶음에 넣는 사용법.txt
```

Core 는 화면을 모른다: 메시지는 `Log` 로 보내고, 작업을 시작한 쪽이 `Operation.Begin(보고자, 취소 토큰)` 으로 보고 대상과
취소를 정한다. 결과는 `PrepareResult` / `BuildResult` / `StatusReport` 같은 객체로 돌려준다.

- 화면 문구와 Core 의 오류 문구는 모두 합니다체(명령은 "~하세요")이고 진행 기록 줄만 짧은 메모체다. CLI 와 화면이 같은 문장을 쓴다.
- 한글이 든 `.ps1` 은 UTF-8 **BOM** 으로 저장한다(PowerShell 5.1 인코딩 문제).
- GUI 는 항상 어두운 테마, 한국어는 왼쪽 정렬·어절 단위 줄바꿈(`KeepAll` 변환기).

## 시험

`dotnet test tests\ActPluginRepack.Tests` (몇 초 — 네트워크 시험은 로컬 가짜 서버와 바로 실패하는 연결로, 캐시와 받는
폴더는 임시 폴더에).

- 시험 프로젝트가 CLI 도 같은 구성으로 같이 빌드하고, 관리자 셸이면 `ElevationTests` 가 그 CLI 를 실제로 관리자로 띄워
  보호된 폴더에 설치·복원·실패·ACT 실행 중(코드 4)을 확인한다. UAC 확인 창 자체는 보안 데스크톱이라 자동으로 누를 수 없다.
- SigRegen 백테스트는 실제 deucalion 수정 이력의 옛/새 exe 가 필요해서, 그 exe 들
  (`fetch-exe --patch <패치 파일 버전> --out <폴더>\<버전>.exe`)이 있는 폴더를 `ACTREPACK_BACKTEST_DIR` 로 알려 줄 때만 돈다
  (목록은 `SigRegenBacktests.cs`).
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
GUI exe 버전을 확인하고, 지금 폴더의 파일을 바꿔 넣은 뒤 새 버전을 띄우고 끝낸다. 새 버전은 이전 프로세스가 끝나길
기다렸다가 비켜 둔 `.old` 를 지운다.

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
- 패치 번호(태그)는 게임 버전에서 구하고(글로벌 먼저), 대응표에 없으면 `--tag 7.58` 로 준다.
- prepare 이후 게임·플러그인이 바뀌었거나 빌드가 prepare 보다 먼저면 멈춘다(틀린 패치본을 올리지 않게).
- 올리는 `gh release create ...` 명령을 알려 줄 뿐 직접 올리지는 않는다. 공식 업데이트가 나오면
  `gh release delete <패치> --repo pnsdh/FFXIV_ACT_Plugin_HotFix --cleanup-tag` 로 내린다.
- GUI 는 설정의 "패치본 배포 기능"을 켜야(기본 꺼짐) 고급 모드에 "패치본 배포 묶음 만들기 (hotfix)" 가 보이고, 결과 화면의
  [올리기 명령 복사] 로 gh 명령을 복사한다.
