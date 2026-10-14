# "패치 필요" 종단 시험 — 패치 당일을 재현해 GUI 간편 모드의 확인 -> 빌드 -> 설치 -> 되돌리기를 끝까지 해 본다.
# 진짜 ACT 와 작업 폴더는 건드리지 않는다. (patchday-e2e.cmd 를 더블클릭, 또는 powershell -File 로)
#
# 방법:
#   - 시험용 작업 폴더의 베이스 플러그인 = 직전 공식 릴리스(-Previous, 기본: 최신의 바로 전). 최신 확인에서 교체되지
#     않게 어셈블리 버전 표시만 최신 번호로 바꾼다 (Mono.Cecil). 게임 위치는 사용자 설정(config.json)을 그대로 쓴다.
#   - 가짜 ACT 폴더에 직전 공식본을 "설치된 것"으로 두고, 그 위치를 적은 가짜 ACT 설정을 ACTREPACK_ACT_CONFIG 로 넘긴다.
#   - gui.json 의 작업 폴더를 잠시 시험용으로 바꾸고 GUI 를 띄운다. 창을 닫으면 gui.json 을 되돌리고 결과를 검사한다:
#     설치가 됐으면 패치본의 opcode 를 최신 공식본과 비교한다 (게임이 최신 공식본과 맞는 시점이면 같아야 함).
# 직전 공식본이 이미 지금 게임과 맞으면 "패치 필요 없음"이 정상 결과다 — 그때는 더 앞의 버전을 -Previous 로 준다.
param([string]$Previous)

$ErrorActionPreference = 'Stop'
$repo = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$gui = Join-Path $repo 'src\ActPluginRepack.Gui\bin\Release\net10.0-windows\ActPluginRepack.Gui.exe'
$cli = Join-Path $repo 'src\ActPluginRepack.Cli\bin\Release\net10.0\ActPluginRepack.exe'
$cecil = Join-Path $env:USERPROFILE '.nuget\packages\mono.cecil\0.11.5\lib\net40\Mono.Cecil.dll'
$base = Join-Path $env:TEMP 'ActPluginRepack-e2e'
$guiJson = Join-Path $env:LOCALAPPDATA 'ActPluginRepack\gui.json'
function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }

try {
    foreach ($f in $gui, $cli, $cecil) { if (-not (Test-Path $f)) { throw "없음: $f (먼저 Release 빌드)" } }
    if (Get-Process 'ActPluginRepack.Gui' -ErrorAction SilentlyContinue) { throw 'GUI 가 이미 켜져 있어요 - 닫고 다시 실행' }

    # 릴리스: 최신 태그는 웹 페이지 이동으로, 직전 태그는 API 목록으로 (토큰 없어도 1회)
    $repoName = 'ravahn/FFXIV_ACT_Plugin'
    $latest = (Invoke-WebRequest "https://github.com/$repoName/releases/latest" -UseBasicParsing).BaseResponse.ResponseUri.AbsolutePath.Split('/')[-1]
    if (-not $Previous) {
        $tags = (Invoke-RestMethod "https://api.github.com/repos/$repoName/releases?per_page=5").tag_name
        $Previous = $tags[[array]::IndexOf($tags, $latest) + 1]
    }
    Say "최신 공식본 $latest, 시험할 직전 공식본 $Previous" Cyan

    if (Test-Path $base) { Remove-Item $base -Recurse -Force }
    $dl = Join-Path $base 'dl'
    foreach ($tag in $Previous, $latest) {
        $zip = Join-Path $dl "$tag.zip"
        New-Item -ItemType Directory -Force $dl | Out-Null
        Invoke-WebRequest "https://github.com/$repoName/releases/download/$tag/FFXIV_ACT_Plugin_$tag.zip" -OutFile $zip -UseBasicParsing
        Expand-Archive $zip (Join-Path $dl $tag) -Force
    }
    $prev = Join-Path $dl $Previous

    # 시험용 작업 폴더
    $ws = Join-Path $base 'ws'
    [IO.Directory]::CreateDirectory((Join-Path $ws 'input\plugin')) | Out-Null
    Copy-Item (Join-Path $env:LOCALAPPDATA 'ActPluginRepack\config.json') (Join-Path $ws 'config.json')
    Add-Type -Path $cecil
    $asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $prev 'FFXIV_ACT_Plugin.dll'))
    $asm.Name.Version = [Version]$latest
    $asm.Write((Join-Path $ws 'input\plugin\FFXIV_ACT_Plugin.dll'))
    $asm.Dispose()
    Get-ChildItem $prev -Filter 'deucalion*' | Copy-Item -Destination (Join-Path $ws 'input\plugin')

    # 가짜 ACT
    $plugin = Join-Path $base 'act\Plugins\FFXIV_ACT_Plugin_KR'
    $overlay = Join-Path $base 'act\Plugins\OverlayPlugin'
    [IO.Directory]::CreateDirectory($plugin) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $overlay 'resources')) | Out-Null
    Copy-Item (Join-Path $prev '*') $plugin
    Set-Content (Join-Path $overlay 'OverlayPlugin.dll') 'dummy'
    Set-Content (Join-Path $overlay 'resources\opcodes.jsonc') '{ "original": true }'
    $cfg = Join-Path $base 'act\Advanced Combat Tracker.config.xml'
    @"
<?xml version="1.0" encoding="utf-8"?>
<Config><ActPlugins>
  <Plugin Enabled="True" Path="$plugin\FFXIV_ACT_Plugin.dll" />
  <Plugin Enabled="True" Path="$overlay\OverlayPlugin.dll" />
</ActPlugins></Config>
"@ | Set-Content $cfg -Encoding UTF8
    $original = (Get-FileHash (Join-Path $plugin 'FFXIV_ACT_Plugin.dll')).Hash

    # GUI 실행 (gui.json 은 끝나면 되돌린다)
    $backup = if (Test-Path $guiJson) { [IO.File]::ReadAllBytes($guiJson) } else { $null }   # 바이트 그대로 되돌린다
    try {
        @{ Advanced = $false; Workspace = $ws } | ConvertTo-Json | Set-Content $guiJson -Encoding UTF8
        $env:ACTREPACK_ACT_CONFIG = $cfg
        Say "`nGUI 를 띄워요. [확인하고 고치기] -> 결과 확인 -> (원하면 [설치 전으로 되돌리기]) -> 창 닫기" Green
        Start-Process $gui -Wait
    }
    finally {
        if ($null -ne $backup) { [IO.File]::WriteAllBytes($guiJson, $backup) } else { Remove-Item $guiJson -ErrorAction SilentlyContinue }
        Remove-Item Env:ACTREPACK_ACT_CONFIG -ErrorAction SilentlyContinue
    }

    # 결과
    $now = (Get-FileHash (Join-Path $plugin 'FFXIV_ACT_Plugin.dll')).Hash
    Say "`n== 결과" White
    if ($now -eq $original) { Say '가짜 ACT 의 플러그인: 원래 상태 (설치 안 했거나 되돌림)' }
    else {
        Say '가짜 ACT 의 플러그인: 패치본이 설치됨'
        & $cli export --root $ws --what opcodes --plugin (Join-Path $plugin 'FFXIV_ACT_Plugin.dll') --out (Join-Path $base 'cmp\patched') --force *> $null
        & $cli export --root $ws --what opcodes --plugin (Join-Path $dl "$latest\FFXIV_ACT_Plugin.dll") --out (Join-Path $base 'cmp\official') --force *> $null
        foreach ($f in Get-ChildItem (Join-Path $base 'cmp\official') -File) {
            $a = Get-Content $f.FullName | Where-Object { $_ -match '\S' }
            $p = Join-Path $base "cmp\patched\$($f.Name)"
            $b = if (Test-Path $p) { Get-Content $p | Where-Object { $_ -match '\S' } } else { @() }
            $same = -not (Compare-Object $a $b)
            Say ("  opcode {0,-24} 최신 공식본({1})과 {2}" -f $f.BaseName, $latest, $(if ($same) { '같음' } else { '다름 (설정 안 된 지역이면 정상일 수 있음)' })) $(if ($same) { 'Green' } else { 'Yellow' })
        }
    }
    $state = Join-Path $ws 'input\.state.json'
    if (Test-Path $state) { $s = Get-Content $state -Raw | ConvertFrom-Json; Say "판단: 패치 필요 $($s.PatchNeeded), 공식 후크 정상 $($s.HooksOkOfficially)" }
    Say "시험 폴더(지워도 됨): $base"
}
catch { Say "ERROR: $($_.Exception.Message)" Red }
finally { Read-Host "`n엔터를 누르면 창이 닫혀요" | Out-Null }
