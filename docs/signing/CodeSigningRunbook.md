# 코드 서명 → 배포 따라하기 런북 (초심자용)

> **이 문서는 위에서 아래로 순서대로 따라 하면 됩니다.** 코드 서명을 한 번도 안 해본 사람 기준으로 썼습니다.
> 환경 구성(최초 1회) → 빌드 → 서명 → 버킷 업로드까지 한 번에 다룹니다.
>
> "왜 이렇게 하는가"(아키텍처·설계 배경)는 [CodeSigningAndDistribution.md](CodeSigningAndDistribution.md)를 보세요. 이 문서는 **손으로 따라 하는 절차**에 집중합니다.

> ⚠️ **비밀 값(토큰 비밀번호, AWS 시크릿 키)은 이 문서나 코드에 적지 마세요.** 비밀번호 관리자에 두고, 터미널에서 직접 입력합니다.

---

## 0. 먼저 — 5분만 개념 잡기

처음이면 이 정도만 알고 시작하면 됩니다.

- **코드 서명이란?** 프로그램에 "이건 누가 만든 진짜다"라는 디지털 도장을 찍는 것. Windows가 이 도장을 보고 신뢰합니다.
- **왜 필요?** 우리 앱은 **MSIX** 형식인데, **MSIX는 서명이 없으면 설치 자체가 안 됩니다.** (일반 exe는 경고만 뜨지만 MSIX는 거부됨)
- **우리가 쓰는 도장:** Sectigo **OV** 코드사인 인증서. 그런데 이 인증서의 **개인키(도장의 핵심)가 USB 토큰 하드웨어 안에** 들어있습니다. 그래서:
  - 서명하려면 **USB 토큰을 PC에 꽂아야** 합니다.
  - 토큰을 읽으려면 **SafeNet**이라는 드라이버가 필요합니다.
  - 서명할 때마다 **토큰 비밀번호**를 입력합니다. (3회 틀리면 잠김! 주의)

**전체 그림:**

```
[최초 1회] 환경 구성: SafeNet 설치 + 토큰 인식 + signtool 확인 + AWS CLI 설정
                              │
[릴리스마다] 빌드 → 서명(토큰) → 스테이징 → 버킷 업로드(make) → URL 검증
```

---

# Part A. 인증 환경 수동 구성 (최초 1회만)

> 한 번 해두면 그 PC에선 다시 안 해도 됩니다. PC를 바꾸거나 새 사람이 맡으면 이 Part를 처음부터.

## A-1. 준비물 체크리스트

- [ ] **USB 코드사인 토큰** (Sectigo eToken, 실물)
- [ ] **토큰 비밀번호** (발급 시 받은 것, 또는 변경한 것)
- [ ] **Windows 11 PC** + Visual Studio 2022/2026 ([WindowsDevSetup-VS2026.md](../windows/WindowsDevSetup-VS2026.md) 참고)
- [ ] **Windows SDK** (signtool 포함 — VS 설치 시 보통 같이 깔림)
- [ ] **AWS 액세스 키** (S3 업로드용 — 없으면 A-5에서 관리자에게 요청)

## A-2. SafeNet Authentication Client 설치

토큰 안의 인증서를 Windows가 읽게 해주는 드라이버입니다. **이게 없으면 토큰을 꽂아도 인증서가 안 보입니다.**

1. 다운로드: <https://www.sectigo.com/knowledge-base/detail/SafeNet-Authentication-Client-Download-for-Sectigo-Certificates-on-eToken/kA03l000000o6kL>
2. 받은 zip 압축 풀기 → **`Msi` 폴더**로 들어가기 (다른 폴더 ADMX/Customization은 무시)
3. **`SafeNetAuthenticationClient-x64-...msi`** 더블클릭 (64비트 Windows)
4. 설치 유형: **`Typical`** 선택
   - > Typical을 골라야 **Microsoft Crypto Providers**가 깔려서 `signtool`이 토큰 키에 접근할 수 있습니다.
5. 설치 끝나면 **재부팅** (USB는 설치 중엔 빼두는 게 깔끔)

## A-3. 토큰 연결 + 인식 확인

1. **USB 토큰을 꽂습니다.**
2. 시작 메뉴 → **SafeNet Authentication Client Tools** 실행 → 토큰과 그 안의 인증서가 보이면 정상.
3. PowerShell로도 확인 (인증서가 Windows 저장소에 보이는지):

   ```powershell
   Get-ChildItem Cert:\CurrentUser\My | Where-Object {
     $_.Thumbprint -eq "EB74741683C9CDCE4457571A7EDD075A835B02C6"
   } | Format-List Subject, HasPrivateKey, NotAfter
   ```

   **예상 출력:**
   ```
   Subject       : CN="ALUX Co.,Ltd", O="ALUX Co.,Ltd", S=Seoul, C=KR
   HasPrivateKey : True
   NotAfter      : 2027-07-01 ...
   ```
   - `HasPrivateKey : True` 가 핵심 — 토큰이 제대로 연결됐다는 뜻.
   - **아무것도 안 나오면** → 토큰이 안 꽂혔거나 SafeNet 설치/재부팅이 안 된 것.

> 🔒 **토큰 비밀번호 3회 실패 시 잠깁니다.** 잠기면 Sectigo 지원으로만 해제됩니다(비용·시간 소요). 비밀번호 입력 창은 **복사·붙여넣기(Ctrl+V)** 되니, 외워서 치지 말고 비밀번호 관리자에서 붙여넣으세요.

## A-4. signtool 확인

서명 도구는 Windows SDK에 들어있는 `signtool.exe`입니다. 위치 찾기:

```powershell
Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" |
  Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
```

**예상 출력 (버전 숫자는 다를 수 있음):**
```
C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe
```

- 이 경로를 메모해두세요. 이후 `$st` 변수로 씁니다.
- **안 나오면** → Visual Studio Installer에서 "Windows SDK" 구성요소를 설치하세요.

## A-5. AWS CLI 설치 + 자격증명 (버킷 업로드용)

서명한 파일을 S3 버킷에 올리려면 AWS CLI가 필요합니다.

1. 설치:
   ```powershell
   winget install -e --id Amazon.AWSCLI --accept-package-agreements --accept-source-agreements
   ```
   설치 후 **새 터미널**을 엽니다. 설치 위치: `C:\Program Files\Amazon\AWSCLIV2\aws.exe`

2. 자격증명 등록 (**본인 터미널에서 직접** — 시크릿 키는 채팅/문서에 붙여넣지 말 것):
   ```powershell
   aws configure
   ```
   - AWS Access Key ID: (본인 키)
   - AWS Secret Access Key: (본인 시크릿)
   - Default region name: **`ap-northeast-2`** (서울)
   - Default output format: `json`

3. 동작 확인:
   ```powershell
   aws sts get-caller-identity
   aws s3 ls s3://dev-scratch-link.aluxcoding.com/
   ```
   둘 다 에러 없이 나오면 OK.

### 액세스 키가 없거나 권한이 막혀 있으면

S3를 웹 콘솔로만 써봤으면 CLI용 키가 없을 수 있습니다. IAM 대시보드도 "액세스 거부"로 막혀 있으면 **본인이 키를 못 만듭니다.** 관리자에게 아래를 요청하세요:

> "S3 배포용 IAM 액세스 키가 필요합니다. 아래 권한을 가진 IAM 사용자 + 액세스 키를 발급해 주세요."
> ```json
> {
>   "Version": "2012-10-17",
>   "Statement": [
>     { "Sid": "S3Upload", "Effect": "Allow",
>       "Action": ["s3:PutObject", "s3:DeleteObject", "s3:ListBucket"],
>       "Resource": [
>         "arn:aws:s3:::scratch-link.aluxcoding.com", "arn:aws:s3:::scratch-link.aluxcoding.com/*",
>         "arn:aws:s3:::dev-scratch-link.aluxcoding.com", "arn:aws:s3:::dev-scratch-link.aluxcoding.com/*"
>       ] },
>     { "Sid": "CloudFront", "Effect": "Allow",
>       "Action": ["cloudfront:CreateInvalidation", "cloudfront:GetInvalidation", "cloudfront:ListDistributions"],
>       "Resource": "*" }
>   ]
> }
> ```

(참고: 이 권한은 `scripts/aws/policies/iam-policy.json.tpl`의 CI용 정책과 동일합니다.)

**여기까지 하면 환경 구성 끝.** 이제 릴리스할 때마다 Part B만 반복합니다.

---

# Part B. 빌드 → 서명 → 업로드 (릴리스마다 반복)

> 경로·명령은 복사-붙여넣기 하면 됩니다. PowerShell에서 실행하세요.
> 아래 예시 버전은 `1.1.1.1039` — 실제 빌드한 버전으로 바뀝니다.

## B-1. 버전 확인 (그리고 필요하면 올리기)

버전은 `1.1.1.1039` 형태입니다:
- 앞 3자리(`1.1.1`) = `SharedProps/Version.props`의 `<ReleaseTriplet>` (수동 관리)
- 4번째(`1039`) = **git 커밋 수** (커밋할 때마다 자동 +1)

현재 버전 보기:
```powershell
make show-version
```

**버전을 올려야 할 때:**
- 코드만 새로 **커밋**해도 4번째가 올라갑니다 (예: 1039 → 1040). → 이게 가장 흔함.
- 기능 단위로 앞자리를 올리려면:
  ```powershell
  make release-patch    # 1.1.1 → 1.1.2
  make release-minor    # 1.1.1 → 1.2.0
  ```
  그 뒤 `Version.props`를 커밋해야 반영됩니다.

> ⚠️ **재빌드만으로는 버전이 안 바뀝니다.** 자동 업데이트 테스트가 되려면 새 버전이 **이전보다 높아야** 하므로, 보통 새 커밋을 한 뒤 빌드합니다.

## B-2. 빌드 (MSIX 번들 생성)

Developer PowerShell이 아니어도 됩니다. msbuild 절대경로로 실행:

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "aluxlabs-link-win-msix\aluxlabs-link-win-msix.wapproj" `
  -maxCpuCount -restore -t:Rebuild `
  -p:SolutionDir="$PWD\" `
  -p:Configuration=Release_Win `
  -p:AppxBundlePlatforms="x86|x64" `
  -p:AppxBundle=Always `
  -p:UapAppxPackageBuildMode=SideloadOnly
```

- `Rebuild` = 깨끗하게 처음부터 (몇 분 걸림). 버전은 `Version.props`에서 자동으로 들어갑니다.
- `x86|x64` = 32/64비트 둘 다 (arm64 제외). `SideloadOnly` = Store용 아닌 직접 배포용.
- `VS 2026`이 아니면 `Visual Studio\18\Community` 부분을 본인 버전/에디션에 맞게 바꾸세요.

빌드 성공하면 번들이 여기 생깁니다:
```
aluxlabs-link-win-msix\AppPackages\...\aluxlabs-link-win-msix_<버전>_x86_x64_Release_Win.msixbundle
```

## B-3. 서명 (USB 토큰)

> ⚠️ **토큰을 꽂은 상태**에서 실행하세요. 실행하면 **SafeNet 비밀번호 창이 팝업**됩니다 — 정확히 입력(붙여넣기 권장).

```powershell
$st = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"   # A-4에서 찾은 경로
$bundle = (Get-ChildItem "aluxlabs-link-win-msix\AppPackages\*\*.msixbundle" | Select-Object -First 1).FullName
& $st sign /fd SHA256 /sha1 EB74741683C9CDCE4457571A7EDD075A835B02C6 `
  /tr http://timestamp.sectigo.com /td SHA256 $bundle
```

명령 옵션 뜻:
- `/sha1 EB7474...` — **인증서를 지문으로 정확히 지정** (이름 `/n`보다 안정적 — `/n`은 가끔 "인증서 못 찾음"이 남)
- `/fd SHA256` — 해시 알고리즘
- `/tr ... /td SHA256` — **타임스탬프(필수)**. 이게 있어야 인증서 만료 후에도 서명이 유효하게 유지됨

**성공 메시지:** `Successfully signed: ...`

## B-4. 서명 검증

```powershell
& $st verify /pa $bundle
```
**성공:** `Successfully verified` + `Number of errors: 0`. 인증서 체인(Sectigo → CA → ALUX Co.,Ltd)과 타임스탬프가 보이면 정상.

## B-5. 스테이징 (업로드용 폴더에 정리)

번들 파일명이 길어서, 업로드 도구가 기대하는 이름(`AluxLabs-Link-<버전>.msixbundle`)으로 복사합니다. 버전은 자동 추출:

```powershell
$src = Get-ChildItem "aluxlabs-link-win-msix\AppPackages\*\*.msixbundle" | Select-Object -First 1
if ($src.Name -match '_(\d+\.\d+\.\d+\.\d+)_') { $ver = $matches[1] }
$stage = "aluxlabs-link-win-msix\dist\upload"
# 이전 번들이 남아있으면 지우기 (두 개면 업로드가 헷갈림)
Get-ChildItem "$stage\*.msixbundle" -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item $src.FullName "$stage\AluxLabs-Link-$ver.msixbundle"
"스테이징 완료: AluxLabs-Link-$ver.msixbundle"
```

> `dist/upload/`는 `.gitignore`에 들어있어 git에 안 올라갑니다 (번들이 142MB라 커밋 금지).

## B-6. 버킷 업로드 (make 한 줄)

`make`가 **`.appinstaller` 자동 생성 + 올바른 Content-Type 업로드 + CloudFront 무효화**까지 다 합니다.

```powershell
make sync-s3-dev    # 개발(dev) 버킷에 업로드
make sync-s3        # 운영(prod) 버킷에 업로드
```

- 보통 **dev에 먼저 올려 테스트** → 문제없으면 **prod**.
- 둘 다 올리면 양쪽 채널에 같은 서명 번들이 배포됩니다.

> 만약 무효화에서 `invalid invalidation paths` 에러가 나면 — Makefile에 `MSYS_NO_PATHCONV` 설정이 빠진 것. 이미 들어가 있으니 최신 Makefile이면 문제 없음.

## B-7. 검증 (URL이 새 버전을 서빙하는지)

```powershell
$r = Invoke-WebRequest -Uri "https://scratch-link.aluxcoding.com/AluxLabsLink.appinstaller" -UseBasicParsing
"Content-Type: " + (Invoke-WebRequest -Uri "https://scratch-link.aluxcoding.com/AluxLabsLink.appinstaller" -Method Head -UseBasicParsing).Headers['Content-Type']
([System.Text.Encoding]::UTF8.GetString($r.Content) | Select-String -Pattern 'Version="[^"]*"' -AllMatches).Matches.Value | Select-Object -Unique
```
- **`Content-Type: application/appinstaller`** + **올린 버전**이 보이면 성공.
- (dev 확인은 URL을 `dev-scratch-link.aluxcoding.com`으로 바꿔서)

**사용자 설치/업데이트 진입점:**
- 운영: `https://scratch-link.aluxcoding.com/AluxLabsLink.appinstaller`
- 개발: `https://dev-scratch-link.aluxcoding.com/AluxLabsLink.appinstaller`

기존 설치자는 앱을 재실행하면 이 URL을 확인해 **자동 업데이트**됩니다.

---

# Part C. 막혔을 때 — 트러블슈팅

| 증상 | 원인 | 해결 |
|---|---|---|
| 서명 시 `No certificates were found that met all the given criteria` | **토큰이 안 꽂혀 있음** (또는 SafeNet 미설치) | 토큰 연결 → A-3 인증서 확인 명령으로 보이는지 점검 후 재시도 |
| 서명 창에서 비밀번호가 계속 틀림 | 오타 (3회면 잠김!) | 비밀번호 관리자에서 **붙여넣기**. 이미 잠겼으면 Sectigo 지원 |
| `aws: Unable to locate credentials` | `aws configure` 안 함 | A-5 자격증명 등록 |
| `AccessDenied` (S3/CloudFront) | IAM 권한 부족 | A-5 하단 정책을 관리자에게 요청 |
| 버전이 계속 똑같음 | 새 커밋 없이 재빌드만 함 | 커밋하거나 `make release-patch` 후 빌드 (B-1) |
| 설치/업데이트가 안 되거나 URL이 텍스트로 열림 | Content-Type 잘못 (콘솔로 직접 올림) | `make sync-s3`로 올리면 자동 해결. 수동이면 `--content-type` 지정 |
| URL이 옛 버전을 보여줌 | CloudFront 캐시 | `make sync-s3`가 무효화까지 하므로 1~2분 대기. 안 되면 무효화 재실행 |
| MSIX 설치 거부 / "신뢰할 수 없음" | 서명이 안 됐거나 깨짐 | B-3 서명 + B-4 검증 다시 |

---

# Part D. 치트시트 (한 화면 요약)

**환경 구성(1회):** SafeNet 설치(Typical) → 재부팅 → 토큰 꽂기 → `Get-ChildItem Cert:\CurrentUser\My`로 확인 → `winget install Amazon.AWSCLI` → `aws configure`

**릴리스(매번):**
```powershell
# 1) 버전 확인 (필요시 커밋/올리기)
make show-version

# 2) 빌드
$msbuild = "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "aluxlabs-link-win-msix\aluxlabs-link-win-msix.wapproj" -maxCpuCount -restore -t:Rebuild -p:SolutionDir="$PWD\" -p:Configuration=Release_Win -p:AppxBundlePlatforms="x86|x64" -p:AppxBundle=Always -p:UapAppxPackageBuildMode=SideloadOnly

# 3) 서명 (토큰 꽂고, 비밀번호 입력)
$st = "C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
$bundle = (Get-ChildItem "aluxlabs-link-win-msix\AppPackages\*\*.msixbundle" | Select-Object -First 1).FullName
& $st sign /fd SHA256 /sha1 EB74741683C9CDCE4457571A7EDD075A835B02C6 /tr http://timestamp.sectigo.com /td SHA256 $bundle
& $st verify /pa $bundle

# 4) 스테이징
$src = Get-ChildItem "aluxlabs-link-win-msix\AppPackages\*\*.msixbundle" | Select-Object -First 1
if ($src.Name -match '_(\d+\.\d+\.\d+\.\d+)_') { $ver = $matches[1] }
$stage = "aluxlabs-link-win-msix\dist\upload"
Get-ChildItem "$stage\*.msixbundle" -ErrorAction SilentlyContinue | Remove-Item -Force
Copy-Item $src.FullName "$stage\AluxLabs-Link-$ver.msixbundle"

# 5) 업로드 (dev 먼저, 그다음 prod)
make sync-s3-dev
make sync-s3
```

---

## 참고

- 설계 배경·인증서 상세·배포 트랙 분리: [CodeSigningAndDistribution.md](CodeSigningAndDistribution.md)
- Windows 개발 환경 세팅: [WindowsDevSetup-VS2026.md](../windows/WindowsDevSetup-VS2026.md)
- 인증서: Sectigo OV / Subject `CN="ALUX Co.,Ltd", O="ALUX Co.,Ltd", S=Seoul, C=KR` / 지문 `EB74741683C9CDCE4457571A7EDD075A835B02C6` / 만료 **2027-07-01**
