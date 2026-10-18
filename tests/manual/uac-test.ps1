# 관리자 권한(UAC) 확인 창 수동 시험 — 일반 권한으로 실행할 것 (uac-test.cmd 를 더블클릭)
#
# Program Files 처럼 보호된 가짜 ACT 폴더를 임시 폴더에 만들고, 실제 CLI 로
#   1) install  -> 확인 창에서 '예'     : 관리자로 올라가 설치되고, 그 기록이 이 창에 이어서 보여야 한다
#   2) restore  -> 확인 창에서 '아니요' : "취소됨" 안내가 나오고 아무것도 바뀌지 않아야 한다
# 를 해 본다. 진짜 ACT 와 작업 폴더는 건드리지 않는다. 결과는 단계마다 바로 result 파일에 쓴다.
#
# 주의: 관리자 쪽이 쓴 파일은 소유자가 Administrators 라 일반 권한으로는 그 파일의 권한을 못 바꾼다. 정리는 폴더(내가
# 만든 것)의 권한만 되돌려 지우고, 실패해도 멈추지 않는다 (예전에는 여기서 오류로 창이 꺼졌다).

$ErrorActionPreference = 'Continue'
. (Join-Path $PSScriptRoot 'common.ps1')
$base = Join-Path $env:TEMP 'ActPluginRepack-uac-test'
$result = Join-Path $env:TEMP 'ActPluginRepack-uac-test-result.txt'
function Say($text, $color = 'Gray') { Write-Host $text -ForegroundColor $color }
function Record($text) { Add-Content -Path $result -Value $text -Encoding UTF8; Say $text 'Cyan' }

# 내가 만든 폴더들의 권한을 되돌리고 지운다 (관리자가 만든 파일은 부모 폴더 권한으로 지울 수 있다)
function Cleanup {
    if (-not (Test-Path $base)) { return }
    Get-ChildItem $base -Recurse -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        & icacls $_.FullName /grant "$($env:USERNAME):(OI)(CI)F" /Q *> $null
    }
    & icacls $base /grant "$($env:USERNAME):(OI)(CI)F" /Q *> $null
    # Remove-Item -Force 는 지우기 전에 파일 속성을 바꾸려다(관리자 소유 파일이라) 거부된다 - rd 는 부모 폴더 권한으로 지운다
    & cmd /c rd /s /q $base *> $null
    if (Test-Path $base) { Say "임시 폴더 일부가 남았습니다 (해롭지 않으니 지워도 됩니다): $base" Yellow }
}

Set-Content -Path $result -Value "UAC 시험 $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -Encoding UTF8
try {
    # 0) 준비
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    if ((New-Object Security.Principal.WindowsPrincipal($id)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        Say '이 창은 관리자 권한으로 실행됐습니다. 확인 창이 뜨지 않으므로 시험할 수 없습니다.' Yellow
        Say '탐색기에서 uac-test.cmd 를 그냥 더블클릭하세요 (관리자로 실행 X).' Yellow
        Record 'SKIP: 관리자 권한으로 실행됨'
        return
    }
    Build-Release
    if (-not (Test-Path $Cli)) { Record "ERROR: 빌드했는데 CLI 가 없습니다: $Cli"; return }
    # 관리자 계정에 "확인 없이 올리기"(ConsentPromptBehaviorAdmin=0)면 확인 창이 안 뜬다 — 1단계는 그대로 시험되지만 2단계는 불가
    $noPrompt = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' -ErrorAction SilentlyContinue).ConsentPromptBehaviorAdmin -eq 0
    if ($noPrompt) {
        Say '참고: 이 PC 는 관리자 계정에 확인 창 없이 관리자 권한을 줍니다 (UAC "알리지 않음").' Yellow
        Say '      확인 창이 뜨지 않고 자동으로 진행됩니다. 2단계("아니요")는 시험할 수 없습니다.' Yellow
        Record '참고: ConsentPromptBehaviorAdmin=0 - 확인 창 없음, 2단계는 결과 무시'
    }

    Cleanup
    $ws = Join-Path $base 'work folder'
    $act = Join-Path $base 'Program Files (x86)\Advanced Combat Tracker'
    $plugin = Join-Path $act 'Plugins\FFXIV_ACT_Plugin_KR'
    $overlay = Join-Path $act 'Plugins\OverlayPlugin\resources'
    New-Item -ItemType Directory -Force $plugin, $overlay, (Join-Path $ws 'output'), (Join-Path $ws 'extras\OverlayPlugin') | Out-Null
    Set-Content (Join-Path $plugin 'FFXIV_ACT_Plugin.dll') 'official'
    Set-Content (Join-Path $overlay 'opcodes.jsonc') 'official opcodes'
    Set-Content (Join-Path $ws 'output\FFXIV_ACT_Plugin.dll') 'patched'
    Set-Content (Join-Path $ws 'extras\OverlayPlugin\opcodes.jsonc') 'patched opcodes'
    # Program Files 와 같은 권한: 관리자/SYSTEM 전체, Users 는 읽기만
    & icacls $act /inheritance:r /grant:r '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' /Q *> $null
    $probe = Join-Path $plugin 'probe.txt'
    $writable = $true
    try { [IO.File]::WriteAllText($probe, 'x'); Remove-Item $probe } catch { $writable = $false }
    if ($writable) { Record 'ERROR: 가짜 ACT 폴더가 보호되지 않았습니다 (일반 권한으로도 쓰기가 됩니다)'; return }
    Say "가짜 ACT 폴더 준비 완료 (일반 권한으로는 쓰기 불가): $act`n"

    # 1) 설치 -> '예'
    Say '==================================================================' White
    Say ' 1단계: 설치  -  곧 Windows 확인 창이 뜨면  [ 예 ]  를 누르세요' Green
    Say '==================================================================' White
    Invoke-Cli @('install', '--root', $ws, '--target', $plugin, '--overlay', $overlay) -NoCheck   # 결과는 아래에서 판정
    $code1 = $LASTEXITCODE
    $installed = ((Get-Content (Join-Path $plugin 'FFXIV_ACT_Plugin.dll')) -eq 'patched') -and ((Get-Content (Join-Path $overlay 'opcodes.jsonc')) -eq 'patched opcodes')
    Record ("1단계 설치(예): 종료 코드 $code1, 파일 바뀜 $installed -> " + $(if ($code1 -eq 0 -and $installed) { 'PASS' } else { 'FAIL' }))
    Say ''

    # 2) 되돌리기 -> '아니요'
    Say '==================================================================' White
    Say ' 2단계: 되돌리기  -  이번에는 확인 창에서  [ 아니요 ]  를 누르세요' Yellow
    Say '==================================================================' White
    Invoke-Cli @('restore', '--root', $ws) -NoCheck   # '아니요'면 실패가 정상
    $code2 = $LASTEXITCODE
    $unchanged = (Get-Content (Join-Path $plugin 'FFXIV_ACT_Plugin.dll')) -eq 'patched'
    Record ("2단계 되돌리기(아니요): 종료 코드 $code2, 그대로 $unchanged -> " + $(if ($code2 -ne 0 -and $unchanged) { 'PASS' } else { 'FAIL (아니요를 눌렀다면)' }))
}
catch {
    Record "ERROR: $($_.Exception.Message)"
}
finally {
    try { Cleanup } catch { Say "정리 중 오류 (무시): $($_.Exception.Message)" Yellow }
    Say "`n결과 파일: $result" White
    Get-Content $result -Encoding UTF8 | ForEach-Object { Say $_ White }
    Read-Host "`n엔터를 누르면 창이 닫힙니다" | Out-Null
}
