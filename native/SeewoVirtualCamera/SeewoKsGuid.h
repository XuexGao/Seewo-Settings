// SeewoKsGuid.h
//
// Defines PINNAME_VIDEO_CAPTURE, the KSCATEGORY-style GUID that a FrameServer
// custom media source puts in MF_DEVICESTREAM_STREAM_CATEGORY to mark a stream as
// a colour video capture stream.
//
// Why this is defined here rather than included from <ksmedia.h>:
//
//   * ks.h, ksproxy.h and ksmedia.h are WDK headers, not plain Windows SDK
//     headers. They are not guaranteed to be present on a build machine that only
//     has the Windows SDK installed, which is exactly the situation on a GitHub
//     Actions windows-latest runner. Depending on them makes the build fragile for
//     no benefit.
//
//   * Pulling them in also breaks the build: cguid.h (reached through ks.h)
//     declares DEFINE_GUID in terms of __uuidof, and including it into a
//     translation unit that also uses Media Foundation's __uuidof-based code
//     produces "error C2059: syntax error: '__uuidof'".
//
// Only this one GUID is needed, so it is defined directly. The value is a
// published part of the Windows API surface and has never changed, so the DLL
// stays free of any KS header dependency while using the identical value that
// ksmedia.h would have supplied.
#pragma once

#include <guiddef.h>

// DECLSPEC_SELECTANY (__declspec(selectany)) lets every translation unit that
// includes this header carry its own definition; the linker merges them. That
// avoids needing an INITGUID arrangement plus a separate definitions file, which
// would be more moving parts than a single constant warrants.
EXTERN_C const GUID DECLSPEC_SELECTANY PINNAME_VIDEO_CAPTURE =
    {0x65E8773D, 0x8F56, 0x11D0, {0xA3, 0xB9, 0x00, 0xA0, 0xC9, 0x22, 0x31, 0x96}};
