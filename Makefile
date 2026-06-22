# This Makefile generates icon and tile images from the sources in `Assets/`.
# This doesn't need to be run every time: just when there's a significant change to the source assets or
# if we need different icons or tiles.

# I recommend running "make" with the "-j" parameter to parallelize these jobs.
# On my computer, a full run takes ~45 sec with "-j" or ~3.5 minutes without.

# Requirements:
# - cairosvg
# - convert (from ImageMagick)
# - optipng

MAC_IMAGES = \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-16.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-16@2x.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-32.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-32@2x.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-128.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-128@2x.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-256.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-256@2x.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-512.png \
	aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-512@2x.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_16x16.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_16x16@2x.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_32x32.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_32x32@2x.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_128x128.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_128x128@2x.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_256x256.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_256x256@2x.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_512x512.png \
	aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_512x512@2x.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-48.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-64.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-96.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-128.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-256.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-512.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-16.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-19.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-32.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-38.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-48.png \
	AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-72.png

WINDOWS_IMAGES = \
	aluxlabs-link-win/aluxlabs-link.ico \
	aluxlabs-link-win/aluxlabs-link-tray.ico \
	aluxlabs-link-win-msix/Images/LockScreenLogo.scale-200.png \
	aluxlabs-link-win-msix/Images/SplashScreen.scale-200.png \
	aluxlabs-link-win-msix/Images/Square150x150Logo.scale-200.png \
	aluxlabs-link-win-msix/Images/Square44x44Logo.scale-200.png \
	aluxlabs-link-win-msix/Images/Square44x44Logo.targetsize-24_altform-unplated.png \
	aluxlabs-link-win-msix/Images/StoreLogo.png \
	aluxlabs-link-win-msix/Images/Wide310x150Logo.scale-200.png

.PHONY: all clean mac windows sync-s3 sync-s3-dev appinstaller stage-deps offline-pack show-version set-version release-patch release-minor

# S3 배포: dist/upload/의 서명된 번들 + .appinstaller를 scratch-link 버킷에 업로드.
# 운영: make sync-s3      → https://scratch-link.aluxcoding.com/
# 개발: make sync-s3-dev  → https://dev-scratch-link.aluxcoding.com/
# 파일별 Content-Type 지정 + CloudFront 무효화까지 수행. (aws s3 sync는 MIME가 깨져 금지)
# aws.exe를 PATH에서 못 찾으면 기본 설치 경로(8.3 단축명, 공백 회피)로 폴백.
# git-bash/MSYS sh가 CloudFront 경로 인자(/foo)를 Windows 경로로 변환하는 것을 방지 (무효화 실패 원인).
export MSYS_NO_PATHCONV := 1
AWS              ?= $(if $(wildcard C:/PROGRA~1/Amazon/AWSCLIV2/aws.exe),C:/PROGRA~1/Amazon/AWSCLIV2/aws.exe,aws)
S3_SRC           ?= aluxlabs-link-win-msix/dist/upload/
S3_BUNDLE        ?= $(notdir $(wildcard $(S3_SRC)*.msixbundle))
APPINSTALLER_DEV ?= aluxlabs-link-win-msix/dist/upload/AluxLabsLink.dev.appinstaller
S3_BUCKET        ?= scratch-link.aluxcoding.com
S3_BUCKET_DEV    ?= dev-scratch-link.aluxcoding.com
CF_DIST_ID       ?= E3HEXR4KAZLITV
CF_DIST_ID_DEV   ?= E1WMSQXPP9L5YF
CT_APPINSTALLER  ?= application/appinstaller
CT_MSIXBUNDLE    ?= application/vnd.ms-appx
AWS_REGION       ?= ap-northeast-2

# VCLibs 프레임워크 의존 패키지: .appinstaller <Dependencies>가 참조. 새 PC엔 없으므로 서버에 함께 호스팅 필수.
# 불변(Microsoft 서명·고정 버전)이라 레포에 고정 체크인. stage-deps 가 dist/upload/ 로 복사.
# 버전 갱신 시: Release 패키지의 AppPackages/*/Dependencies/{x64,x86}/ 에서 이 폴더로 덮어쓰고 템플릿 Version 갱신.
VCLIBS           ?= Microsoft.VCLibs.x64.14.00.appx Microsoft.VCLibs.x64.14.00.Desktop.appx Microsoft.VCLibs.x86.14.00.appx Microsoft.VCLibs.x86.14.00.Desktop.appx
VCLIBS_SRC       ?= aluxlabs-link-win-msix/Dependencies

# 오프라인/USB 설치 패키지 (make offline-pack). 번들이 여러 개면 OFFLINE_BUNDLE=<파일명> 으로 지정.
OFFLINE_DIR      ?= aluxlabs-link-win-msix/dist/AluxLabsLink-Offline
OFFLINE_BUNDLE   ?= $(notdir $(wildcard $(S3_SRC)*.msixbundle))
OFFLINE_SCRIPT   ?= aluxlabs-link-win-msix/Install-Offline.ps1

# --- 버전 관리 + .appinstaller 생성 (Windows 전용) ---
# 단일 소스: SharedProps/Version.props 의 <ReleaseTriplet> (= Major.Minor.Patch). 4번째 Build 는 커밋 수 자동.
# .appinstaller 는 staging 된 번들 파일명에서 버전을 그대로 읽어 생성 → 번들과 절대 어긋나지 않음.
VERSION_PROPS         ?= SharedProps/Version.props
APPINSTALLER_TEMPLATE ?= aluxlabs-link-win-msix/AluxLabsLink.appinstaller.template
APPINSTALLER          ?= aluxlabs-link-win-msix/dist/upload/AluxLabsLink.appinstaller
APPINSTALLER_HOST     ?= scratch-link.aluxcoding.com
APPINSTALLER_HOST_DEV ?= dev-scratch-link.aluxcoding.com
RELEASE_TRIPLET        = $(shell awk -F'[<>]' '/ReleaseTriplet/{print $$3}' $(VERSION_PROPS))
GIT_COMMITS            = $(shell git rev-list --count HEAD)
BUNDLE_VERSION         = $(patsubst AluxLabs-Link-%.msixbundle,%,$(S3_BUNDLE))

all: mac windows

clean:
	rm -vf $(MAC_IMAGES) $(WINDOWS_IMAGES)

mac: $(MAC_IMAGES)

windows: $(WINDOWS_IMAGES)

sync-s3: appinstaller stage-deps
	$(if $(strip $(S3_BUNDLE)),,$(error $(S3_SRC) 에 *.msixbundle 없음 — 빌드/서명/스테이징 먼저))
	"$(AWS)" s3 cp "$(S3_SRC)$(S3_BUNDLE)" "s3://$(S3_BUCKET)/$(S3_BUNDLE)" --content-type $(CT_MSIXBUNDLE) --cache-control "public, max-age=31536000, immutable" --region $(AWS_REGION)
	for f in $(VCLIBS); do "$(AWS)" s3 cp "$(S3_SRC)$$f" "s3://$(S3_BUCKET)/$$f" --content-type $(CT_MSIXBUNDLE) --cache-control "public, max-age=31536000, immutable" --region $(AWS_REGION); done
	"$(AWS)" s3 cp "$(S3_SRC)AluxLabsLink.appinstaller" "s3://$(S3_BUCKET)/AluxLabsLink.appinstaller" --content-type $(CT_APPINSTALLER) --cache-control "public, max-age=300" --region $(AWS_REGION)
	"$(AWS)" cloudfront create-invalidation --distribution-id $(CF_DIST_ID) --paths "/AluxLabsLink.appinstaller" "/$(S3_BUNDLE)" $(addprefix /,$(VCLIBS))

sync-s3-dev: appinstaller stage-deps
	$(if $(strip $(S3_BUNDLE)),,$(error $(S3_SRC) 에 *.msixbundle 없음 — 빌드/서명/스테이징 먼저))
	"$(AWS)" s3 cp "$(S3_SRC)$(S3_BUNDLE)" "s3://$(S3_BUCKET_DEV)/$(S3_BUNDLE)" --content-type $(CT_MSIXBUNDLE) --cache-control "public, max-age=31536000, immutable" --region $(AWS_REGION)
	for f in $(VCLIBS); do "$(AWS)" s3 cp "$(S3_SRC)$$f" "s3://$(S3_BUCKET_DEV)/$$f" --content-type $(CT_MSIXBUNDLE) --cache-control "public, max-age=31536000, immutable" --region $(AWS_REGION); done
	"$(AWS)" s3 cp "$(APPINSTALLER_DEV)" "s3://$(S3_BUCKET_DEV)/AluxLabsLink.appinstaller" --content-type $(CT_APPINSTALLER) --cache-control "public, max-age=300" --region $(AWS_REGION)
	"$(AWS)" cloudfront create-invalidation --distribution-id $(CF_DIST_ID_DEV) --paths "/AluxLabsLink.appinstaller" "/$(S3_BUNDLE)" $(addprefix /,$(VCLIBS))

show-version:
	@echo "triplet (Version.props): $(RELEASE_TRIPLET)"
	@echo "quad    (+build):        $(RELEASE_TRIPLET).$(GIT_COMMITS)"
	@echo "staged bundle:           $(if $(strip $(S3_BUNDLE)),$(S3_BUNDLE) [$(BUNDLE_VERSION)],(none in $(S3_SRC)))"

set-version:
	@echo "$(VERSION)" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$$' || { echo "ERROR: make set-version VERSION=x.y.z (예: 1.1.0)"; exit 1; }
	sed -i 's|<ReleaseTriplet>.*</ReleaseTriplet>|<ReleaseTriplet>$(VERSION)</ReleaseTriplet>|' $(VERSION_PROPS)
	@echo "Version.props -> $(VERSION)"

release-patch:
	$(MAKE) set-version VERSION=$(shell echo $(RELEASE_TRIPLET) | awk -F. '{print $$1"."$$2"."$$3+1}')

release-minor:
	$(MAKE) set-version VERSION=$(shell echo $(RELEASE_TRIPLET) | awk -F. '{print $$1"."$$2+1".0"}')

# staging 된 번들에 맞춰 prod/dev .appinstaller 두 개를 템플릿에서 생성
appinstaller:
	$(if $(strip $(S3_BUNDLE)),,$(error $(S3_SRC) 에 *.msixbundle 없음 — 빌드/서명/스테이징 먼저))
	sed -e 's|__HOST__|$(APPINSTALLER_HOST)|g' -e 's|__VERSION__|$(BUNDLE_VERSION)|g' -e 's|__BUNDLE__|$(S3_BUNDLE)|g' "$(APPINSTALLER_TEMPLATE)" > "$(APPINSTALLER)"
	sed -e 's|__HOST__|$(APPINSTALLER_HOST_DEV)|g' -e 's|__VERSION__|$(BUNDLE_VERSION)|g' -e 's|__BUNDLE__|$(S3_BUNDLE)|g' "$(APPINSTALLER_TEMPLATE)" > "$(APPINSTALLER_DEV)"
	@echo "appinstaller 생성: $(BUNDLE_VERSION) (prod + dev)"

# 레포 고정 VCLibs(Dependencies/{x64,x86}/)를 dist/upload/ 로 평탄화 복사 (업로드 소스 통일)
stage-deps:
	$(if $(strip $(VCLIBS_SRC)),,$(error $(VCLIBS_SRC) 없음))
	for f in $(VCLIBS); do a=$$(echo $$f | sed -E 's/.*\.(x64|x86)\..*/\1/'); cp -v "$(VCLIBS_SRC)/$$a/$$f" "$(S3_SRC)$$f"; done

# 오프라인/USB 설치 패키지 조립: 서명된 번들 + Install-Offline.ps1 + VCLibs 를 한 폴더로 모은다.
# 번들이 여러 개면 OFFLINE_BUNDLE=<파일명> 으로 명시.
offline-pack:
	$(if $(strip $(OFFLINE_BUNDLE)),,$(error $(S3_SRC) 에 릴리스 *.msixbundle 없음 — 빌드/서명/스테이징 먼저))
	$(if $(filter 1,$(words $(OFFLINE_BUNDLE))),,$(error 번들이 여러 개임: $(OFFLINE_BUNDLE) → make offline-pack OFFLINE_BUNDLE=<파일명>))
	rm -rf "$(OFFLINE_DIR)"
	mkdir -p "$(OFFLINE_DIR)/Dependencies"
	cp -v "$(S3_SRC)$(OFFLINE_BUNDLE)" "$(OFFLINE_DIR)/"
	cp -v "$(OFFLINE_SCRIPT)" "$(OFFLINE_DIR)/"
	cp -v $(VCLIBS_SRC)/x64/*.appx $(VCLIBS_SRC)/x86/*.appx "$(OFFLINE_DIR)/Dependencies/"
	@echo "오프라인 패키지: $(OFFLINE_DIR) ($(OFFLINE_BUNDLE)) — USB로 복사 후 Install-Offline.ps1 실행"

# Assumes the input SVG is square and that pixel [0,0] is a good background color
# Pads the output horizontally, using the background color, to match the requested size
# Usage: $(eval $(call svg2png,outpath/outfile.png,Assets/infile.svg,width,height,dpi))
define svg2png
$(1): $(2)
	./svg-convert.sh "$$<" "$$@" "$(3)" "$(4)" "$(5)"
endef

# Usage: $(eval $(call svg2ico,outpath/outfile.ico,Assets/infile.svg,size1 size2...))
define svg2ico
$(1): $(2)
	./svg-convert.sh "$$<" "$$@" $(3)
endef

# macOS app icon
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-16.png,Assets/rounded.svg,16,16,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-16@2x.png,Assets/rounded.svg,32,32,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-32.png,Assets/rounded.svg,32,32,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-32@2x.png,Assets/rounded.svg,64,64,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-128.png,Assets/rounded.svg,128,128,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-128@2x.png,Assets/rounded.svg,256,256,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-256.png,Assets/rounded.svg,256,256,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-256@2x.png,Assets/rounded.svg,512,512,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-512.png,Assets/rounded.svg,512,512,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/AppIcon.appiconset/AppIcon-512@2x.png,Assets/rounded.svg,1024,1024,144))

# macOS app status bar icon
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_16x16.png,Assets/glyph.svg,16,16,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_16x16@2x.png,Assets/glyph.svg,32,32,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_32x32.png,Assets/glyph.svg,32,32,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_32x32@2x.png,Assets/glyph.svg,64,64,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_128x128.png,Assets/glyph.svg,128,128,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_128x128@2x.png,Assets/glyph.svg,256,256,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_256x256.png,Assets/glyph.svg,256,256,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_256x256@2x.png,Assets/glyph.svg,512,512,144))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_512x512.png,Assets/glyph.svg,512,512,72))
$(eval $(call svg2png,aluxlabs-link-mac/Assets.xcassets/StatusBarIcon.iconset/icon_512x512@2x.png,Assets/glyph.svg,1024,1024,144))

# macOS Safari extension icon
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-48.png,Assets/rounded.svg,48,48,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-64.png,Assets/rounded.svg,64,64,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-96.png,Assets/rounded.svg,96,96,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-128.png,Assets/rounded.svg,128,128,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-256.png,Assets/rounded.svg,256,256,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/icon-512.png,Assets/rounded.svg,512,512,72))

# macOS Safari extension toolbar icon
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-16.png,Assets/glyph.svg,16,16,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-19.png,Assets/glyph.svg,19,19,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-32.png,Assets/glyph.svg,32,32,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-38.png,Assets/glyph.svg,38,38,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-48.png,Assets/glyph.svg,48,48,72))
$(eval $(call svg2png,AluxLabs\ Link\ Safari\ Helper/AluxLabs\ Link\ Safari\ Extension/Resources/images/toolbar-icon-72.png,Assets/glyph.svg,72,72,72))

# Windows app & tray icons
# See also:
#   https://stackoverflow.com/q/3236115
#   https://iconhandbook.co.uk/reference/chart/windows/
$(eval $(call svg2ico,aluxlabs-link-win/aluxlabs-link.ico,Assets/square.svg,256 128 96 64 48 32 24 16))
$(eval $(call svg2ico,aluxlabs-link-win/aluxlabs-link-tray.ico,Assets/simplified.svg,32 24 16))

# Windows MSIX
# TODO: does Microsoft really want DPI=72 for all of these?
# See https://learn.microsoft.com/en-us/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design#effective-pixels-and-scale-factor
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/LockScreenLogo.scale-200.png,Assets/rounded.svg,48,48,72))
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/SplashScreen.scale-200.png,Assets/rounded.svg,1240,600,72))
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/Square44x44Logo.scale-200.png,Assets/rounded.svg,88,88,72))
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/Square44x44Logo.targetsize-24_altform-unplated.png,Assets/rounded.svg,24,24,72))
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/Square150x150Logo.scale-200.png,Assets/rounded.svg,300,300,72))
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/StoreLogo.png,Assets/rounded.svg,50,50,72))
$(eval $(call svg2png,aluxlabs-link-win-msix/Images/Wide310x150Logo.scale-200.png,Assets/rounded.svg,620,300,72))
