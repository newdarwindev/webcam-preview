# Webcam Preview

Webcam Preview is a small, borderless Windows webcam viewer. It shows the full camera frame, stays above other desktop windows, and opens when you sign in. The preview runs locally and has no recording or streaming features.

## Install

1. Download [Install-WebcamPreview.exe](https://github.com/newdarwindev/webcam-preview/releases/latest/download/Install-WebcamPreview.exe).
2. Run the installer. It installs for your Windows account, creates Start menu and sign-in shortcuts, and opens the preview.

Requires 64-bit Windows 10 or 11 and a Windows-compatible camera or capture device. The installer is unsigned and needs no administrator privileges. Run it again to update an existing installation.

## Use

- Drag the image to move the window. Drag an edge or corner to resize it.
- Right-click to toggle **Invert colors** or close the preview. Inversion is enabled when the app opens.
- The viewer prefers a device named **USB Video** and otherwise uses the first available camera. To specify a different camera, add its exact Windows device name in quotes to the shortcut's target arguments.

### Gamepad

With an XInput-compatible controller, press **A** to toggle fullscreen for the selected webcam or Chrome tab. Use **LB/RB** to move backward or forward through the webcam and open Chrome tabs. **LT/RT** also cycle. Fullscreen follows the selection. Press **B** to restart the camera capture.

Run `powershell -File scripts/test-gamepad.ps1` to log controller button presses and releases for a live input test.

## Build from source

Run `powershell -File scripts/build.ps1` to build the viewer and self-contained installer in `dist`. Run `powershell -File scripts/install.ps1` to build and install in one step.

## Remove

Close the preview. Remove the **Webcam Preview** shortcuts from your Windows Start menu Programs and Startup folders, then delete `%LOCALAPPDATA%\WebcamPreview`.
