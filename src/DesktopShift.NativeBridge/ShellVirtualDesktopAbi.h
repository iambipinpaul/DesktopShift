#pragma once

// This is the only file in DesktopShift that declares private Windows Shell
// virtual-desktop COM ABI. The declarations are intentionally build-scoped.

#include <inspectable.h>
#include <objectarray.h>
#include <servprov.h>
#include <winstring.h>
#include <wrl/client.h>

namespace DesktopShift::NativeBridge::ShellAbi
{
    inline constexpr GUID CLSID_ImmersiveShell = {
        0xC2F03A33, 0x21F5, 0x47FA, {0xB4, 0xBB, 0x15, 0x63, 0x62, 0xA2, 0xF2, 0x39}};
    inline constexpr GUID CLSID_VirtualDesktopManagerInternal = {
        0xC5E0CDCA, 0x7B6E, 0x41B2, {0x9F, 0xC4, 0xD9, 0x39, 0x75, 0xCC, 0x46, 0x7B}};
    inline constexpr GUID CLSID_VirtualDesktopNotificationService = {
        0xA501FDEC, 0x4A09, 0x464C, {0xAE, 0x4E, 0x1B, 0x9C, 0x21, 0xB8, 0x49, 0x18}};

    // DesktopShift only needs an application-view identity and the collection
    // prefix through GetViewForHwnd. Keeping the declarations prefix-only
    // avoids claiming knowledge of later private slots that can change between
    // Windows build families.
    MIDL_INTERFACE("372E1D3B-38D3-42E4-A15B-8AB2B178F513")
    IApplicationView : public IUnknown
    {
    };

    MIDL_INTERFACE("1841C6D7-4F9D-42C0-AF41-8747538F10E5")
    IApplicationViewCollection : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE GetViews(IObjectArray** views) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetViewsByZOrder(IObjectArray** views) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetViewsByAppUserModelId(
            PCWSTR appUserModelId,
            IObjectArray** views) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetViewForHwnd(
            HWND window,
            IApplicationView** view) = 0;
    };

    MIDL_INTERFACE("3F07F4BE-B107-441A-AF0F-39D82529072C")
    IVirtualDesktop24H2 : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE IsViewVisible(
            IApplicationView* view,
            BOOL* visible) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetId(GUID* id) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetName(HSTRING* name) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetWallpaperPath(HSTRING* path) = 0;
        virtual HRESULT STDMETHODCALLTYPE IsRemote(BOOL* remote) = 0;
    };

    MIDL_INTERFACE("53F5CA0B-158F-4124-900C-057158060B27")
    IVirtualDesktopManagerInternal24H2 : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE GetCount(UINT* count) = 0;
        virtual HRESULT STDMETHODCALLTYPE MoveViewToDesktop(
            IApplicationView* view,
            IVirtualDesktop24H2* desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE CanViewMoveDesktops(
            IApplicationView* view,
            BOOL* canMove) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetCurrentDesktop(IVirtualDesktop24H2** desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE GetDesktops(IObjectArray** desktops) = 0;
        // Keep this prefix in lockstep with Microsoft's build-family declaration:
        // https://github.com/microsoft/TypeAgent/blob/main/dotnet/autoShell/Services/WindowsVirtualDesktopService.cs
        virtual HRESULT STDMETHODCALLTYPE GetAdjacentDesktop(
            IVirtualDesktop24H2* from,
            int direction,
            IVirtualDesktop24H2** desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE SwitchDesktop(IVirtualDesktop24H2* desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE SwitchDesktopAndMoveForegroundView(
            IVirtualDesktop24H2* desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE CreateDesktop(IVirtualDesktop24H2** desktop) = 0;

        // Everything below exists for one reason: SetDesktopName is the
        // fourteenth slot, and a vtable cannot be entered halfway. Naming a
        // managed destination in Task View needs slot 14, so slots 10 to 13
        // have to be declared to place it, whether DesktopShift wants them or
        // not.
        //
        // That is a real cost and it is stated rather than hidden. This
        // declaration now claims knowledge of four slots further into private
        // territory than it used to, and slot 11 is RemoveDesktop. If a build
        // family shifts these, a SetDesktopName call lands on a neighbour, and
        // one of those neighbours deletes a desktop.
        //
        // Three things hold that risk down, and all three are required:
        //
        //   1. The AdapterProfile table in DesktopShiftNativeBridge.cpp gates
        //      naming per build. An unrecognized build never reaches any slot
        //      declared here, so the first call is never a blind one.
        //   2. Slots 10, 11 and 13 are declared with deliberately unusable
        //      signatures and DoNotCall names. They occupy the right offsets
        //      and nothing more; calling one does not compile by accident.
        //   3. FindDesktop at slot 12 is read-only and is called first, as a
        //      behavioural probe. If it does not return the desktop whose ID it
        //      was handed, the layout has moved and naming stays switched off.
        //
        // Removal and reordering are still never exported. See
        // DesktopTopologyNotificationContractTests, which asserts that.
        virtual HRESULT STDMETHODCALLTYPE Reserved_MoveDesktop_DoNotCall(
            void* desktop,
            int index) = 0;
        virtual HRESULT STDMETHODCALLTYPE Reserved_RemoveDesktop_DoNotCall(
            void* desktop,
            void* fallback) = 0;
        virtual HRESULT STDMETHODCALLTYPE FindDesktop(
            const GUID* desktopId,
            IVirtualDesktop24H2** desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE
            Reserved_GetDesktopSwitchIncludeExcludeViews_DoNotCall(
                void* desktop,
                void** included,
                void** excluded) = 0;
        virtual HRESULT STDMETHODCALLTYPE SetDesktopName(
            IVirtualDesktop24H2* desktop,
            HSTRING name) = 0;
    };

    MIDL_INTERFACE("B9E5E94D-233E-49AB-AF5C-2B4541C3AADE")
    IVirtualDesktopNotification24H2 : public IUnknown
    {
        // Shell revisions have exposed both per-monitor and global callback
        // shapes under this IID. DesktopShift's x64 sink deliberately accepts
        // the superset and never dereferences callback arguments. Extra x64
        // register/stack arguments are caller-owned, so this remains safe for
        // both shapes while preserving the common vtable slot ordering.
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopCreated(
            IObjectArray* monitors,
            IVirtualDesktop24H2* desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopDestroyBegin(
            IObjectArray* monitors,
            IVirtualDesktop24H2* destroyed,
            IVirtualDesktop24H2* fallback) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopDestroyFailed(
            IObjectArray* monitors,
            IVirtualDesktop24H2* destroyed,
            IVirtualDesktop24H2* fallback) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopDestroyed(
            IObjectArray* monitors,
            IVirtualDesktop24H2* destroyed,
            IVirtualDesktop24H2* fallback) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopMoved(
            IObjectArray* monitors,
            IVirtualDesktop24H2* desktop,
            INT64 oldIndex,
            INT64 newIndex) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopNameChanged(
            IObjectArray* monitors,
            IVirtualDesktop24H2* desktop,
            HSTRING name) = 0;
        virtual HRESULT STDMETHODCALLTYPE ViewVirtualDesktopChanged(
            IObjectArray* monitors,
            IUnknown* view) = 0;
        virtual HRESULT STDMETHODCALLTYPE CurrentVirtualDesktopChanged(
            IObjectArray* monitors,
            IVirtualDesktop24H2* oldDesktop,
            IVirtualDesktop24H2* newDesktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopWallpaperChanged(
            IObjectArray* monitors,
            IVirtualDesktop24H2* desktop,
            HSTRING path) = 0;
        virtual HRESULT STDMETHODCALLTYPE VirtualDesktopSwitched(
            IObjectArray* monitors,
            IVirtualDesktop24H2* desktop) = 0;
        virtual HRESULT STDMETHODCALLTYPE RemoteVirtualDesktopConnected(
            IObjectArray* monitors,
            IVirtualDesktop24H2* desktop) = 0;
    };

    MIDL_INTERFACE("0CD45E71-D927-4F15-8B0A-8FEF525337BF")
    IVirtualDesktopNotificationService24H2 : public IUnknown
    {
        virtual HRESULT STDMETHODCALLTYPE Register(
            IVirtualDesktopNotification24H2* notification,
            DWORD* cookie) = 0;
        virtual HRESULT STDMETHODCALLTYPE Unregister(DWORD cookie) = 0;
    };
}
