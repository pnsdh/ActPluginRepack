<#
.SYNOPSIS
  한 번에 임시 플러그인을 만든다: input\ 의 재료로 output\FFXIV_ACT_Plugin.dll 생성.

.DESCRIPTION
  tool\ 의 ActPluginRepack CLI 를 빌드한 뒤 `build` 서브커맨드를 실행하는 얇은 런처.
  실제 로직(CSV 변환 / 리팩 / 부속 파일 복사)은 전부 C# 쪽에 있다.
  단계별 실행이 필요하면 CLI 를 직접 쓴다 (README.md 참고):

    dotnet tool\bin\Release\net10.0\ActPluginRepack.dll <build|repack|csv|export> [options]

.PARAMETER Plugin
  원본 플러그인 경로 override. 기본 input\plugin\FFXIV_ACT_Plugin.dll.

.PARAMETER NoCsv
  CSV -> data 재생성을 건너뛴다(직전 생성 결과를 그대로 재사용).

.EXAMPLE
  .\build.ps1
#>
param(
    [string]$Plugin,
    [switch]$NoCsv
)
$ErrorActionPreference = 'Stop'

dotnet build (Join-Path $PSScriptRoot 'tool') -c Release --nologo -v q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "tool build failed (exit $LASTEXITCODE)" }

$dll = Join-Path $PSScriptRoot 'tool\bin\Release\net10.0\ActPluginRepack.dll'
$cliArgs = @('build', '--root', $PSScriptRoot)
if ($Plugin) { $cliArgs += @('--plugin', $Plugin) }
if ($NoCsv)  { $cliArgs += '--no-csv' }
dotnet $dll @cliArgs
if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }
