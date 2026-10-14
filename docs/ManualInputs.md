# 재료 직접 넣기

`prepare` 없이 재료를 직접 넣어도 된다. 원본 `FFXIV_ACT_Plugin.dll`
([공식 릴리스](https://github.com/ravahn/FFXIV_ACT_Plugin/releases))과 동봉 DLL 을 `input\plugin\` 에 넣고, 바꿀 것만
아래 폴더에 넣은 뒤 `.\build.ps1` — 넣은 것만 바뀌고 나머지는 베이스 DLL 원본값 그대로다. 결과는 `output\`.
재료는 모두 선택사항이며 최소 하나만 있으면 빌드된다.

대상 데이터는 플러그인 DLL 안에 **두 겹으로**(Costura.Fody) 들어 있고, 바깥 DEFLATE 해제 → 안쪽 리소스 교체 → 재압축을
이 도구가 한다:

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

새 플러그인 버전에서 리소스/Costura 이름이 바뀌었을 때만 `src\ActPluginRepack.Core\Plugin\Costura.cs` 의 상수를 고친다.

## opcode — `input\opcodes\`

- 파일: `Global.txt` / `Korean.txt` / `Chinese.txt` / `TraditionalChinese.txt` — 그 외 이름은 무시.
- 형식: 각 줄 `이름|16진수`. `export --what opcodes` 로 지금 내장값을 뽑아 시작점으로 쓸 수 있다.
- 구하는 곳: `prepare`([opcode](HowItWorks.md#opcode)), [ACT_FFXIV 디스코드](https://discord.gg/9tHJ7s2P3r)의
  [#contribute-logfiles](https://discord.com/channels/551474815727304704/551527590800654386) 제보,
  [karashiiro/FFXIVOpcodes](https://github.com/karashiiro/FFXIVOpcodes). 직접 조사할 때 참고:
  [xivdev/opcodediff](https://github.com/xivdev/opcodediff), [Yarukon/FFXIVNetworkOpcodes](https://github.com/Yarukon/FFXIVNetworkOpcodes/commits/master/).

## 스킬명 오버라이드 — `input\overrides\`

- 파일: `global_*.txt` — 이름이 `global_` 로 시작해야(대소문자 구분) 플러그인 로더가 읽는다. 로더가 접두사로 리소스를
  전부 열거하므로 개수·이름 자유, 기존 수정과 신규 추가 모두 가능.
- 형식: 각 줄 `옛이름|새이름` (예: `_rsv_30478_..._|Claw to Tail`).
- 플러그인 로더는 파일마다 `ToDictionary` 로 읽는다 — **한 파일 안에 같은 키가 두 줄 있으면 그 파일과 그 뒤에 읽는
  override 파일이 전부 무시된다**. 그래서 build 가 이런 파일을 거부한다. 파일끼리 겹치면 나중에 읽은 것(추가한 리소스는
  맨 뒤)이 이긴다.

### RSV 가져오기 — `rsv`

고급 모드 "ACT 로그에서 RSV 가져오기". [OverlayPlugin](https://github.com/OverlayPlugin/OverlayPlugin) 을 쓰며 RSV 데이터가
있는 인스턴스에 들어가면 ACT 로그에 **262 줄**로 기록된다. `rsv` 가 ACT 로그 폴더(FFXIV 플러그인 설정 → ACT 설정 → 기본
위치)의 최근 로그(기본 30일, `--days` / `--logs`)에서 모아 `input\overrides\global_rsv.txt` 에 넣는다.

- 공식 플러그인에 같은 값으로 이미 있는 키는 넣지 않는다(나중에 공식이 따라잡으면 다음 가져오기 때 이 파일에서 지움).
- 공식이나 직접 넣은 override 파일과 값이 다르면 넣지 않고 알린다(exit 2). 같은 키가 로그마다 다르면 가장 최근 로그의
  값을 쓰고 참고로만 알린다. 이름에 `|` 나 줄바꿈이 있는 값(대사 등)은 뺀다.
- 디스코드에도 올라오지만 opcode 에 비해 늦는 편이라 패치 직후용 출처로는 맞지 않는다.

## 게임 데이터 — `input\csv\<lang>\`

데이터마이닝 CSV 를 언어 폴더(ko/en/ja/de/fr/cn/tc)에 넣으면 빌드가 `data\` 의 리스트로 변환해 교체한다(`data\` 는 자동
생성물). 구하는 곳: `prepare`([게임 데이터](HowItWorks.md#게임-데이터-csv)), [xivapi/ffxiv-datamining](https://github.com/xivapi/ffxiv-datamining).

| 생성 리스트 | 필요한 CSV | 적용 언어 |
|---|---|---|
| ActionList / StatusList | `Action.csv` / `Status.csv` | 7개 언어 전부 |
| TerritoryList | `TerritoryType.csv` + `PlaceName.csv` | en/ja/ko/de/fr |
| ContentFinderList | `ContentFinderCondition.csv` | en/ja/ko/de/fr |
| ActionCategoryList / WorldList / MapList ※ | `Action.csv` / `World.csv` / `Map.csv`+`PlaceName.csv` | **en 만** |

- ※ 고정 3종은 플러그인이 지역 무관하게 `_English` 만 읽는다 — `input\csv\en` 이 있을 때만 바꾸고, 없으면 원래 영어값.
- cn/tc 는 ActionList/StatusList 만 변환한다(DLL 에 나머지 cn/tc 리소스가 없음).
- xiv-data-oxidizer 형식(1행 `#,Name,...`)과 SaintCoinach 형식(1행 `key,0,1,...`, 2행 헤더, 3행 타입) 둘 다 읽는다.
- 데이터 리스트는 기존 리소스만 교체된다(신규 추가는 오버라이드만). id 진법(16/10진수)은 변환기가 리스트마다 맞춘다.
- CSV 변환이 실패하면 빌드는 바로 멈춘다(누락분이 조용히 남지 않게).

## 시그니처 — `input\signatures\`

플러그인은 게임 exe 에서 바이트 패턴(시그니처)으로 위치를 찾는 곳이 세 군데 있다. 게임 패치로 코드가 바뀌면 깨질 수
있으므로 `sigcheck` 로 점검한다(`prepare` 가 자동으로 하고, 깨졌으면 [자동 복구](HowItWorks.md#시그니처-자동-복구)한다):

| 종류 | 쓰는 곳 | 깨지면 | 입력 파일 |
|---|---|---|---|
| `deucalion` | 동봉 deucalion(`deucalion-*.distrib.dll`)의 패킷 후크 `Recv` / `Send` / `SendLobby` / `CreateTarget.*` | 패킷이 하나도 안 들어옴 — **치명**(exit 2) | `deucalion.txt` |
| `memory` | 플러그인의 메모리 읽기 7종(`ChatLog`, `MobArray`, `PartyList` …), `Global` / `TraditionalChinese` | 해당 정보만 못 읽음 — 경고 | `memory.txt` |
| `oodle` | Machina 의 Oodle 함수 12종(`Global` / `Korean`) | deucalion 캡처에는 안 쓰임 — 경고 | `oodle.txt` |

`config.json` 의 각 클라이언트에서 시그니처별 일치 개수를 보여 주고 항목별로 `OK` / `BROKEN` 을 판정한다.
`oodle.*_Train` 2개는 공식 플러그인에서도 오래전부터 0건이라 늘 경고로 나온다(무시해도 됨).

입력 파일 형식은 각 줄 `이름|패턴` (`#` 주석 가능). 이름은 `sigcheck` 가 보여 주는 것이고, memory/oodle 은 `이름`(모든 변형)
또는 `변형.이름`(예: `Global.ChatLog`). 패턴 문법은 [pelite](https://docs.rs/pelite/latest/pelite/pattern/fn.parse.html)
(`?` 는 1바이트, `$ { ' }` 는 call 대상 따라가기). 예: `Recv|E8 $ { ' } 4C 8B 47 10 44 8B 4F 1C`

빌드는 넣은 패턴을 `config.json` 의 클라이언트에서 먼저 검증하고(0건이면 중단) 다음처럼 적용한다:

- **deucalion**: deucalion dll 안의 시그니처 문자열을 같은 길이로 덮어쓰고, Machina 가 검사하는 deucalion 체크섬(SHA256)을
  새 해시로 맞춘다. 패턴이 원래보다 길면 공백을 빼서 맞추고, 그래도 길면 오류.
- **`Recv` + 플러그인 3.0.3.3 이상**: Machina 가 런타임에 deucalion 으로 RECV 시그니처를 보내므로 그 문자열도 같이 바꾼다
  (deucalion 은 내장 Recv 가 실패했을 때만 런타임 시그니처를 쓰므로 한쪽만 바꾸면 무시될 수 있다).
- **memory**: 플러그인 메모리 dll 의 `Signatures_<변형>.txt` 리소스에서 그 줄을 바꾼다.
- **oodle**: Machina 의 `SigScan` / `KoreanSigScan` 안의 패턴 문자열을 바꾼다.

> 이미 게임에 주입된 deucalion 은 게임을 끌 때까지 남아 있다. 패치본으로 바꾼 뒤에는 게임을 다시 시작한다.
