# LocalTTS v4.4

작성자: **JW-292**

기존 LocalTTS의 UI, PIP, 마이크 릴레이, 본인 모니터링, Discord 단일 오디오 버스는
유지하고 음성 엔진만 독립 합성음성 v4.4로 교체한 Windows 로컬 앱입니다.

## 실행

1. `A:\LocalTTS-AI\GPT-SoVITS-v2-240821` 엔진과
   `A:\LocalTTS-AI\voices\mashiro_candidate_v5b\mashiro_candidate_v5b-e1.ckpt`가
   현재 PC에 있어야 합니다.
2. `run_v44.bat` 또는 `LocalTTS_v4.4.exe`를 실행합니다.
3. 음성 목록 첫 항목인 `독립 합성음성 v4.4`를 사용합니다.
4. Discord 송출은 앱에서 `CABLE Input`, Discord에서 `CABLE Output`을 선택합니다.
5. Discord 입력 프로필은 합성음을 훼손하지 않는 `Studio / 순수 오디오`를 권장합니다.

PIP에서는 한 줄 입력창에 문장을 쓰고 Enter를 누릅니다. 입력은 접수 즉시 지워지며,
PIP의 X는 앱을 종료하지 않고 일반 화면으로 돌아갑니다.

## v4.4 고정 구성

- 음색 비율: Mashiro 40%, Riko 20%, Tabi 20%, Uni 20%
- GPT: `mashiro_candidate_v5b-e1.ckpt`
- SoVITS: `models/v44/v44_fourway_sovits.pth`
- reference: `models/v44/ref_v44_fourway.wav`
- 추론: `cut0`, top-k 3, top-p 0.68, temperature 0.52,
  repetition penalty 1.28, speed 0.94, seed 24680
- 폭주 방지: `max(6.5초, 유효 글자 수 × 0.30초)` 초과 시 seed 101로 1회 재합성
- 파형 보정: 750Hz -0.75dB, 2.8kHz +0.75dB, 양방향 biquad, peak 0.95

앱은 시작할 때 `config/v44_tts_infer.yaml`을 현재 설치 경로에 맞게 다시 만들므로
폴더를 옮겨도 v4.4 모델 경로가 깨지지 않습니다. Numba 캐시는 공용 AppData가 아니라
앱의 `cache/numba`에 생성되어 권한 충돌을 피합니다.

## 검사와 빌드

명령 프롬프트에서 다음을 실행할 수 있습니다.

```bat
build.bat
LocalTTS_v4.4.exe --self-test
LocalTTS_v4.4.exe --v44-self-test
LocalTTS_v4.4.exe --audio-bus-self-test
```

- `--v44-self-test`: 실제 모델 선택, 합성, 파형 보정, 품질 가드 검사
- `--audio-bus-self-test`: 48kHz float Discord 버스 반복 재생/재열기 검사

## 폴더 구성

| 경로 | 용도 |
|---|---|
| `LocalTTS_v4.4.exe` | 실행 파일 |
| `run_v44.bat` | 간편 실행 |
| `NAudio.dll` | 오디오 출력 라이브러리 |
| `models/v44/` | v4.4 SoVITS, reference, manifest |
| `assets/` | 앱 배경과 PIP 이미지 |
| `src/` | 재현 가능한 C# 소스 |
| `docs/` | 전체 분석 보고서와 PDF |
| `build.bat` | 소스 재빌드 |

`cache/`와 `LocalTTS_stability.log`는 실행 중 자동 생성되며 Git에는 포함되지 않습니다.

## 주의

- 두 LocalTTS 버전을 동시에 같은 CABLE 장치에 송출하지 마세요.
- Discord에서 합성음이 짧게 잘리거나 웅얼거리면 모델을 바꾸기 전에 Studio 입력
  프로필, Krisp, 자동 gain, echo cancellation, 자동 입력 감도를 확인하세요.
- 모델과 원본 음성의 사용·배포 권리는 사용자가 직접 확인해야 합니다.

상세한 실험 근거와 A/B 수치는 [음성 품질 엔지니어링 보고서](docs/VOICE_QUALITY_ENGINEERING_REPORT.md)에 있습니다.

