# 알려진 이슈 — Deployment

> 리브랜드/출하 검증 당시 기록 중 **현재까지 유효한 것만** 남긴 문서.
> 출하 형식·코드 서명은 모두 결정·동작 완료(MSIX 사이드로드 + Sectigo OV 토큰 서명) — [../signing/CodeSigningAndDistribution.md](../signing/CodeSigningAndDistribution.md) 참조.
> 과거의 MSIX packaged 크래시·Ghost 패키지 충돌·.NET 버전 회귀 시도 등은 해결되거나 무의미해져 삭제했다(필요 시 git 히스토리에서 확인).

## 1. 트레이 Tooltip 미표시 (unpackaged 한정)

### 증상
트레이 아이콘에 마우스 호버 시 풍선 tooltip이 표시되지 않는다.

### 영향 범위
**Unpackaged 모드(F5 디버그 / portable EXE)에서만** 발생한다. **MSIX 설치(packaged) 모드에서는 정상** — 실제 배포는 MSIX이므로 **사용자 영향 없음**. 개발 중 디버그 실행 시에만 보이는 현상이다.

### 추정 원인
`H.NotifyIcon.WinUI`가 `Shell_NotifyIcon(NIM_MODIFY)` 호출 시 `NIF_TIP` 플래그를 누락하는 라이브러리 버그로 추정. XAML `ToolTipText` 설정, 런타임 재할당, NuGet 버전 다운그레이드 모두 효과 없었다.

### 우선순위
**낮음** — 배포 형식(MSIX)에서 정상 동작하므로 출하를 막지 않는다. 사용자 피드백이 명시적으로 요구하면 라이브러리 교체(P/Invoke `Shell_NotifyIcon` 직접 호출 등)를 검토한다.

---

> **재시도 금지 메모:** `.NET 8` + `WindowsAppSDK 1.8` 스택을 원본 `.NET 6` + `WAS 1.3/1.5/1.6`으로 회귀하는 시도는 **불가**하다. 옛 WAS NuGet의 build target이 현재 빌드 환경(.NET 10 SDK + VS 2026)의 경로 구조와 비호환이라, 빌드 환경까지 회귀해야 한다. 작업량 크고 호환성 이슈 사슬 가능성 — **다시 시도하지 말 것**.
