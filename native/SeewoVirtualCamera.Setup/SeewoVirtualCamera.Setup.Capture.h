// SeewoVirtualCamera.Setup.Capture.h
//
// The "capture" command: reads real frames from the virtual camera through the
// standard Media Foundation device-source path, proving the media source actually
// delivers samples rather than merely appearing in a device enumeration.
#pragma once

#include <windows.h>

#include <string>

// Opens the virtual camera whose friendly name contains `friendlyNameSubstring`
// (an empty string matches the first virtual camera found), reads `frameCount`
// frames, and optionally writes the first frame to `outputPath` as a PNG.
//
// On success `summary` receives a human-readable report of what was observed -
// frame count, measured rate, brightness and whether the picture changed. The text
// is returned rather than printed so that the caller, which already knows whether
// stdout is a console, can emit it with the correct encoding.
//
// Returns 0 on success. On failure returns a non-zero exit code and fills `error`
// with an explanation rather than a bare HRESULT.
int CaptureFrames(const std::wstring& friendlyNameSubstring,
                  int frameCount,
                  const std::wstring& outputPath,
                  std::wstring* summary,
                  std::wstring* error);
