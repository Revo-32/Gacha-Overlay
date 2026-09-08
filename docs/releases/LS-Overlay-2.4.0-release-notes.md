# LS Overlay 2.4.0

GTA Companion의 공개 이벤트 콘텐츠 한국어 번역 품질과 안정성을 개선한 업데이트입니다.

## 주요 변경사항

- 지정된 공개 GTA 온라인 이벤트 정보를 자연스러운 한국어로 제공하기 위한 AI 기반 번역 시스템을 추가했습니다.
- 총기, 무기 밴, 사업장, 부동산, 습격, 활동 등 주요 용어를 일관되게 표시하도록 승인된 한국어 용어집을 확장했습니다.
- 차량명과 검증되지 않은 고유 활동명 등은 임의로 번역하지 않도록 보호합니다.
- 금액, GTA 달러, RP, 할인율, 배수, 횟수, 기간 등의 사실값을 검증합니다.
- `RP를`, `90분`, `3배`와 같은 한국어 조사·기간·배수 표현을 자연스럽게 표시하도록 후처리를 개선했습니다.
- 검증된 번역을 서버의 제한된 Translation Memory에 저장하고, 동일한 내용과 서버 재시작 후에 재사용합니다.
- AI 번역에 실패하면 안전한 기존 정보 또는 원문으로 대체하며 기존 Last-Good 기능은 유지됩니다.

## 개인정보 및 외부 AI 처리

Google Gemini를 이용한 AI 번역은 지정 채널과 작성자가 모두 확인된 공개 GTA 온라인 이벤트 소스에만 적용됩니다. 일반 Discord 채팅, 판매 메시지, 사용자 정보, 인증정보 등은 AI 번역 서비스로 전송하지 않습니다.

AI 번역의 완전한 정확도나 상시 가용성을 보장하지 않습니다. 중요한 이벤트 조건은 공개 원문과 함께 확인하세요.

## 설치 및 업데이트

**LS-Overlay-2.4.0-win-x64.zip**을 새 폴더에 모두 압축 해제하고, 기존 LS Overlay를 트레이에서 완전히 종료한 뒤 **LSOverlay.exe**를 실행하세요. 별도 .NET 설치는 필요하지 않습니다.

기존 `%LOCALAPPDATA%\GachaOverlay` 폴더는 삭제하지 마세요.

## 링크

- [서비스 상태](https://status.revo32.cloud)
- [개인정보처리방침](https://overlay.revo32.cloud/privacy)
- [이용약관](https://overlay.revo32.cloud/terms)
- [문의: revo.32.39.41@gmail.com](mailto:revo.32.39.41@gmail.com)

LS Overlay는 Rockstar Games, Take-Two Interactive, Discord, Google과 제휴하거나 이들의 승인을 받은 제품이 아닌 독립적인 비공식 도구입니다.
