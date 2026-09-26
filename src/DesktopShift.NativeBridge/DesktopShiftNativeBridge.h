#pragma once

#include <guiddef.h>
#include <stdint.h>

#if defined(DESKTOPSHIFT_NATIVEBRIDGE_EXPORTS)
#define DESKTOPSHIFT_NATIVE_API __declspec(dllexport)
#else
#define DESKTOPSHIFT_NATIVE_API __declspec(dllimport)
#endif

extern "C"
{
    enum DesktopShiftNativeStage : uint32_t
    {
        DesktopShiftNativeStageNone = 0,
        DesktopShiftNativeStageBuildSelection = 1,
        DesktopShiftNativeStageComInitialization = 2,
        DesktopShiftNativeStageShellActivation = 3,
        DesktopShiftNativeStageManagerActivation = 4,
        DesktopShiftNativeStageEnumeration = 5,
        DesktopShiftNativeStageCurrentDesktop = 6,
        DesktopShiftNativeStageBehaviorValidation = 7,
        DesktopShiftNativeStageNotificationActivation = 8,
        DesktopShiftNativeStageNotificationRegistration = 9,
        DesktopShiftNativeStageDesktopCreation = 10,
        DesktopShiftNativeStageDesktopSwitch = 11,
        DesktopShiftNativeStageApplicationViewActivation = 12,
        DesktopShiftNativeStageWindowMove = 13,
        DesktopShiftNativeStageLayoutProbe = 14,
        DesktopShiftNativeStageDesktopRename = 15,
        DesktopShiftNativeStageApplicationViewLookup = 16,
        DesktopShiftNativeStageDesktopReorder = 17,
        DesktopShiftNativeStageWindowPin = 18,
    };

    enum DesktopShiftNativeTopologyReason : uint32_t
    {
        DesktopShiftNativeTopologyCreated = 1,
        DesktopShiftNativeTopologyDestroyed = 2,
        DesktopShiftNativeTopologyMoved = 3,
        DesktopShiftNativeTopologyNameChanged = 4,
        DesktopShiftNativeTopologyCurrentChanged = 5,
        DesktopShiftNativeTopologySwitched = 6,
        DesktopShiftNativeTopologyRemoteConnected = 7,
    };

    struct DesktopShiftNativeError
    {
        int32_t HResult;
        DesktopShiftNativeStage Stage;
        wchar_t Message[256];
    };

    struct DesktopShiftNativeDesktop
    {
        GUID Id;
        wchar_t DisplayName[260];
        uint32_t Position;
        int32_t IsCurrent;
    };

    using DesktopShiftTopologyCallback =
        void(__stdcall*)(DesktopShiftNativeTopologyReason reason, void* context);

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_CreateAdapter(
        uint32_t windowsBuild,
        void** adapter,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_ValidateAdapter(
        void* adapter,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_RefreshSnapshot(
        void* adapter,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_GetDesktopCount(
        void* adapter,
        uint32_t* count,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_GetDesktop(
        void* adapter,
        uint32_t index,
        DesktopShiftNativeDesktop* desktop,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_GetCurrentDesktopId(
        void* adapter,
        GUID* desktopId,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_CreateDesktop(
        void* adapter,
        GUID* desktopId,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_SwitchDesktop(
        void* adapter,
        const GUID* desktopId,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_MoveDesktop(
        void* adapter,
        const GUID* desktopId,
        uint32_t targetPosition,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_MoveWindowToDesktop(
        void* adapter,
        intptr_t windowHandle,
        const GUID* desktopId,
        DesktopShiftNativeError* error) noexcept;

    // Proves, without changing anything, that this build lays the manager
    // vtable out where DesktopShift believes it does. Resolves a desktop the
    // caller already enumerated through FindDesktop and checks the identity
    // that comes back. A build whose layout has shifted fails here instead of
    // discovering it during a rename.
    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_ProbeDesktopLookup(
        void* adapter,
        DesktopShiftNativeError* error) noexcept;

    // Naming and reordering are the only mutations here that target a desktop
    // rather than a window. Removing a desktop remains deliberately absent.
    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_SetDesktopName(
        void* adapter,
        const GUID* desktopId,
        const wchar_t* name,
        DesktopShiftNativeError* error) noexcept;

    // Pinning is the one operation here that changes what a window is shown on
    // rather than which desktop it lives on, so it is its own surface with its
    // own probe: a build whose pin slots were never admitted refuses these
    // before any pin slot is reached, and a refused pin costs pinning only.
    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_IsWindowPinned(
        void* adapter,
        intptr_t windowHandle,
        int32_t* isPinned,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_PinWindow(
        void* adapter,
        intptr_t windowHandle,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_UnpinWindow(
        void* adapter,
        intptr_t windowHandle,
        DesktopShiftNativeError* error) noexcept;

    // Proves, without changing anything, that this build lays the pinned-apps
    // surface out where DesktopShift believes it does. It queries the pin state
    // of a window the Shell already owns and reports what came back. A build
    // whose layout has shifted fails here instead of discovering it during a
    // pin, and the failure costs pinning while leaving every other operation in
    // Full Mode.
    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_ProbeWindowPin(
        void* adapter,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API int32_t __stdcall DesktopShiftNative_StartNotifications(
        void* adapter,
        DesktopShiftTopologyCallback callback,
        void* context,
        DesktopShiftNativeError* error) noexcept;

    DESKTOPSHIFT_NATIVE_API void __stdcall DesktopShiftNative_StopNotifications(
        void* adapter) noexcept;

    DESKTOPSHIFT_NATIVE_API void __stdcall DesktopShiftNative_DestroyAdapter(
        void* adapter) noexcept;
}
