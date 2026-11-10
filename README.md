# FFXIVLauncherKR

[글로벌 퀵런처](https://github.com/goatcorp/FFXIVQuickLauncher) 기반 한국 서버용 런처

## 설치

[릴리즈 페이지](https://github.com/dal4kr/FFXIVQuickLauncherKR/releases/latest)에서 설치파일 다운로드 후 실행.

## 사용 방법

1. 게임 설치 경로 선택. 새 폴더에 설치하려면 **게임 설치 / 업데이트** 탭에서 진행
1. 게임 설치 경로를 확인. 기본값은 `C:\Program Files (x86)\FINAL FANTASY XIV - KOREA`
1. 로그인 방식을 선택. 액토즈는 아이디·비밀번호·자동 입력 방지 문자를 입력하고, 네이버는 별도 인증 창에서 로그인
1. 게임 OTP가 설정되어 있으면 OTP 앱의 인증번호 입력. 로그인 완료 후 게임 실행
1. 점검 중 사전 패치가 공개되면 로그인 후 **게임 설치 / 업데이트** 탭에서 진행. 점검 종료 후 **접속 상태 새로고침**을 누르면 게임용 SID 발급. 런처를 종료했거나 인증이 만료되면 다시 로그인
1. 로그인 세션 저장 시 다음 실행부터 저장한 SID를 그대로 사용. 세션이 만료되면 **새로 로그인**으로 교체
1. **계정 추가**로 여러 계정을 저장하고 목록에서 선택. **자동 실행**을 켜면 런처 업데이트 확인 후 저장된 세션으로 게임 실행

## 백업 및 복원

**백업 / 복원** 탭에서 `.xivlauncher_backup` 파일을 만들거나 복원할 수 있습니다. 게임은 종료한 상태에서 진행합니다.

- 런처 설정·암호화된 계정 파일·Dalamud 설정·설치된 플러그인·플러그인 설정을 포함
- **게임·캐릭터 설정도 백업**을 선택하면 문서 폴더의 KR 게임 설정도 포함. 복원 시 함께 복원할지 선택
- 복원은 포함된 파일을 덮어쓰며, 백업에 없는 파일은 유지
- 다른 Windows 사용자 환경에서는 계정 세션을 다시 로그인해야 할 수 있음
- 게임 설치 파일·런타임·로그·스크린샷과 플러그인이 외부에 저장한 데이터는 포함하지 않음

## 유의 사항

1. **네이버 로그인, OTP는 테스트 하지 않음**
1. 계정별 SID는 현재 Windows 사용자만 사용 가능하게 암호화
1. 게임 OTP는 입력창에서 직접 입력. 네이버 로그인 창에는 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/#download-section)이 필요
1. 비밀번호·OTP·웹 쿠키·인증키 등은 파일에 저장하지 않음. 계정별 게임 SID만 저장
1. Dalamud 설정의 **지원 게임 버전 검사 무시**를 켜면 다른 버전에서도 연결 시도. 기본은 해제
1. 공지·배너는 포함하지 않음
1. 게임 실행 버튼은 저장한 SID를 사용. 서버 재시작 등으로 SID가 만료되면 **새로 로그인** 필요

## 저장 위치

`%AppData%/XIVLauncherKR/`

- accounts.dat: 로그인 방식별 계정의 암호화한 SID와 발급 시각
- settings.json: 게임 경로, 로그인 방식·아이디, 세션 저장 여부, 관리자 실행 여부, Dalamud 옵션. 비밀번호는 저장하지 않음

- addon/Hooks: KR Dalamud 배포본
- dalamudConfig.json, installedPlugins, pluginConfigs: 기존 설정 및 플러그인
- dalamudAssets: 버전별 assets
- runtime: 필요한 경우 사용하는 별도 .NET 런타임
- temp: Dalamud 임시 파일
- .launcher-staging: 다운로드·검증 중인 파일

게임 내려받기·패치 임시 파일은 선택한 게임 설치 폴더의 `.xivlauncher-update`에 저장. Program Files 등 보호된 폴더에서 쓰기 권한이 없으면 런처의 관리자 실행 옵션 사용.
