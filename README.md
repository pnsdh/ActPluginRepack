# ActPluginRepack — 패치 직후 임시 플러그인 만들기

플러그인 작성자가 새 버전을 내기 전에, **opcode / 스킬명 오버라이드 / 게임 데이터 테이블**을
갈아끼운 `FFXIV_ACT_Plugin.dll` 을 직접 만드는 도구. 소스 재빌드 없이 DLL 안의 리소스만 교체한다.

대상 데이터는 플러그인 DLL 안에 **두 겹으로**(Costura.Fody) 박혀 있어서, 바깥 DEFLATE 를
풀고 → 안쪽 리소스를 바꾸고 → 다시 압축해 되넣어야 한다. 이 도구가 자동으로 한다:

```
FFXIV_ACT_Plugin.dll
  ├─ (DEFLATE) costura.machina.ffxiv.dll.compressed
  │     └─ Machina.FFXIV.dll
  │           └─ Machina.FFXIV.Headers.Opcodes.<Region>.txt               ← opcode
  └─ (DEFLATE) costura.ffxiv_act_plugin.resource.dll.compressed
        └─ FFXIV_ACT_Plugin.Resource.dll
              ├─ ...Generated.Override.global_*.txt                        ← override
              └─ ...Generated.<List>_<Lang>.txt                            ← 게임 데이터
```

## 준비물

.NET SDK (net10.0) + `input\` 재료. **저장소는 폴더 구조만 제공한다** — 재료는 직접 구해서 넣는다:

| 폴더 | 넣을 것 | 출처 |
|---|---|---|
| `input\plugin\` | 원본 `FFXIV_ACT_Plugin.dll` (필수) + 배포판에 동봉된 나머지 `*.dll` (output 에 복사됨) | [ravahn](https://github.com/ravahn/FFXIV_ACT_Plugin) 공식 릴리스 |
| `input\opcodes\` | `Global/Korean/Chinese/TraditionalChinese.txt` — 각 줄 `이름\|16진수` | 직접 편집 (`export` 로 현재값 추출 가능) |
| `input\overrides\` | `global_*.txt` — 각 줄 `옛이름\|새이름` (선택) | 직접 편집 (파일명은 `global_` 로 시작해야 함) |
| `input\csv\<lang>\` | 데이터마이닝 CSV (lang: ko/en/ja/de/fr/cn/tc) | 커뮤니티 데이터마이닝 시트 |

## 사용법

```powershell
.\build.ps1
```

CSV 변환(→`data\`) + 주입 + 부속 파일 복사가 한 번에 끝난다. 나온 `output\` 폴더를
ACT 플러그인 폴더에 통째로 복사하면 된다(원본 백업 권장).

- `-NoCsv` : CSV 재변환 생략. `-Plugin <경로>` : 원본 플러그인 지정.
- input 에 **넣은 것만 교체**되고 나머지는 베이스 DLL 원본값 그대로 동작한다.
  재료 폴더가 비어 있으면 그 단계는 자동 생략.
- CSV 변환이 실패하면 빌드는 즉시 중단된다(누락분이 조용히 남는 것 방지).

단계별로 돌리고 싶으면 CLI 서브커맨드를 직접 쓴다 (경로 기본값은 전부 프로젝트 루트 기준):

```powershell
$cli = 'tool\bin\Release\net10.0\ActPluginRepack.dll'   # build.ps1 이 빌드해 둠
dotnet $cli repack                                   # opcode/override/data 주입만
dotnet $cli csv --csv-dir input\csv\ko               # CSV -> data\ 변환만
dotnet $cli export --what opcodes                    # 내장 리소스 추출 (data / overrides / opcodes)
```

## 교체 가능한 리소스

- **opcode**: region 4개 고정(`Global/Chinese/Korean/TraditionalChinese.txt`). 그 외 이름은 무시.
- **override**: 로더가 `global_` 접두사 리소스를 전부 열거하므로 개수·이름 자유, **신규 추가 가능**.
- **게임 데이터**: CSV 를 넣으면 해당 리스트만 변환·교체. **기존 리소스명만 교체**(추가 불가).

| 생성 리스트 | 필요한 CSV | 적용 언어 |
|---|---|---|
| ActionList / StatusList | `Action.csv` / `Status.csv` | 7개 언어 전부 |
| TerritoryList | `TerritoryType.csv` + `PlaceName.csv` | en/ja/ko/de/fr |
| ContentFinderList | `ContentFinderCondition.csv` | en/ja/ko/de/fr |
| ActionCategoryList / WorldList / MapList ※ | `Action.csv` / `World.csv` / `Map.csv`+`PlaceName.csv` | **en 만** |

- ⚠ id 진법: ActionList/StatusList/ActionCategoryList/MapList = **16진수**,
  TerritoryList/ContentFinderList/WorldList = **10진수** (변환기가 자동 처리).
- ※ 고정 3종은 플러그인이 지역 무관하게 `_English` 만 읽는다 — `input\csv\en` 이 있을 때만
  생성·교체하고, 없으면 베이스 DLL 영어값을 유지한다.
- cn/tc 폴더는 ActionList/StatusList 만 유효(DLL 에 나머지 cn/tc 리소스가 없음).

## 주의

- **strong name**: 서명은 무효가 되지만 공개키 토큰을 유지하므로 ACT 의 full-trust 로컬 로드
  (.NET Framework strong-name bypass)로 정상 로드된다.
- 새 플러그인 버전이 나오면 그 DLL 로 같은 작업을 반복하면 된다. 리소스/Costura 이름이
  바뀌었을 때만 `tool\Costura.cs` 의 상수를 수정.
- 한글 메시지가 든 `.ps1` 은 UTF-8 **BOM** 으로 저장할 것(PS 5.1 인코딩 문제).

## 라이선스 / 면책

이 저장소의 도구 코드는 [MIT License](LICENSE)다.

비공식 개인 도구이며 Square Enix, ravahn(FFXIV_ACT_Plugin) 과 무관하다. 플러그인
바이너리는 저장소에 포함하지 않으므로 공식 배포처에서 직접 받는다. 패치된 플러그인의
사용(및 FINAL FANTASY XIV 관련 약관 준수)은 본인 책임이다.
FINAL FANTASY XIV © SQUARE ENIX CO., LTD.
