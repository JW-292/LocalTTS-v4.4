@echo off
setlocal
cd /d "%~dp0"
set "CSC=C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
  echo Windows C# compiler was not found.
  pause
  exit /b 1
)

if not exist "NAudio.dll" (
  echo NAudio.dll is missing.
  pause
  exit /b 1
)

if not exist "models\v44\v44_fourway_sovits.pth" (
  echo v4.4 SoVITS model is missing.
  pause
  exit /b 1
)

if not exist "config\v44_tts_infer.yaml" (
  echo v4.4 inference config is missing.
  pause
  exit /b 1
)

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /define:VOICE_V44 /out:LocalTTS_v4.4.exe /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll /reference:"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\v3.0\System.Speech.dll" /reference:NAudio.dll /resource:"assets\mashiro_ui_background_app.png",LocalTtsVoice.Assets.MashiroBackground /resource:"assets\mashiro_pip_avatar_app.png",LocalTtsVoice.Assets.MashiroAvatar src\NativeTTS_v44.cs
if errorlevel 1 (
  echo.
  echo LocalTTS v4.4 build failed.
  pause
  exit /b 1
)

echo LocalTTS_v4.4.exe was built successfully.
endlocal
