<#
.SYNOPSIS
  한 번에 임시 플러그인을 만든다: input\ 의 재료로 output\FFXIV_ACT_Plugin.dll 생성.

.DESCRIPTION
  src\ 의 ActPluginRepack CLI 를 빌드한 뒤 `build` 명령을 실행하는 얇은 런처.
  실제 로직은 전부 C#(src\ActPluginRepack.Core)에 있다. 단계별 실행이나 상태 확인은 CLI 를 직접 쓴다
  (docs\CLI.md 참고):

    dotnet src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.dll <status|prepare|build|install|...> [options]

.PARAMETER Plugin
  원본 플러그인 경로 override. 기본 input\plugin\FFXIV_ACT_Plugin.dll.

.PARAMETER NoCsv
  CSV -> data 재생성을 건너뛴다(직전 생성 결과를 그대로 재사용).

.PARAMETER Prepare
  빌드 전에 `prepare` 를 먼저 돌린다: 베이스 플러그인을 최신 공식 릴리스로 맞추고, 시그니처를 점검·복구하고,
  config.json 의 게임 클라이언트로 opcode·CSV 를 수집한다. 깨진 시그니처가 있거나 opcode 를 아직 못 구했으면
  빌드하지 않고 멈춘다. 공식 플러그인이 이미 맞으면 빌드하지 않는다(-Install 이면 공식 최신본을 설치),
  패치가 필요한지 판단할 수 없으면 멈춘다.

.PARAMETER Install
  빌드 뒤 `install` 로 ACT 의 FFXIV 플러그인 폴더에 설치한다(원래 파일은 backups\ 에). ACT 와 게임을 끄고 실행.
  관리자 권한이 필요하면(ACT 가 Program Files 에 있으면) 설치 단계만 UAC 확인 창으로 올린다. 되돌리기는 CLI 의 `restore`.

.EXAMPLE
  .\build.ps1

.EXAMPLE
  .\build.ps1 -Prepare -Install
#>
param(
    [string]$Plugin,
    [switch]$NoCsv,
    [switch]$Prepare,
    [switch]$Install
)
$ErrorActionPreference = 'Stop'

# CLI 종료 코드 — 값은 src\ActPluginRepack.Core\Infrastructure\ExitCodes.cs 와 같다 (docs\CLI.md 의 표)
$ExitCodes = @{
    Ok          = 0
    HooksBroken = 2   # prepare: 패킷 후크가 깨졌거나 시그니처 점검 자체가 실패함
    Incomplete  = 3   # prepare: opcode 를 못 구한 클라이언트가 있음
    NotNeeded   = 5   # prepare: 공식 플러그인이 이미 지금 게임과 맞음
    Undecided   = 6   # prepare: 패치가 필요한지 판단하지 못함
    NewerPlugin = 7   # install: ACT 에 더 새 플러그인이 있어 그대로 둠
}

# CLI 프로젝트와 Release 빌드 결과 (docs\CLI.md 의 예와 같은 위치)
$cliProject = Join-Path $PSScriptRoot 'src\ActPluginRepack.Cli'
$cliOut = Join-Path $cliProject 'bin\Release\net10.0'
$dll = Join-Path $cliOut 'ActPluginRepack.dll'

$buildLog = dotnet build $cliProject -c Release --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { $buildLog | Write-Host; throw "도구를 빌드하지 못했습니다 (종료 코드 $LASTEXITCODE)." }

if ($Prepare) {
    $prepArgs = @('prepare', '--root', $PSScriptRoot)
    if ($Plugin) { $prepArgs += @('--plugin', $Plugin) }
    dotnet $dll @prepArgs
    if ($LASTEXITCODE -eq $ExitCodes.HooksBroken) { throw "패킷 후크 시그니처가 깨졌거나 시그니처 점검 자체가 실패했습니다(게임 exe 가 업데이트 중이었던 경우 등). 위 기록을 보고, 필요하면 input\signatures\deucalion.txt 를 고친 뒤 다시 실행하세요." }
    if ($LASTEXITCODE -eq $ExitCodes.Incomplete) { throw "준비가 끝나지 않았습니다(opcode 가 아직 공개되지 않았습니다). 나중에 다시 실행하세요." }
    if ($LASTEXITCODE -eq $ExitCodes.NotNeeded) {
        # 공식 플러그인이 이미 지금 게임과 맞다 — 패치본을 만들지 않는다 (-Install 이면 공식 최신본을 설치)
        Write-Host "패치가 필요 없습니다. 공식 플러그인이 이미 지금 게임과 맞습니다."
        if ($Install -and $Plugin) {
            # -Plugin 으로 판단했으니 input\plugin 의 공식본이 그 플러그인과 같다는 보장이 없다 — 직접 설치하게 둔다
            Write-Host "-Plugin 을 줬으므로 공식 플러그인을 자동으로 설치하지 않습니다. 필요하면 직접 설치하세요."
        } elseif ($Install) {
            dotnet $dll install --root $PSScriptRoot --official
            # ACT 에 더 새 플러그인이 있어 그대로 둠 (해루봇의 한국 임시본 등) — 실패가 아니다
            if ($LASTEXITCODE -eq $ExitCodes.NewerPlugin) { Write-Host "ACT 에 더 새 플러그인이 있어 그대로 둡니다." }
            elseif ($LASTEXITCODE -ne $ExitCodes.Ok) { throw "공식 플러그인을 설치하지 못했습니다 (install --official, 종료 코드 $LASTEXITCODE)." }
        }
        # exit 0 은 dot-source(. .\build.ps1)로 부르면 PowerShell 창까지 닫는다 — 종료 코드만 0 으로 두고 돌아간다
        $global:LASTEXITCODE = $ExitCodes.Ok
        return
    }
    if ($LASTEXITCODE -eq $ExitCodes.Undecided) { throw "패치가 필요한지 판단하지 못했습니다. 위 기록을 보고 나중에 다시 실행하세요." }
    if ($LASTEXITCODE -ne $ExitCodes.Ok) { throw "prepare 에 실패했습니다 (종료 코드 $LASTEXITCODE)." }
}

$cliArgs = @('build', '--root', $PSScriptRoot)
if ($Plugin) { $cliArgs += @('--plugin', $Plugin) }
if ($NoCsv)  { $cliArgs += '--no-csv' }
dotnet $dll @cliArgs
if ($LASTEXITCODE -ne $ExitCodes.Ok) { throw "build 에 실패했습니다 (종료 코드 $LASTEXITCODE)." }

if ($Install) {
    dotnet $dll install --root $PSScriptRoot
    if ($LASTEXITCODE -eq $ExitCodes.NewerPlugin) {
        Write-Host "ACT 에 이 패치본의 베이스보다 새 플러그인이 있어 그대로 둡니다. 공식 플러그인이 갱신된 뒤 다시 실행하세요."
        $global:LASTEXITCODE = $ExitCodes.Ok
    }
    elseif ($LASTEXITCODE -ne $ExitCodes.Ok) { throw "install 에 실패했습니다 (종료 코드 $LASTEXITCODE)." }
}
