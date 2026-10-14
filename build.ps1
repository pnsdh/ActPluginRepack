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
  빌드하지 않고 멈춘다.

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

$buildLog = dotnet build (Join-Path $PSScriptRoot 'src\ActPluginRepack.Cli') -c Release --nologo -v q 2>&1
if ($LASTEXITCODE -ne 0) { $buildLog | Write-Host; throw "tool build failed (exit $LASTEXITCODE)" }

$dll = Join-Path $PSScriptRoot 'src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.dll'

if ($Prepare) {
    $prepArgs = @('prepare', '--root', $PSScriptRoot)
    if ($Plugin) { $prepArgs += @('--plugin', $Plugin) }
    dotnet $dll @prepArgs
    if ($LASTEXITCODE -eq 2) { throw "broken signatures - fix input\signatures\deucalion.txt, then run again" }
    if ($LASTEXITCODE -eq 3) { throw "prepare incomplete (opcodes not available yet) - run again later" }
    if ($LASTEXITCODE -ne 0) { throw "prepare failed (exit $LASTEXITCODE)" }
}

$cliArgs = @('build', '--root', $PSScriptRoot)
if ($Plugin) { $cliArgs += @('--plugin', $Plugin) }
if ($NoCsv)  { $cliArgs += '--no-csv' }
dotnet $dll @cliArgs
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }

if ($Install) {
    dotnet $dll install --root $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw "install failed (exit $LASTEXITCODE)" }
}
