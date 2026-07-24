# ActPluginRepack — 패치 직후 임시 플러그인 만들기

플러그인 작성자가 새 버전을 내기 전에, **opcode / 스킬명 오버라이드 / 게임 데이터 테이블**을
갈아끼운 `FFXIV_ACT_Plugin.dll` 을 직접 만드는 도구. 소스 재빌드 없이 DLL 안의 리소스만 교체한다.

대상 데이터는 플러그인 DLL 안에 **두 겹으로**(Costura.Fody) 박혀 있고, 바깥 DEFLATE 해제 →
안쪽 리소스 교체 → 재압축을 이 도구가 자동으로 한다:

```
FFXIV_ACT_Plugin.dll
  ├─ (DEFLATE) costura.machina.ffxiv.dll.compressed
  │     └─ Machina.FFXIV.dll
  │           └─ Machina.FFXIV.Headers.Opcodes.<Region>.txt               ← opcode
  └─ (DEFLATE) costura.ffxiv_act_plugin.resource.dll.compressed
        └─ FFXIV_ACT_Plugin.Resource.dll
              ├─ ...Generated.Override.global_*.txt                        ← 스킬명 오버라이드
              └─ ...Generated.<List>_<Lang>.txt                            ← 게임 데이터
```

## 빠른 시작

1. .NET SDK (net10.0) 를 설치한다.
2. 원본 `FFXIV_ACT_Plugin.dll` ([ravahn 공식 릴리스](https://github.com/ravahn/FFXIV_ACT_Plugin))을
   `input\plugin\` 에 넣는다. 배포판에 동봉된 나머지 `*.dll` 도 같이 넣으면 output 에 복사된다.
3. 아래 [재료 준비](#재료-준비)에서 **바꿀 것만** 준비해 넣는다.
   넣은 것만 교체되고, 안 넣은 리소스는 베이스 DLL 원본값 그대로 동작한다.
4. 실행한다:
   ```powershell
   .\build.ps1
   ```
5. `output\` 폴더를 ACT 플러그인 폴더에 통째로 복사한다(원본 백업 권장).

옵션: `-NoCsv` (CSV 재변환 생략) / `-Plugin <경로>` (원본 플러그인 지정)

## 재료 준비

세 재료 모두 선택사항이다 — 바꿀 것만 넣으면 되고, 최소 하나만 있으면 빌드된다.

### opcode — `input\opcodes\`

- 파일: `Global.txt` / `Korean.txt` / `Chinese.txt` / `TraditionalChinese.txt` — region 4개 고정, 그 외 이름은 무시.
- 형식: 각 줄 `이름|16진수`. `export` 커맨드로 현재 내장값을 추출해 시작점으로 쓸 수 있다.

구하는 곳:

- [ACT_FFXIV 디스코드](https://discord.gg/9tHJ7s2P3r)의
  [#contribute-logfiles](https://discord.com/channels/551474815727304704/551527590800654386) 에 올라오는 제보
- [karashiiro/FFXIVOpcodes](https://github.com/karashiiro/FFXIVOpcodes)
- 직접 조사할 때 참고: [xivdev/opcodediff](https://github.com/xivdev/opcodediff),
  [Yarukon/FFXIVNetworkOpcodes](https://github.com/Yarukon/FFXIVNetworkOpcodes/commits/master/)

### 스킬명 오버라이드 — `input\overrides\`

- 파일: `global_*.txt` — 파일명이 `global_` 로 시작해야 로더가 읽는다.
- 형식: 각 줄 `옛이름|새이름` (예: `_rsv_30478_..._|Claw to Tail`).
- 로더가 접두사로 리소스를 **전부 열거**하므로 개수·이름 자유, 기존 수정과 **신규 추가** 모두 가능.

구하는 곳:

- [OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin) 사용 중 RSV 데이터가 있는
  인스턴스에 입장하면 ACT 로그에 **262 라인**으로 기록된다 — 여기서 추출하는 방식이 일반적.
- 디스코드 #contribute-logfiles 에도 올라오지만 opcode 에 비해 기여가 늦는 편이라,
  패치 직후 임시 플러그인 용도의 출처로는 부적절하다.

### 게임 데이터 — `input\csv\<lang>\`

데이터마이닝 CSV 를 언어 폴더(ko/en/ja/de/fr/cn/tc)에 넣으면
빌드가 `data\` 의 리스트로 변환해 교체한다. `data\` 는 자동 생성 산출물이므로 손대지 않는다.

구하는 곳:

- [xivapi/ffxiv-datamining](https://github.com/xivapi/ffxiv-datamining) 에 올라오는 CSV
- 직접 추출할 때 참고: [skyborn-industries/xiv-data-oxidizer](https://github.com/skyborn-industries/xiv-data-oxidizer)

| 생성 리스트 | 필요한 CSV | 적용 언어 |
|---|---|---|
| ActionList / StatusList | `Action.csv` / `Status.csv` | 7개 언어 전부 |
| TerritoryList | `TerritoryType.csv` + `PlaceName.csv` | en/ja/ko/de/fr |
| ContentFinderList | `ContentFinderCondition.csv` | en/ja/ko/de/fr |
| ActionCategoryList / WorldList / MapList ※ | `Action.csv` / `World.csv` / `Map.csv`+`PlaceName.csv` | **en 만** |

- ※ 고정 3종은 플러그인이 지역 무관하게 `_English` 만 읽는다 — `input\csv\en` 이 있을 때만
  생성·교체하고, 없으면 베이스 DLL 영어값을 유지한다.
- cn/tc 폴더는 ActionList/StatusList 만 유효하다(DLL 에 나머지 cn/tc 리소스가 없음).
- 데이터 리스트는 기존 리소스명만 교체된다(신규 추가는 오버라이드만 가능).
- id 진법(16진수/10진수)은 리스트마다 다르지만 변환기가 자동 처리한다.
- CSV 변환이 실패하면 빌드는 즉시 중단된다(누락분이 조용히 남는 것 방지).

## CLI 단계별 실행

`build.ps1` 은 아래 CLI 의 `build` 를 호출하는 런처다. 단계별로 돌리고 싶으면 직접 쓴다
(경로 기본값은 전부 프로젝트 루트 기준):

```powershell
$cli = 'tool\bin\Release\net10.0\ActPluginRepack.dll'   # build.ps1 이 빌드해 둠
dotnet $cli repack                                   # opcode/override/data 주입만
dotnet $cli csv --csv-dir input\csv\ko               # CSV -> data\ 변환만
dotnet $cli export --what opcodes                    # 내장 리소스 추출 (data / overrides / opcodes)
```

## 동작 / 주의

- **strong name**: 서명은 무효가 되지만 공개키 토큰을 유지하므로 ACT 의 full-trust 로컬 로드
  (.NET Framework strong-name bypass)로 정상 로드된다.
- 새 플러그인 버전이 나오면 그 DLL 로 같은 작업을 반복하면 된다. 리소스/Costura 이름이
  바뀌었을 때만 `tool\Costura.cs` 의 상수를 수정.
- 한글 메시지가 든 `.ps1` 은 UTF-8 **BOM** 으로 저장할 것(PS 5.1 인코딩 문제).

## 라이선스 / 면책

이 저장소의 도구 코드는 [MIT License](LICENSE)다.

비공식 개인 도구이며 Square Enix, ravahn(FFXIV_ACT_Plugin)과 무관하다. 플러그인
바이너리는 저장소에 포함하지 않으므로 공식 배포처에서 직접 받는다. 패치된 플러그인의
사용(및 FINAL FANTASY XIV 관련 약관 준수)은 본인 책임이다.
FINAL FANTASY XIV © SQUARE ENIX CO., LTD.
