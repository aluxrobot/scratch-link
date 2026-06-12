# 라이선스 / 상표 준수 제약 (필독)

> 원본 [`scratch-link`](https://github.com/scratchfoundation/scratch-link)(Scratch Foundation, AGPL-3.0-only)의 Windows 포크인 **AluxLabs Link**가 **반드시 지켜야 하는 법적 제약**. 리브랜딩 작업 자체는 끝났지만 이 제약은 **영구히 유효**하다.
> (폐기된 리브랜딩 계획서에서 법적 제약 부분만 발췌·정리)

## 1. 절대 변경/삭제 금지 (AGPL §5 / 상표)

| 항목 | 위치 | 이유 |
|---|---|---|
| AGPL 라이선스 전문 | `LICENSE` | AGPL §5 — 라이선스 텍스트 동봉 의무 |
| 상표 정책 문서 | `TRADEMARK` | Scratch Foundation 상표권 명시 — 삭제 시 법적 분쟁 위험 |
| `// <copyright file="X" company="Scratch Foundation">` 헤더 | 모든 원본 유래 `.cs` | AGPL §5 — 원저작자 표시 보존 |
| `// Copyright (c) Scratch Foundation. All rights reserved.` | 모든 원본 유래 `.cs` | 동일 |
| `// Based on scratch-link by Scratch Foundation, licensed under AGPL-3.0-only.` | ALUX 신규 작성 파일 헤더 | 정확한 attribution — 유지 |
| 원본 프로토콜 명세 문서 | `Documentation/Architecture.md`, `BluetoothLE.md`, `Bluetooth.md`, `NetworkProtocol.md`, `TestPlans.md` | 원본 프로토콜 명세(historical reference). "Scratch Link"를 프로토콜 명칭으로 보고 그대로 둔다 |
| 프로토콜 식별자·포트 | `/scratch/ble`, `/scratch/bt`, `/scratch/serial` (WebSocket path), 포트 `20211` | Scratch 클라이언트와의 wire-level 호환성 |

## 2. 상표("Scratch") 사용 규칙

- **제품명에 "Scratch" 단어 사용 금지** (nominative fair use도 안전 마진을 위해 회피).
- "for Scratch" / "Scratch-compatible" 같이 endorsement로 읽히는 표현 회피. 문서에 한해 "Scratch와 호환됨" 정도만.
- 소스/문서의 "Scratch Link"가 **프로토콜**을 가리키면 유지, **우리 제품**을 가리키면 "AluxLabs Link"로 표기.

## 3. 필수 파일 — `NOTICE` (AGPL §5)

저장소 루트 `NOTICE`에 fork 출처·변경 내역·상표 disclaimer를 명시한다. 권장 본문:

```
AluxLabs Link
Copyright (c) 2026 ALUX, Inc.

This product is derived from scratch-link by the Scratch Foundation
(https://github.com/scratchfoundation/scratch-link), originally licensed
under the GNU Affero General Public License v3.0 (AGPL-3.0-only).

This product is also distributed under the AGPL-3.0-only license.
See the LICENSE file for the full license text.

The following modifications have been made by ALUX, Inc.:
  - Removed macOS support and Safari Helper extension
  - Added USB Serial transport support
  - Changed default WebSocket port to 20211 to allow coexistence with
    the original Scratch Link on the same machine
  - Upgraded to .NET 8 and Windows App SDK 1.8

"Scratch" is a trademark of the Scratch Foundation. AluxLabs Link is
not affiliated with, endorsed by, or sponsored by the Scratch Foundation.
References to the "Scratch Link protocol" in source code documentation
refer to the network protocol established by the original scratch-link
project, used here for client compatibility.
```

`README.md` 상단에도 fork 출처 + AGPL + 상표 무관 disclaimer 블록을 유지한다:

```markdown
This is a Windows-only fork of [scratch-link](https://github.com/scratchfoundation/scratch-link)
by the Scratch Foundation, redistributed under the AGPL-3.0-only license.

"Scratch" is a trademark of the Scratch Foundation. This product is not
affiliated with, endorsed by, or sponsored by the Scratch Foundation.
```

## 4. 검증 체크리스트 (라이선스/상표 한정)

- [ ] `LICENSE`, `TRADEMARK` 가 변경되지 않았다
- [ ] 모든 `.cs`의 `company="Scratch Foundation"` 헤더 유지 (`grep -rn "Copyright (c) Scratch Foundation"` 결과가 불변)
- [ ] `NOTICE` 파일 존재 + fork 출처 명시
- [ ] `README.md`에 fork 출처 + AGPL 표시 + 상표 disclaimer 있음
- [ ] 제품명/번들 파일명에 "Scratch" 없음, 표시명은 "AluxLabs Link"
- [ ] 원본 프로토콜 명세 문서(`Documentation/*.md`) 텍스트 불변
