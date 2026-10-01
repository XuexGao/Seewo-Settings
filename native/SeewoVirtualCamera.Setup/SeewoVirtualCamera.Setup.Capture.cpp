// SeewoVirtualCamera.Setup.Capture.cpp
//
// Implements the "capture" command: opens the virtual camera as an ordinary
// consumer application would, pulls a number of frames, and reports what it got.
//
// This exists because registering a camera and creating it only prove that the
// device appears in an enumeration. Neither proves the media source actually
// delivers samples, which is the entire point of the component. This command
// closes that gap by reading real frames through the standard Media Foundation
// device-source path - the same one Zoom, Teams and ffmpeg use - and can write one
// frame to a PNG so the result can be looked at directly.
//
// It is deliberately part of the setup tool rather than of the app: it needs no UI,
// and being runnable from a build script is what makes it useful as a test.

#include "SeewoVirtualCamera.Setup.Capture.h"

#include <mfapi.h>
#include <mfidl.h>
#include <mfobjects.h>
#include <mfreadwrite.h>
#include <mferror.h>
#include <wincodec.h>
#include <ocidl.h>   // IPropertyBag2, used by IWICBitmapFrameEncode
#include <shlwapi.h>

#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "windowscodecs.lib")

#include <winternl.h>   // RTL_OSVERSIONINFOW, for the real OS build number

#include <cstdio>
#include <cstring>   // memcmp, used by the frame comparison
#include <string>
#include <vector>

namespace {

// Writes an IMFMediaBuffer holding an RGB32 image out as a PNG.
//
// WIC is used rather than a raw pixel dump so the file can be opened and looked at
// directly, which is the point of producing it.
HRESULT SaveRgb32ToPng(const wchar_t* path,
                       IMFMediaBuffer* buffer,
                       UINT32 width,
                       UINT32 height,
                       LONG stride,
                       std::wstring* error) {
    BYTE* scanline0 = nullptr;
    HRESULT hr = buffer->Lock(&scanline0, nullptr, nullptr);
    if (FAILED(hr)) {
        *error = L"无法锁定样本缓冲区。";
        return hr;
    }

    HRESULT result = S_OK;
    IWICImagingFactory* factory = nullptr;
    IWICBitmapEncoder* encoder = nullptr;
    IWICStream* stream = nullptr;
    IWICBitmapFrameEncode* frame = nullptr;
    IPropertyBag2* properties = nullptr;

    do {
        hr = ::CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                IID_PPV_ARGS(&factory));
        if (FAILED(hr)) { result = hr; *error = L"无法创建 WIC 工厂。"; break; }

        hr = factory->CreateStream(&stream);
        if (FAILED(hr)) { result = hr; break; }

        hr = stream->InitializeFromFilename(path, GENERIC_WRITE);
        if (FAILED(hr)) { result = hr; *error = L"无法创建输出文件。"; break; }

        hr = factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder);
        if (FAILED(hr)) { result = hr; break; }

        hr = encoder->Initialize(stream, WICBitmapEncoderNoCache);
        if (FAILED(hr)) { result = hr; break; }

        hr = encoder->CreateNewFrame(&frame, &properties);
        if (FAILED(hr)) { result = hr; break; }

        hr = frame->Initialize(properties);
        if (FAILED(hr)) { result = hr; break; }

        hr = frame->SetSize(width, height);
        if (FAILED(hr)) { result = hr; break; }

        WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
        hr = frame->SetPixelFormat(&format);
        if (FAILED(hr)) { result = hr; break; }

        // A negative stride means the buffer is bottom-up, which is the usual case
        // for a Media Foundation RGB32 frame. The rows are walked in reverse so the
        // PNG is not saved upside down.
        const bool bottomUp = stride < 0;
        const LONG rowBytes = bottomUp ? -stride : stride;

        for (UINT32 y = 0; y < height; ++y) {
            const UINT32 sourceRow = bottomUp ? (height - 1 - y) : y;
            BYTE* row = scanline0 + (static_cast<size_t>(sourceRow) * rowBytes);

            hr = frame->WritePixels(1, static_cast<UINT>(rowBytes),
                                    static_cast<UINT>(rowBytes), row);
            if (FAILED(hr)) { result = hr; *error = L"写入 PNG 像素失败。"; break; }
        }

        if (FAILED(hr)) { break; }

        hr = frame->Commit();
        if (FAILED(hr)) { result = hr; break; }

        hr = encoder->Commit();
        if (FAILED(hr)) { result = hr; *error = L"提交 PNG 失败。"; break; }
    } while (false);

    if (properties) properties->Release();
    if (frame) frame->Release();
    if (encoder) encoder->Release();
    if (stream) stream->Release();
    if (factory) factory->Release();

    buffer->Unlock();
    return result;
}

// Rough average brightness of a frame, used to tell a real image apart from an
// all-black one. A virtual camera that enumerates but yields black frames is a
// realistic failure mode, and reporting "80 frames, all black" is far more useful
// than reporting "80 frames".
double AverageLuminance(IMFMediaBuffer* buffer, UINT32 width, UINT32 height, LONG stride) {
    BYTE* data = nullptr;
    if (FAILED(buffer->Lock(&data, nullptr, nullptr))) {
        return -1.0;
    }

    const bool bottomUp = stride < 0;
    const LONG rowBytes = bottomUp ? -stride : stride;

    unsigned long long total = 0;
    unsigned long long count = 0;

    // Sampling every 16th pixel in each direction is ample for a brightness check
    // and keeps this cheap on a 1080p frame.
    for (UINT32 y = 0; y < height; y += 16) {
        BYTE* row = data + (static_cast<size_t>(y) * rowBytes);
        for (UINT32 x = 0; x < width; x += 16) {
            const BYTE* pixel = row + (static_cast<size_t>(x) * 4);
            // BGRA: the green channel dominates perceived luma, so it is a good
            // single-channel proxy for brightness.
            total += pixel[1];
            ++count;
        }
    }

    buffer->Unlock();
    return count == 0 ? -1.0 : static_cast<double>(total) / static_cast<double>(count);
}

// Releases every device activation returned by MFEnumDeviceSources.
void ReleaseDeviceList(IMFActivate** devices, UINT32 count) {
    if (devices == nullptr) {
        return;
    }

    for (UINT32 i = 0; i < count; ++i) {
        devices[i]->Release();
    }

    ::CoTaskMemFree(devices);
}

// Finds the index of the first virtual camera, or -1 when there is none.
int FindVirtualCamera(IMFActivate** devices, UINT32 deviceCount,
                      const std::wstring& friendlyNameSubstring) {
    for (UINT32 i = 0; i < deviceCount; ++i) {
        wchar_t* name = nullptr;
        UINT32 nameLength = 0;

        if (FAILED(devices[i]->GetAllocatedString(
                MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &nameLength)) || name == nullptr) {
            continue;
        }

        // The pipeline appends "Windows Virtual Camera" to the friendly name we
        // registered, so matching that suffix identifies a virtual camera and also
        // keeps a physical camera from being picked by accident.
        const bool matchesName = friendlyNameSubstring.empty() ||
            ::StrStrIW(name, friendlyNameSubstring.c_str()) != nullptr;
        const bool isVirtual = ::StrStrIW(name, L"Virtual Camera") != nullptr;

        ::CoTaskMemFree(name);

        if (matchesName && isVirtual) {
            return static_cast<int>(i);
        }
    }

    return -1;
}

// Appends the report of what was observed.
//
// The text is built here and printed by the caller rather than written to stdout
// directly, because the caller already knows whether stdout is a console and
// therefore which encoding to use. Duplicating that decision here is exactly how
// the mojibake defect arose in the first place.
void AppendReport(std::wstring* summary,
                  UINT32 framesRead,
                  UINT32 width,
                  UINT32 height,
                  double firstLuminance,
                  double lastLuminance,
                  bool distinctFrames,
                  bool haveFirstFrameBytes,
                  ULONGLONG firstSampleTime,
                  ULONGLONG lastSampleTime,
                  const std::wstring& outputPath) {
    wchar_t line[512] = {};

    ::swprintf_s(line, L"已从虚拟摄像头读取 %u 帧（%ux%u）。", framesRead, width, height);
    summary->append(L"[完成] ");
    summary->append(line);

    if (lastSampleTime > firstSampleTime && framesRead > 1) {
        const double fps =
            (static_cast<double>(framesRead - 1) * 10000000.0) /
            static_cast<double>(lastSampleTime - firstSampleTime);
        ::swprintf_s(line, L"\n       实测帧率：%.1f fps", fps);
        summary->append(line);
    }

    if (firstLuminance >= 0.0 && lastLuminance >= 0.0) {
        ::swprintf_s(line, L"\n       平均亮度：首帧 %.1f，末帧 %.1f（0 = 全黑）",
                     firstLuminance, lastLuminance);
        summary->append(line);
    }

    if (haveFirstFrameBytes) {
        ::swprintf_s(line, L"\n       画面%s。",
                     distinctFrames ? L"在变化（推流正常）"
                                    : L"没有变化（静止画面或内置测试图案都会如此）");
        summary->append(line);
    }

    if (!outputPath.empty() && ::PathFileExistsW(outputPath.c_str())) {
        ::swprintf_s(line, L"\n       首帧已保存到：%ls", outputPath.c_str());
        summary->append(line);
    }
}

}  // namespace

// Builds a message that distinguishes "not installed" from "this backend cannot be
// read by this command".
//
// The Windows 10 DirectShow filter does not appear in the Media Foundation device list,
// so on that system `capture` can never find it no matter how the install went. Saying
// so is far more useful than the generic "run install first", which sends the user to
// re-run a step that already succeeded.
std::wstring DescribeMissingCamera(UINT32 deviceCount) {
    RTL_OSVERSIONINFOW version = {};
    version.dwOSVersionInfoSize = sizeof(version);

    HMODULE ntdll = ::GetModuleHandleW(L"ntdll.dll");
    bool isWindows11OrLater = false;

    if (ntdll != nullptr) {
        using RtlGetVersionFn = LONG(WINAPI*)(PRTL_OSVERSIONINFOW);
        auto rtlGetVersion = reinterpret_cast<RtlGetVersionFn>(
            ::GetProcAddress(ntdll, "RtlGetVersion"));

        if (rtlGetVersion != nullptr && rtlGetVersion(&version) == 0) {
            isWindows11OrLater = version.dwBuildNumber >= 22000;
        }
    }

    if (!isWindows11OrLater) {
        return L"本命令只能读取 Media Foundation 虚拟摄像头，而当前系统（内部版本低于 22000）"
               L"使用的是 DirectShow 回退方案，该方案不会出现在 Media Foundation 的设备列表中。"
               L"这不是安装失败。请改用 DirectShow 应用验证："
               L"ffmpeg -list_devices true -f dshow -i dummy，"
               L"或在 OBS / 微信 / 钉钉的摄像头列表中选择 SeewoAssistant Virtual Camera。";
    }

    if (deviceCount == 0) {
        return L"系统没有报告任何视频输入设备。请先运行 install 注册组件并创建摄像头实例。";
    }

    return L"已枚举到视频设备，但没有找到本程序的虚拟摄像头。请运行 install 或 create 创建它。";
}

int CaptureFrames(const std::wstring& friendlyNameSubstring,
                  int frameCount,
                  const std::wstring& outputPath,
                  std::wstring* summary,
                  std::wstring* error) {
    const HRESULT comResult = ::CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(comResult) && comResult != RPC_E_CHANGED_MODE) {
        *error = L"CoInitializeEx 失败。";
        return 4;
    }

    const HRESULT startupResult = ::MFStartup(MF_VERSION, MFSTARTUP_LITE);
    if (FAILED(startupResult)) {
        *error = L"MFStartup 失败，Media Foundation 平台不可用。";
        return 4;
    }

    IMFAttributes* attributes = nullptr;
    IMFActivate** devices = nullptr;
    UINT32 deviceCount = 0;
    IMFMediaSource* source = nullptr;
    IMFSourceReader* reader = nullptr;

    int exitCode = 0;

    do {
        HRESULT hr = ::MFCreateAttributes(&attributes, 1);
        if (FAILED(hr)) { *error = L"MFCreateAttributes 失败。"; exitCode = 4; break; }

        attributes->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE,
                            MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);

        hr = ::MFEnumDeviceSources(attributes, &devices, &deviceCount);
        if (FAILED(hr)) { *error = L"MFEnumDeviceSources 失败。"; exitCode = 4; break; }

        const int selected = FindVirtualCamera(devices, deviceCount, friendlyNameSubstring);

        if (selected < 0) {
            // Say which backend this command can actually serve. On Windows 10 the
            // camera is a DirectShow filter, which Media Foundation cannot enumerate, so
            // "run install first" is misleading advice - install already succeeded and
            // this subcommand simply does not apply.
            const std::wstring reason = DescribeMissingCamera(deviceCount);
            *error = reason;
            exitCode = 7;
            break;
        }

        hr = devices[selected]->ActivateObject(IID_PPV_ARGS(&source));
        if (FAILED(hr)) { *error = L"激活虚拟摄像头失败，媒体源可能未能加载。"; exitCode = 7; break; }

        hr = ::MFCreateSourceReaderFromMediaSource(source, nullptr, &reader);
        if (FAILED(hr)) { *error = L"创建 SourceReader 失败。"; exitCode = 7; break; }

        // Ask for RGB32 so the frame can be inspected and saved without writing a
        // YUV converter here. If the source will not offer it, its native output is
        // still fine for counting frames, so this is not fatal.
        IMFMediaType* targetType = nullptr;
        if (SUCCEEDED(::MFCreateMediaType(&targetType))) {
            targetType->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
            targetType->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_RGB32);

            const HRESULT setResult = reader->SetCurrentMediaType(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, targetType);
            targetType->Release();

            if (FAILED(setResult)) {
                // Fall back to the source's preferred type; frames are still counted.
                reader->SetCurrentMediaType(
                    static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, nullptr);
            }
        }

        UINT32 width = 0;
        UINT32 height = 0;
        LONG stride = 0;
        UINT32 framesRead = 0;
        UINT32 gaps = 0;
        double firstLuminance = -1.0;
        double lastLuminance = -1.0;
        bool distinctFrames = false;
        ULONGLONG firstSampleTime = 0;
        ULONGLONG lastSampleTime = 0;
        std::vector<BYTE> firstFrameBytes;

        for (int i = 0; i < frameCount; ++i) {
            DWORD streamFlags = 0;
            LONGLONG timestamp = 0;
            IMFSample* sample = nullptr;

            hr = reader->ReadSample(
                static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM),
                0, nullptr, &streamFlags, &timestamp, &sample);

            if (FAILED(hr)) {
                *error = L"ReadSample 失败，无法从虚拟摄像头读取画面。";
                exitCode = 8;
                if (sample != nullptr) { sample->Release(); }
                break;
            }

            if ((streamFlags & MF_SOURCE_READERF_ENDOFSTREAM) != 0) {
                *error = L"流提前结束，虚拟摄像头没有持续输出画面。";
                exitCode = 8;
                if (sample != nullptr) { sample->Release(); }
                break;
            }

            if (sample == nullptr) {
                // A gap is normal for a live source: no sample was ready for this
                // request. It is counted so a stream that is mostly gaps is visible.
                ++gaps;
                continue;
            }

            if (framesRead == 0) {
                // Derive the geometry from the sample's own media type rather than
                // assuming RGB32, so a fallback type is still reported correctly.
                IMFMediaType* currentType = nullptr;
                if (SUCCEEDED(reader->GetCurrentMediaType(
                        static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), &currentType))) {
                    ::MFGetAttributeSize(currentType, MF_MT_FRAME_SIZE, &width, &height);

                    LONG storedStride = 0;
                    UINT32 strideSize = sizeof(storedStride);
                    if (SUCCEEDED(currentType->GetBlob(
                            MF_MT_DEFAULT_STRIDE,
                            reinterpret_cast<UINT8*>(&storedStride),
                            sizeof(storedStride), &strideSize))) {
                        stride = storedStride;
                    }

                    currentType->Release();
                }

                // A missing stride attribute is normal; the natural stride for RGB32
                // is width * 4.
                if (stride == 0 && width > 0) {
                    stride = static_cast<LONG>(width * 4);
                }

                firstSampleTime = static_cast<ULONGLONG>(timestamp);

                IMFMediaBuffer* firstBuffer = nullptr;
                if (SUCCEEDED(sample->ConvertToContiguousBuffer(&firstBuffer)) && firstBuffer != nullptr) {
                    BYTE* data = nullptr;
                    DWORD length = 0;
                    if (SUCCEEDED(firstBuffer->Lock(&data, nullptr, &length))) {
                        firstFrameBytes.assign(data, data + length);
                        firstBuffer->Unlock();
                    }
                    firstBuffer->Release();
                }
            }

            IMFMediaBuffer* buffer = nullptr;
            if (SUCCEEDED(sample->ConvertToContiguousBuffer(&buffer)) && buffer != nullptr) {
                const double luminance = AverageLuminance(buffer, width, height, stride);

                if (framesRead == 0) {
                    firstLuminance = luminance;

                    if (!outputPath.empty()) {
                        std::wstring saveError;
                        if (FAILED(SaveRgb32ToPng(outputPath.c_str(), buffer, width, height,
                                                  stride, &saveError))) {
                            // Not fatal: frames were read successfully, so the save
                            // failure is only reported when nothing else went wrong.
                            if (error->empty()) {
                                *error = saveError;
                            }
                        }
                    }
                }

                lastLuminance = luminance;

                // Compare the last frame with the first to confirm the picture is
                // actually changing. This is what distinguishes a working virtual
                // camera from one that is stuck on a single frame.
                if (i == frameCount - 1 && !firstFrameBytes.empty()) {
                    BYTE* data = nullptr;
                    DWORD length = 0;
                    if (SUCCEEDED(buffer->Lock(&data, nullptr, &length))) {
                        const size_t compareLength =
                            length < firstFrameBytes.size() ? length : firstFrameBytes.size();
                        distinctFrames =
                            (::memcmp(data, firstFrameBytes.data(), compareLength) != 0);
                        buffer->Unlock();
                    }
                }

                buffer->Release();
            }

            lastSampleTime = static_cast<ULONGLONG>(timestamp);
            ++framesRead;
            sample->Release();
        }

        if (exitCode != 0) {
            break;
        }

        if (framesRead == 0) {
            *error = L"虚拟摄像头没有输出任何帧。设备存在，但媒体源没有产生样本。";
            exitCode = 8;
            break;
        }

        AppendReport(summary, framesRead, width, height,
                     firstLuminance, lastLuminance, distinctFrames,
                     !firstFrameBytes.empty(), firstSampleTime, lastSampleTime, outputPath);

        if (gaps > 0) {
            wchar_t line[160] = {};
            ::swprintf_s(line, L"\n       读取过程中有 %u 次没有就绪的样本（直播源的正常情况）。", gaps);
            summary->append(line);
        }

        // An all-black stream means the device exists but produces no usable image.
        // That is worth failing on: it is the difference between "the camera is
        // installed" and "the camera works".
        if (firstLuminance >= 0.0 && firstLuminance < 1.0 &&
            lastLuminance >= 0.0 && lastLuminance < 1.0) {
            summary->append(L"\n[警告] 所有帧都是全黑的：设备存在，但没有有效画面。");
            *error = L"虚拟摄像头输出的所有帧都是全黑的。";
            exitCode = 9;
        }
    } while (false);

    if (reader != nullptr) { reader->Release(); }
    if (source != nullptr) { source->Release(); }
    ReleaseDeviceList(devices, deviceCount);
    if (attributes != nullptr) { attributes->Release(); }

    ::MFShutdown();
    return exitCode;
}
