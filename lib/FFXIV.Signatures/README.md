# FFXIV.Signatures

FFXIV 게임 exe(`ffxiv_dx11.exe`)에서 시그니처를 다루는 .NET 10 라이브러리다. PE 읽기, 패턴 파싱, 스캔, 값 해석,
패치 뒤 시그니처 재생성을 한다. ActPluginRepack 과 OverlayPlugin / cactbot 의 패치 점검 도구가 함께 쓴다.

- 이 폴더만으로 빌드된다. 이 폴더의 `Directory.Build.props` 가 저장소 루트의 설정을 막기 때문에, 폴더째 다른 저장소로
  옮겨도 똑같이 빌드된다. 외부 의존성은 [Iced](https://github.com/icedland/iced)(디스어셈블러) 하나다.
- 쓰는 쪽은 `ProjectReference` 로 이 폴더의 `FFXIV.Signatures\FFXIV.Signatures.csproj` 를 건다. NuGet 패키지로는 내지 않는다.
- **판 규칙**: 공개 API 는 추가만 하고 의미를 바꾸지 않는다. 깨는 변경이 필요하면 어셈블리 major 와
  `SignatureLibrary.ApiVersion`(지금 1)을 올린다. 어셈블리 판은 `FFXIV.Signatures.csproj` 의 `SignaturesVersion` 이다.
- 오류는 `SignatureException` 으로 던진다. 메시지는 사용자에게 그대로 보여도 되는 한국어 문장이다.
  null 인자(`Parse(null)`, `PeImage.Parse(null, …)`, `GameExe.ReadVersion(null)` 등)만 `ArgumentNullException` 이다 —
  입력이 잘못된 것이 아니라 부르는 코드의 실수라서. `TryParse(null)` 은 false.

## API

| 형식 | 하는 일 |
|---|---|
| `PeImage` | `Load(path)` / `Parse(bytes, what)`. `Bytes`, `Sections`(이름, RVA, 크기, `Executable`, `MappedSize`, `FileSize`), `ImageBase`, `Find(name)` / `Require(name)`(없으면 `SignatureException`), `Span(section)`(파일 정렬 채움 포함) / `FileSpan(section)`(메모리에 올라가는 부분만), `SectionAt(rva)`, `FileOffset(rva)`, `ReadInt32/64(rva)` |
| `SigPattern` | `Parse(text)`(pelite 표기) / `Parse(text, SigSyntax.Hex)` / `TryParse(text, out p)`. `Text`(pelite 표기 — Hex 로 파싱했으면 바꾼 결과), `HexToPelite` / `PeliteToHex`, `Scan(target)` → `Match` 목록 |
| `SigPattern.Match` | `Rva`(일치 시작), `Length`, `End`, `Section`, `Saves`(북마크 / `$ { ' }` 도착 주소. 선택되지 않은 대안 쪽의 북마크는 0), `CrossesBoundary(0x10000)`, `MissedByChunkedScan(offset, rip)` |
| `ScanTarget` | `new ScanTarget(path, ScanScope.Text \| AllSections)` 또는 이미 읽은 `new ScanTarget(peImage, path, scope)`. exe 를 한 번 읽어 여러 패턴에 돌려 쓴다. 만든 뒤에는 읽기만 하므로 여러 스레드에서 같이 써도 된다 |
| `SigValue` | `Resolve(image, match, offset, rip, ripOffset)` → `ReadRva`, `ResolvedRva`, `Section`, `Error` |
| `CodeImage` | 재생성용 exe 표현(.text 디스어셈블과 .pdata 함수 표). `new CodeImage(path)` 또는 이미 읽은 `new CodeImage(scanTarget)`(Text 범위만 — 같은 exe 를 스캔에도 쓸 때 파일과 .text 문자열을 두 번 들지 않게), `Target`(그 exe 의 `ScanTarget`), `Image`, `FunctionTokens(rva)`(감싼 함수의 명령어 모양 — 함수 비교용). 크므로(재생성 한 번 뒤 exe 하나에 200MB 남짓 — 그중 파일과 .text 문자열이 120MB 남짓) 같은 exe 는 하나만 만든다. 안에 캐시가 있어 **여러 스레드에서 동시에 쓰면 안 된다** |
| `SigRegen` | `Regenerate(oldExe, newExe, oldPattern, readOffset?, debug?)` → `Result`. `debug` 를 주면 위치 찾기 과정을 한 줄씩 넘긴다 |
| `GameExe` | `ReadVersion(exePath)` — exe 옆 `ffxivgame.ver` 의 게임 버전 (없거나, 읽지 못하거나, 경로가 잘못되었거나, 형식이 다르면 null) |

### 패턴 표기

- **pelite** (deucalion 표기): `48 8B ?`(`?` 하나 = 1바이트), `**`(1바이트), `[n]` / `[n-m]` 건너뛰기(n, m ≤ 16384, 짧은 쪽부터),
  `( a | b )` 대안, `'` 북마크, `$ { ' }`(rel32 를 따라간 주소 저장).
- **hex** (OverlayPlugin / cactbot SigScan 표기): 두 글자가 1바이트이고 `??` 가 와일드카드 1바이트다. 공백은 무시하고
  대소문자는 가리지 않는다. 길이가 홀수이거나 반쪽 와일드카드(`?A`)가 있으면 오류다.
- **주의**: hex 표기를 `Parse(text)`(pelite)에 그대로 넘기면 오류 없이 다르게 읽힌다 — `488B??05` 의 `??` 가 2바이트가 된다.
  hex 는 `Parse(text, SigSyntax.Hex)` 로.

### 값 해석

OverlayPlugin / cactbot SigScan 과 같은 규칙이다. 일치 시작을 A, 길이를 L 이라 할 때:

- `rip = true`: `[A+L+offset]` 의 int32 를 v 로 읽고 `A+L+offset+4+ripOffset+v` 를 낸다.
- `rip = false`: `[A+L+offset]` 의 int64 에서 `ImageBase` 를 뺀다.

계산은 RVA 로 한다. 결과가 어느 섹션에도 없으면 `Section` 이 null 이다. 맞긴 맞는데 엉뚱한 곳을 가리킬 때의 신호다.

### 스캔 범위와 제한

- 기본 `Text`: `.text` 섹션만 훑는다.
- `AllSections`: 파일에 데이터가 있는 모든 섹션을 섹션마다 따로 훑는다. 섹션 경계에 걸친 일치, PE 헤더, bss 는 보지 않는다.
- `Match.CrossesBoundary(0x10000)`: 일치 구간이 64KB 경계를 넘는지.
- `Match.MissedByChunkedScan(offset, rip)`: OverlayPlugin / cactbot 의 SigScan 이 이 일치를 놓치는지. 두 스캐너는 모듈을 64KB
  덩어리로 읽고, "일치 + max(0, offset) + 읽을 값(rip 4 / 아니면 8바이트)" 이 덩어리 안에 다 들어오는 일치만 본다. 모듈 기준
  주소가 64KB 정렬이라 RVA 로 계산해도 같다. (모듈 끝의 덜 찬 마지막 덩어리는 따로 계산하지 않는다.)
- `Parse` 가 거부하는 패턴: 고정 바이트가 없는 것, 0바이트에도 맞는 것(`[0]`, `(4C|)`), 고정 바이트를 하나도 거치지 않는
  대안 길이 있어 모든 곳에 맞는 것(`(48|?) ?` — 길이와 상관없이), 가변 건너뛰기를 겹쳐 폭의 곱이 16385 를 넘는 것
  (`[0-16384] [0-16384]`), 앞의 넓은 건너뛰기 때문에 짧은 시험에서도 너무 오래 걸리는 것.
- 스캔 시간: 일치 하나를 찾는 데 30초, 스캔 전체에 120초가 넘으면 `SignatureException` 으로 멈춘다.
  건너뛰기가 있는 패턴은 정규식을 컴파일해 쓴다(결과는 같고 열 배쯤 빠르다).

### 재생성 (`SigRegen`)

옛 exe 에서 맞고 새 exe 에서는 깨진 패턴을, 덮던 명령어들의 모양으로 새 exe 에서 찾아 다시 만든다. 모양은 레지스터와
주소성 값을 뺀 명령어다. 새 패턴은 새 exe 에서 고른 위치들에만, 옛 exe 의 일치와 같은 개수로 맞아야 하고, 아니면 실패로 보고한다.

- **call 형**(`E8 $ { ' }` 이 든 패턴): 옛 call 대상 함수에 대응하는 새 함수(지문이 같고 바이트가 가장 비슷한 것 — 동점이면
  대응하지 않음)가 새 패턴의 call 대상이어야 한다. 대응하는 함수를 못 찾으면 `CallTargetUnverified`. call 형은 대상으로
  검증하므로, 앞부분으로 줄인 모양으로도 찾고 뒤쪽이 바뀐 명령어도 쓴다.
- **call 형이 아닌 패턴**: 위치가 맞는지 확인할 방법이 없어 **위치는 추정이다**. 그래서 더 엄격하다 — 새 패턴의 명령어는 옛 패턴이
  덮던 명령어와 모두 같은 모양이어야 하고(줄인 모양으로는 찾지 않는다), 만든 패턴을 옛 exe 에서도 훑어 옛 일치와 겹치지 않는
  곳에 맞으면(패치 전부터 있던 다른 코드) 늘려 보고, 그래도 맞으면 실패로 보고한다. 진짜 위치의 모양이 바뀌어 사라졌을 때
  비슷한 다른 곳을 고르지 않게 하려는 것이다. 그래도 새로 생긴 다른 코드는 가려내지 못하므로 만든 패턴은 다시 스캔하고 게임에서
  값을 확인할 것.

`readOffset` 을 주면 그 자리가 어느 필드인지 기억했다가 새 패턴의 같은 필드로 새 offset 을 계산한다. 필드는 RIP 변위,
변위, rel32, 즉치 중 하나다. 새 명령어의 모양과 필드 크기가 같아야 하고, 주소성 필드면 가리키는 섹션도 같아야 한다.

입력 탓의 실패 — 패턴 형식 오류, 자동 복구가 지원하지 않는 문법(`$ { }` 밖의 북마크 `'`, 건너뛰기 `[n]`/`[n-m]`, 여러 바이트나
와일드카드가 든 대안), 스캔 시간 초과, 읽지 못하는 자리 — 는 예외 대신 `Pattern` 이 null 인 `Result` 로 알린다(`Detail` 이 그 문구).
call 대상이 파일 밖이라 대응시키지 못한 것은 실패가 아니라 미확인(`CallTargetUnverified`)이다. 예외로 끝나는 것은 null 인자
(`ArgumentNullException`)와 메모리 부족 같은 실행 환경의 문제뿐이다(그 밖의 예외는 이 라이브러리의 버그). (`CodeImage` 를 만들 때 exe 가 아니거나 `.pdata` 가 없으면
그 생성자가 `SignatureException` 을 던진다.)

`Result` 의 필드:

| 필드 | 뜻 |
|---|---|
| `Pattern` | 새 패턴 (pelite). 실패했거나 이미 맞으면 null |
| `Offset` | 새 offset |
| `RipOffset` | RIP 변위 뒤로 명령어 끝까지 남는 바이트 수 (rel32 — call/jmp/jcc 대상 — 는 0) |
| `ReadField` | 사람용 설명. 예: `rip disp of lea rcx,[...]` |
| `AlreadyMatches` | 옛 패턴이 새 exe 에서도 옛 exe 와 **같은 개수로** 맞음. call 형은 옛 대상에 대응하는 함수가 새 일치의 대상에 있어야 하고, `readOffset` 을 줬으면 읽는 필드의 모양도 같아야 한다. call 형이 아니면 같은 위치인지는 보지 않는다. (`Offset`/`RipOffset` 은 바뀌었을 수 있다) |
| `OldRipOffset` | 옛 명령어의 RipOffset — `RipOffset` 과 다르면 읽는 쪽의 ripOffset 도 바꿔야 함 |
| `CallTargetUnverified` | call 형인데 대응하는 함수를 못 찾아(또는 같은 함수가 여럿이라 고르지 못해), 일치 중 하나라도 call 대상이 같은지 확인하지 못함 (`Detail` 의 문구 대신 이것으로 판단) |
| `Detail` | 결과 설명 (사람용. 위치를 어떻게 찾았는지 — 전체 모양 묶음, 모양 n개, 문맥, call 대상, 함수 대응 — 도 붙는다) |

전역 변수 주소 자체가 두 exe 에서 같은 대상인지는 확인할 수 없다.

```csharp
using FFXIV.Signatures;

var target = new ScanTarget(@"C:\...\game\ffxiv_dx11.exe");
var sig = SigPattern.Parse("488B5720B8000000E0483BD00F84????????488D0D", SigSyntax.Hex);
foreach (var m in sig.Scan(target))
{
    var v = SigValue.Resolve(target.Image, m, offset: 0, rip: true);
    Console.WriteLine($"0x{m.Rva:x} -> 0x{v.ResolvedRva:x} {v.Section}");
}

var r = SigRegen.Regenerate(new CodeImage(oldExe), new CodeImage(newExe),
                            SigPattern.HexToPelite("803D??????????74??488B03488BCBFF50"), readOffset: -15);
if (r.Pattern != null) Console.WriteLine($"{SigPattern.PeliteToHex(r.Pattern)} offset {r.Offset} ripOffset {r.RipOffset}");
```

## 판 기록

- **1.2.0** — 재생성 메모리를 절반 아래로: `CodeImage` 가 명령어와 모양 문자열 대신 명령어 주소와 모양 해시만 남긴다(해시가 같으면
  다시 디코드해 문자열로 확인하므로 결과는 그대로). 옛·새 exe 한 쌍의 최대 작업 집합 1.1GB → 450MB 남짓, 첫 재생성도 더 빠름.
  공개 API 추가: `CodeImage(ScanTarget)`. 재생성 결과와 ApiVersion(1)은 그대로.
- **1.1.0** — 공개 API 는 그대로(ApiVersion 1), 동작이 보수적으로 바뀜: call 형이 아닌 패턴의 재생성에 옛 exe 교차 검사와
  모든 명령어 모양 일치를 요구(전에는 엉뚱한 곳을 성공으로 낼 수 있었음), 함수 대응이 동점이면 미확인, `Regenerate` 가 형식 오류도
  `Result` 로, 모든 곳에 맞는 패턴을 길이와 상관없이 거부, 가변 건너뛰기를 겹친 패턴 거부, 스캔 전체 시간 제한, RawSize 0 섹션의
  PointerToRawData 를 보지 않음, `GameExe.ReadVersion` 이 잘못된 경로에 null, null 인자는 `ArgumentNullException`.
- **1.0.0** — 처음.

## 시험

```
dotnet test FFXIV.Signatures.Tests
```

실제 deucalion 수정 이력으로 하는 백테스트는 게임 exe 가 필요하다. `<패치 파일 버전>.exe` 들이 있는 폴더를
`FFXIV_SIGNATURES_BACKTEST_DIR` 로 알려 줄 때만 돈다(`SigRegenBacktests.cs`).
