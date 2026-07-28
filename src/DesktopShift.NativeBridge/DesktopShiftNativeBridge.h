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
