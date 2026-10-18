# tests\manual 의 시험 스크립트가 같이 쓰는 경로와 도우미. 시험 스크립트 맨 앞에서 dot-source 한다:
#   . (Join-Path $PSScriptRoot 'common.ps1')
# 경로는 build.ps1 과 같은 Release 빌드 결과 (build.ps1 은 이 파일을 쓰지 않는다 — 바꾸면 둘 다).

$Repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$CliProject = Join-Path $Repo 'src\ActPluginRepack.Cli'
$GuiProject = Join-Path $Repo 'src\ActPluginRepack.Gui'
$CliOut = Join-Path $CliProject 'bin\Release\net10.0'
$Cli = Join-Path $CliOut 'ActPluginRepack.exe'
$Gui = Join-Path $GuiProject 'bin\Release\net10.0-windows\ActPluginRepack.Gui.exe'

# Release 로 빌드한다 (기본: CLI). 실패하면 빌드 기록을 보이고 멈춘다.
function Build-Release([string[]]$Projects = @($CliProject)) {
    foreach ($p in $Projects) {
        Write-Host "빌드하는 중: $(Split-Path $p -Leaf) (Release)" -ForegroundColor DarkGray
        $log = dotnet build $p -c Release --nologo -v q 2>&1
        if ($LASTEXITCODE -ne 0) { $log | Write-Host; throw "$(Split-Path $p -Leaf) 를 빌드하지 못했습니다 (종료 코드 $LASTEXITCODE)." }
    }
}

# CLI 를 실행한다. 종료 코드가 0 이 아니면 멈춘다 (-NoCheck 면 확인하지 않음 — 그 값은 $LASTEXITCODE 로 본다).
# -Quiet 는 출력을 버린다. 예: Invoke-Cli @('export', '--root', $ws, '--what', 'opcodes') -Quiet
function Invoke-Cli([string[]]$Arguments, [switch]$Quiet, [switch]$NoCheck) {
    if ($Quiet) { & $Cli @Arguments *> $null } else { & $Cli @Arguments }
    if (-not $NoCheck -and $LASTEXITCODE -ne 0) { throw "CLI $($Arguments[0]) 이(가) 실패했습니다 (종료 코드 $LASTEXITCODE)." }
}
