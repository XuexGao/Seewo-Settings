// SeewoWrl.h
//
// Single include point for the Windows Runtime C++ Template Library (WRL).
//
// WRL is used instead of C++/WinRT and WIL so the media source depends only on
// the Windows SDK, with no NuGet packages to restore. That keeps
// `msbuild SeewoVirtualCamera.vcxproj` working on a clean machine.
//
// Two of WRL's headers are not clean at /W4 in a way this project cannot fix:
//
//   * C4324 - "structure was padded due to alignment specifier" - is raised by
//     WRL's own StaticStorage (wrl/module.h). The padding is deliberate on
//     Microsoft's side.
//
// The suppressions are applied here, around the includes, rather than as a
// project-wide DisableSpecificWarnings. That way they cover only the third-party
// headers and stay active for this project's own code, where such a warning
// would be worth seeing.
#pragma once

#pragma warning(push)
#pragma warning(disable : 4324)  // structure was padded due to alignment specifier

#include <wrl.h>
#include <wrl/client.h>
#include <wrl/implements.h>
#include <wrl/module.h>

#pragma warning(pop)
