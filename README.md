# AE_Preprocessor

이 프로젝트는 오디오 시퀀스(WAV) 데이터를 분석하여 스펙트로그램(Spectrogram) 이미지를 생성하는 전처리 도구입니다.

## 📁 데이터 구조 (DB)

프로그램은 루트 디렉토리의 `DB` 폴더 내에 있는 하위 폴더들을 순회하며 데이터를 처리합니다.

```text
AE_Preprocessor/
├── DB/
│   ├── YYMMDD_HHMMSS/          # 개별 실험/데이터 세션 폴더
│   │   ├── Audio/              # WAV 파일들이 저장된 폴더
│   │   │   ├── 0.wav
│   │   │   └── ...
│   │   └── Spectrogram/        # (생성됨) 변환된 이미지가 저장되는 곳
│   └── ...
├── CSharp/                     # 소스 코드
└── config/                     # 분석 설정 파일
```

*   **입력**: `DB/{세션명}/` 내부의 모든 `.wav` 파일을 순서대로 읽어 하나로 합친 뒤 처리합니다.
*   **출력**: `DB/{세션명}/Spectrogram/` 폴더 내에 `00000000.png` 형식의 8자리 패딩 파일명으로 저장됩니다.

## 🛠 사용 방법

### 1. 환경 준비
*   [.NET 8.0 SDK](https://dotnet.microsoft.com/download)가 설치되어 있어야 합니다.

### 2. 설정 확인
`config/Sensor_Interface/MicrophoneInterfaceConfig.xml` 파일을 통해 분석 파라미터를 수정할 수 있습니다.
*   `SampleRate`: 오디오 샘플링 속도 (기본: 48000)
*   `FftSize`: FFT 크기 (기본: 512)
*   `HopSize`: 프레임 간격 (기본: 256)
*   `StripWidth`: 결과 이미지 한 장에 들어갈 프레임 수 (기본: 94)

### 3. 실행 명령어
터미널에서 프로젝트 루트 디렉토리 기준으로 아래 명령어를 입력하여 실행합니다.

```powershell
dotnet run -p .\CSharp\SpectrogramProcessor.csproj
```

## 📝 주요 특징
*   **dB Scaling & Gating**: 소리 신호를 데시벨 단위로 변환하고 노이즈 플로어를 처리하여 시각화합니다.
*   **자동 배치 처리**: `DB` 폴더 내의 모든 세션을 한 번에 처리합니다.
*   **이미지 최적화**: OpenCV를 사용하여 8-bit 단일 채널(Grayscale) PNG로 저장합니다.
