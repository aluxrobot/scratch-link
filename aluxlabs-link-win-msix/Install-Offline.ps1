<#
.SYNOPSIS
    네트워크 없이(USB 등) AluxLabs Link 를 설치한다. .msixbundle 과 VCLibs 의존 appx 를
    로컬 파일로 함께 설치하므로 다운로드가 차단된 환경(학교 등)에서 동작한다.

.DESCRIPTION
    스크립트와 같은 폴더의 .msixbundle 1개와 Dependencies(또는 같은 폴더)의 VCLibs *.appx 를 찾아
    Add-AppxPackage -DependencyPath 로 한 번에 설치한다. .cer 가 있으면 신뢰 저장소에 먼저 등록한다.

.NOTES
    서명 인증서가 공개 신뢰(CA 발급) 가 아니면, 같은 폴더에 .cer 를 두고 관리자 권한으로 실행해야 한다.
#>
[CmdletBinding()]
param(
    [string]$Root = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'

$bundle = Get-ChildItem -Path $Root -Filter '*.msixbundle' -File | Select-Object -First 1
if (-not $bundle) { throw "이 폴더에 .msixbundle 이 없습니다: $Root" }

$deps = @(Get-ChildItem -Path $Root -Filter 'Microsoft.VCLibs.*.appx' -File -Recurse)
if ($deps.Count -eq 0) { throw "VCLibs 의존 appx 를 찾지 못했습니다 (Microsoft.VCLibs.*.appx)." }

$cer = Get-ChildItem -Path $Root -Filter '*.cer' -File | Select-Object -First 1
if ($cer) {
    Write-Host "[1/2] 서명 인증서 신뢰 등록: $($cer.Name)"
    Import-Certificate -FilePath $cer.FullName -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}

Write-Host "[2/2] 설치: $($bundle.Name)"
$deps | ForEach-Object { Write-Host "      + 의존성: $($_.Name)" }

Add-AppxPackage -Path $bundle.FullName -DependencyPath ($deps | ForEach-Object { $_.FullName })

Write-Host "설치 완료: $($bundle.Name)" -ForegroundColor Green
