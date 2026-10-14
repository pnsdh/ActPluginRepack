# 배포용 zip 만들기: dist\ActPluginRepack-<버전>-win-x64.zip
#
#   ActPluginRepack-<버전>\
#     ActPluginRepack.Gui.exe   실행할 파일 (단일 파일, .NET 10 데스크톱 런타임 필요)
#     사용법.txt
#     ida\                      IDA 직접 추출용 래퍼 (GUI 가 같은 폴더에서 찾음)
#     cli\ActPluginRepack.exe   명령줄 도구 + GUI 가 관리자 권한 설치에 쓴다
#     licenses\                 내장 글꼴 라이선스 (SIL OFL)
#
# 버전은 Directory.Build.props. 사용법: .\publish.ps1  (zip 만들지 않으려면 -NoZip, 시험용으로 버전을 바꾸려면 -Version 2.0.1)
# zip 옆에 <zip>.sha256 과 <zip>.sig(서명)도 만든다 — 자동 업데이트가 이걸로 받은 파일을 확인하므로 릴리스에 셋 다 올린다.
param([switch]$NoZip, [string]$Version)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = if ($Version) { $Version } else { $props.Project.PropertyGroup.Version }
$name = "ActPluginRepack-$version"
$dist = Join-Path $root 'dist'
$out = Join-Path $dist $name
$stage = Join-Path $dist '.stage'
foreach ($d in $out, $stage) { if (Test-Path $d) { Remove-Item $d -Recurse -Force } }

$common = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', '-p:PublishSingleFile=true', '-p:DebugType=none',
            '-p:GenerateDocumentationFile=false', "-p:Version=$version", '--nologo', '-v', 'q')
Write-Host "[publish] GUI / CLI $version"
dotnet publish (Join-Path $root 'src\ActPluginRepack.Gui') @common -o (Join-Path $stage 'gui')
if ($LASTEXITCODE -ne 0) { throw 'GUI publish 실패' }
dotnet publish (Join-Path $root 'src\ActPluginRepack.Cli') @common -o (Join-Path $stage 'cli')
if ($LASTEXITCODE -ne 0) { throw 'CLI publish 실패' }

New-Item -ItemType Directory -Force $out, (Join-Path $out 'cli'), (Join-Path $out 'licenses') | Out-Null
Copy-Item (Join-Path $stage 'gui\ActPluginRepack.Gui.exe') $out
Copy-Item (Join-Path $stage 'gui\ida') $out -Recurse
Copy-Item (Join-Path $stage 'cli\ActPluginRepack.exe') (Join-Path $out 'cli')
Copy-Item (Join-Path $root 'src\ActPluginRepack.Gui\Fonts\*-OFL.txt') (Join-Path $out 'licenses')
Copy-Item (Join-Path $root 'packaging\사용법.txt') $out
Remove-Item $stage -Recurse -Force

# 빠진 것 / 남은 것 점검 — GUI publish 가 참조한 CLI 를 반쪽(dll 없는 exe)으로 끼워 넣는 일이 없게 필요한 것만 골라 담았다
$expected = 'ActPluginRepack.Gui.exe', '사용법.txt', 'ida', 'cli', 'licenses'
$actual = Get-ChildItem $out | ForEach-Object Name
if (Compare-Object $expected $actual) { throw "배포 폴더 구성이 예상과 다름: $($actual -join ', ')" }
foreach ($d in 'ida', 'cli', 'licenses') { if (-not (Get-ChildItem (Join-Path $out $d) -File)) { throw "$d\ 가 비어 있음" } }
& (Join-Path $out 'cli\ActPluginRepack.exe') --version | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'cli\ActPluginRepack.exe 가 실행되지 않음' }

Get-ChildItem $out -Recurse -File | ForEach-Object { '  {0,-40} {1,10:N0}' -f $_.FullName.Substring($out.Length + 1), $_.Length }
if ($NoZip) { return }
$zip = Join-Path $dist "$name-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $out -DestinationPath $zip
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$zip.sha256", "$hash  $(Split-Path $zip -Leaf)`n", (New-Object System.Text.UTF8Encoding $false))
# 서명 (자동 업데이트가 내장 공개키로 확인한다 — 키는 %USERPROFILE%\.actrepack\release-signing-key.pem, 저장소 밖)
& (Join-Path $out 'cli\ActPluginRepack.exe') release-sign --zip $zip
if ($LASTEXITCODE -ne 0) { throw '서명 실패 - 서명 키를 확인 (처음이면 cli\ActPluginRepack.exe release-keygen)' }
Write-Host "[publish] $zip  ($('{0:N0}' -f (Get-Item $zip).Length) bytes)"
Write-Host "          SHA256 $hash  (-> .sha256, .sig — 릴리스에 zip 과 함께 셋 다 올릴 것)"
Write-Host "          릴리스 태그는 v$version (업데이트 확인이 v 태그만 본다). 패치된 플러그인은 pnsdh/FFXIV_ACT_Plugin_HotFix 로"
