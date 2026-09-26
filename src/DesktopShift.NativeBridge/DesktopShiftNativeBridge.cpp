#define DESKTOPSHIFT_NATIVEBRIDGE_EXPORTS

#include "DesktopShiftNativeBridge.h"
#include "ShellVirtualDesktopAbi.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <cwchar>
#include <deque>
#include <functional>
#include <future>
#include <iterator>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <type_traits>
#include <utility>
#include <vector>
#include <windows.h>

using Microsoft::WRL::ComPtr;
using namespace DesktopShift::NativeBridge::ShellAbi;

namespace
{
    constexpr HRESULT HrAdapterNotValidated =
        MAKE_HRESULT(SEVERITY_ERROR, FACILITY_ITF, 0x201);
    constexpr HRESULT HrContractMismatch =
        MAKE_HRESULT(SEVERITY_ERROR, FACILITY_ITF, 0x202);

    struct AdapterProfile
    {
        uint32_t Build;
        bool Validated;
        // Whether this build family may reach the manager slots past
        // CreateDesktop. Tracked separately from Validated because the two rest
        // on different evidence: the read-only prefix has been exercised on
        // real machines, whereas slots 10 to 14 are documented by published
        // reverse-engineering and proved at runtime by the FindDesktop probe.
        // A family only earns this once its layout is confirmed; unconfirmed
        // families still enumerate, create, switch and move as before.
        bool ExtendedLayoutValidated;
        // Whether this build family may reach the view-pin slots of the
        // pinned-apps surface. Pinning is a second private interface rather
        // than a slot in the manager, so it is admitted on its own evidence:
        // it is proved at runtime by the read-only pin probe, and a family that
        // has not been confirmed here still enumerates, creates, switches and
        // moves exactly as before while reporting pinning as unavailable.
        bool PinnedAppsValidated;
    };

    constexpr AdapterProfile Profiles[] = {
        {22631, false, false, false},
        {26100, true, true, true},
        {26200, true, true, true},
        {28000, false, false, false},
    };

    void ClearError(DesktopShiftNativeError* error) noexcept
    {
        if (error != nullptr)
        {
            error->HResult = S_OK;
            error->Stage = DesktopShiftNativeStageNone;
            error->Message[0] = L'\0';
        }
    }

    HRESULT SetError(
        DesktopShiftNativeError* error,
        HRESULT result,
        DesktopShiftNativeStage stage,
        const wchar_t* message) noexcept
    {
        if (error != nullptr)
        {
            error->HResult = result;
            error->Stage = stage;
            if (message == nullptr)
            {
                error->Message[0] = L'\0';
            }
            else
            {
                static_cast<void>(wcsncpy_s(error->Message, message, _TRUNCATE));
            }
        }

        return result;
    }

    HRESULT SetUnexpectedError(
        DesktopShiftNativeError* error,
        DesktopShiftNativeStage stage = DesktopShiftNativeStageBehaviorValidation) noexcept
    {
        return SetError(
            error,
            E_UNEXPECTED,
            stage,
            L"The native virtual-desktop bridge encountered an unexpected failure.");
    }

    const AdapterProfile* SelectProfile(uint32_t build) noexcept
    {
        const auto found = std::find_if(
            std::begin(Profiles),
            std::end(Profiles),
            [build](const AdapterProfile& profile)
            {
                return profile.Build == build;
            });
        return found == std::end(Profiles) ? nullptr : found;
    }

    class NotificationSink final : public IVirtualDesktopNotification24H2
    {
    public:
        NotificationSink(
            DesktopShiftTopologyCallback callback,
            void* context) noexcept
            : callback_(callback),
              context_(context)
        {
        }

        HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) noexcept override
        {
            if (value == nullptr)
            {
                return E_POINTER;
            }

            if (iid == IID_IUnknown || iid == __uuidof(IVirtualDesktopNotification24H2))
            {
                *value = static_cast<IVirtualDesktopNotification24H2*>(this);
                AddRef();
                return S_OK;
            }

            *value = nullptr;
            return E_NOINTERFACE;
        }

        ULONG STDMETHODCALLTYPE AddRef() noexcept override
        {
            return ++references_;
        }

        ULONG STDMETHODCALLTYPE Release() noexcept override
        {
            const ULONG remaining = --references_;
            if (remaining == 0)
            {
                delete this;
            }
            return remaining;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopCreated(
            IObjectArray*,
            IVirtualDesktop24H2*) noexcept override
        {
            Notify(DesktopShiftNativeTopologyCreated);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopDestroyBegin(
            IObjectArray*,
            IVirtualDesktop24H2*,
            IVirtualDesktop24H2*) noexcept override
        {
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopDestroyFailed(
            IObjectArray*,
            IVirtualDesktop24H2*,
            IVirtualDesktop24H2*) noexcept override
        {
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopDestroyed(
            IObjectArray*,
            IVirtualDesktop24H2*,
            IVirtualDesktop24H2*) noexcept override
        {
            Notify(DesktopShiftNativeTopologyDestroyed);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopMoved(
            IObjectArray*,
            IVirtualDesktop24H2*,
            INT64,
            INT64) noexcept override
        {
            Notify(DesktopShiftNativeTopologyMoved);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopNameChanged(
            IObjectArray*,
            IVirtualDesktop24H2*,
            HSTRING) noexcept override
        {
            Notify(DesktopShiftNativeTopologyNameChanged);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE ViewVirtualDesktopChanged(
            IObjectArray*,
            IUnknown*) noexcept override
        {
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE CurrentVirtualDesktopChanged(
            IObjectArray*,
            IVirtualDesktop24H2*,
            IVirtualDesktop24H2*) noexcept override
        {
            Notify(DesktopShiftNativeTopologyCurrentChanged);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopWallpaperChanged(
            IObjectArray*,
            IVirtualDesktop24H2*,
            HSTRING) noexcept override
        {
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE VirtualDesktopSwitched(
            IObjectArray*,
            IVirtualDesktop24H2*) noexcept override
        {
            Notify(DesktopShiftNativeTopologySwitched);
            return S_OK;
        }

        HRESULT STDMETHODCALLTYPE RemoteVirtualDesktopConnected(
            IObjectArray*,
            IVirtualDesktop24H2*) noexcept override
        {
            Notify(DesktopShiftNativeTopologyRemoteConnected);
            return S_OK;
        }

    private:
        ~NotificationSink() = default;

        void Notify(DesktopShiftNativeTopologyReason reason) const noexcept
        {
            if (callback_ != nullptr)
            {
                callback_(reason, context_);
            }
        }

        std::atomic<ULONG> references_{1};
        DesktopShiftTopologyCallback callback_;
        void* context_;
    };

    class NativeAdapter final
    {
    public:
        explicit NativeAdapter(
            bool extendedLayoutValidated,
            bool pinnedAppsValidated)
            : workEvent_(CreateEventW(nullptr, FALSE, FALSE, nullptr)),
              extendedLayoutValidated_(extendedLayoutValidated),
              pinnedAppsValidated_(pinnedAppsValidated)
        {
            if (workEvent_ == nullptr)
            {
                throw std::bad_alloc();
            }

            worker_ = std::thread([this]()
            {
                ThreadMain();
            });
        }

        NativeAdapter(const NativeAdapter&) = delete;
        NativeAdapter& operator=(const NativeAdapter&) = delete;

        ~NativeAdapter()
        {
            try
            {
                Invoke([this]()
                {
                    StopNotificationsCore();
                    notificationService_.Reset();
                    pinnedApps_.Reset();
                    applicationViews_.Reset();
                    manager_.Reset();
                    shell_.Reset();
                });
            }
            catch (...)
            {
            }

            stopping_.store(true);
            SetEvent(workEvent_);
            if (worker_.joinable())
            {
                worker_.join();
            }
            CloseHandle(workEvent_);
        }

        HRESULT Activate(DesktopShiftNativeError* error)
        {
            return Invoke([this, error]()
            {
                if (FAILED(comInitializationResult_))
                {
                    return SetError(
                        error,
                        comInitializationResult_,
                        DesktopShiftNativeStageComInitialization,
                        L"COM initialization failed on the native bridge worker.");
                }

                HRESULT result = CoCreateInstance(
                    CLSID_ImmersiveShell,
                    nullptr,
                    CLSCTX_LOCAL_SERVER,
                    IID_PPV_ARGS(shell_.ReleaseAndGetAddressOf()));
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageShellActivation,
                        L"Windows Shell service activation failed.");
                }

                result = shell_->QueryService(
                    CLSID_VirtualDesktopManagerInternal,
                    __uuidof(IVirtualDesktopManagerInternal24H2),
                    reinterpret_cast<void**>(manager_.ReleaseAndGetAddressOf()));
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageManagerActivation,
                        L"The build-specific virtual-desktop manager interface was unavailable.");
                }

                result = shell_->QueryService(
                    __uuidof(IApplicationViewCollection),
                    __uuidof(IApplicationViewCollection),
                    reinterpret_cast<void**>(applicationViews_.ReleaseAndGetAddressOf()));
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageApplicationViewActivation,
                        L"The build-specific application-view collection was unavailable.");
                }

                ClearError(error);
                return S_OK;
            });
        }

        HRESULT Validate(DesktopShiftNativeError* error)
        {
            return Invoke([this, error]()
            {
                behaviorValidated_ = false;
                HRESULT result = ReadSnapshot(error);
                if (FAILED(result))
                {
                    return result;
                }

                result = EnsureNotificationService(error);
                if (FAILED(result))
                {
                    return result;
                }

                ComPtr<NotificationSink> probe;
                probe.Attach(new (std::nothrow) NotificationSink(nullptr, nullptr));
                if (probe == nullptr)
                {
                    return SetError(
                        error,
                        E_OUTOFMEMORY,
                        DesktopShiftNativeStageNotificationRegistration,
                        L"Could not allocate the harmless notification-registration probe.");
                }

                DWORD cookie = 0;
                result = notificationService_->Register(probe.Get(), &cookie);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageNotificationRegistration,
                        L"Virtual-desktop notification registration validation failed.");
                }

                const HRESULT unregisterResult = notificationService_->Unregister(cookie);
                if (FAILED(unregisterResult))
                {
                    return SetError(
                        error,
                        unregisterResult,
                        DesktopShiftNativeStageNotificationRegistration,
                        L"Virtual-desktop notification unregistration validation failed.");
                }

                behaviorValidated_ = true;
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT RefreshSnapshot(DesktopShiftNativeError* error)
        {
            return Invoke([this, error]()
            {
                return ReadSnapshot(error);
            });
        }

        HRESULT GetDesktopCount(
            uint32_t* count,
            DesktopShiftNativeError* error)
        {
            if (count == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageEnumeration,
                    L"A desktop-count output pointer was not provided.");
            }

            return Invoke([this, count, error]()
            {
                *count = static_cast<uint32_t>(snapshot_.size());
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT GetDesktop(
            uint32_t index,
            DesktopShiftNativeDesktop* desktop,
            DesktopShiftNativeError* error)
        {
            if (desktop == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageEnumeration,
                    L"A desktop output pointer was not provided.");
            }

            return Invoke([this, index, desktop, error]()
            {
                if (index >= snapshot_.size())
                {
                    return SetError(
                        error,
                        E_BOUNDS,
                        DesktopShiftNativeStageEnumeration,
                        L"The requested desktop index is outside the validated snapshot.");
                }

                *desktop = snapshot_[index];
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT GetCurrentDesktopId(
            GUID* id,
            DesktopShiftNativeError* error)
        {
            if (id == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageCurrentDesktop,
                    L"A current-desktop output pointer was not provided.");
            }

            return Invoke([this, id, error]()
            {
                if (currentDesktop_ == GUID_NULL)
                {
                    return SetError(
                        error,
                        HrContractMismatch,
                        DesktopShiftNativeStageCurrentDesktop,
                        L"The native bridge has no validated current desktop.");
                }

                *id = currentDesktop_;
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT CreateDesktop(
            GUID* id,
            DesktopShiftNativeError* error)
        {
            if (id == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageDesktopCreation,
                    L"A created-desktop output pointer was not provided.");
            }
            *id = GUID_NULL;

            return Invoke([this, id, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageDesktopCreation,
                        L"Desktop creation is disabled until harmless adapter validation succeeds.");
                }

                HRESULT result = ReadSnapshot(error);
                if (FAILED(result))
                {
                    return result;
                }

                ComPtr<IVirtualDesktop24H2> created;
                result = manager_->CreateDesktop(created.ReleaseAndGetAddressOf());
                if (FAILED(result) || created == nullptr)
                {
                    return SetError(
                        error,
                        FAILED(result) ? result : HrContractMismatch,
                        DesktopShiftNativeStageDesktopCreation,
                        L"The Windows Shell did not create a virtual desktop.");
                }

                GUID createdId{};
                result = created->GetId(&createdId);
                if (FAILED(result) || createdId == GUID_NULL)
                {
                    return SetError(
                        error,
                        FAILED(result) ? result : HrContractMismatch,
                        DesktopShiftNativeStageDesktopCreation,
                        L"The created virtual desktop returned an invalid identifier.");
                }

                // Creation is committed once Shell returns a desktop with a valid
                // identifier. Return it immediately: post-commit verification
                // could erase a successful mutation and make callers retry,
                // creating a duplicate. Notifications or the next explicit read
                // refresh the cached inventory.
                *id = createdId;
                ClearError(error);
                return S_OK;
            });
        }

        /// Proves the manager vtable is laid out where this build family says
        /// it is, without changing anything.
        ///
        /// FindDesktop is slot 12 and is a pure lookup, so a wrong answer costs
        /// nothing while a wrong *slot* would. It is reached only after the
        /// per-build naming gate, so an unrecognized family never calls into
        /// this region at all — the ordering is the safety, not the probe on
        /// its own.
        HRESULT ProbeDesktopLookup(DesktopShiftNativeError* error)
        {
            return Invoke([this, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageLayoutProbe,
                        L"The vtable layout probe is disabled until harmless adapter validation succeeds.");
                }

                if (!extendedLayoutValidated_)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                        DesktopShiftNativeStageLayoutProbe,
                        L"This Windows build family is not cleared for the manager slots past desktop creation.");
                }

                HRESULT result = ReadSnapshot(error);
                if (FAILED(result))
                {
                    return result;
                }

                // The current desktop is the safest possible subject: validation
                // already proved it exists and that its identity round-trips.
                const GUID knownId = currentDesktop_;
                if (knownId == GUID_NULL)
                {
                    return SetError(
                        error,
                        HrContractMismatch,
                        DesktopShiftNativeStageLayoutProbe,
                        L"The layout probe had no validated desktop to look up.");
                }

                ComPtr<IVirtualDesktop24H2> found;
                result = manager_->FindDesktop(
                    &knownId,
                    found.ReleaseAndGetAddressOf());
                if (FAILED(result) || found == nullptr)
                {
                    return SetError(
                        error,
                        FAILED(result) ? result : HrContractMismatch,
                        DesktopShiftNativeStageLayoutProbe,
                        L"Desktop lookup did not answer, so the manager vtable layout is not the one this build expects.");
                }

                GUID foundId{};
                result = found->GetId(&foundId);
                if (FAILED(result) || foundId != knownId)
                {
                    return SetError(
                        error,
                        FAILED(result) ? result : HrContractMismatch,
                        DesktopShiftNativeStageLayoutProbe,
                        L"Desktop lookup returned a different desktop than it was asked for, so the manager vtable layout has moved.");
                }

                layoutProbed_ = true;
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT MoveDesktop(
            const GUID& id,
            uint32_t targetPosition,
            DesktopShiftNativeError* error)
        {
            if (id == GUID_NULL)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageDesktopReorder,
                    L"A non-empty target desktop identifier is required.");
            }

            return Invoke([this, id, targetPosition, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageDesktopReorder,
                        L"Desktop reordering is disabled until harmless adapter validation succeeds.");
                }

                if (!extendedLayoutValidated_ || !layoutProbed_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageDesktopReorder,
                        L"Desktop reordering is disabled until the extended vtable layout is proved.");
                }

                HRESULT result = ReadSnapshot(error);
                if (FAILED(result))
                {
                    return result;
                }

                if (targetPosition >= snapshot_.size())
                {
                    return SetError(
                        error,
                        E_INVALIDARG,
                        DesktopShiftNativeStageDesktopReorder,
                        L"The requested Task View position is outside the desktop inventory.");
                }

                const auto known = std::find_if(
                    snapshot_.begin(),
                    snapshot_.end(),
                    [&id](const DesktopShiftNativeDesktop& desktop)
                    {
                        return desktop.Id == id;
                    });
                if (known == snapshot_.end())
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageDesktopReorder,
                        L"The requested desktop was not present in the validated inventory.");
                }

                if (known->Position == targetPosition)
                {
                    ClearError(error);
                    return S_OK;
                }

                ComPtr<IObjectArray> desktops;
                result = manager_->GetDesktops(desktops.ReleaseAndGetAddressOf());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopReorder,
                        L"The desktop inventory could not be resolved for reordering.");
                }

                UINT count = 0;
                result = desktops->GetCount(&count);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopReorder,
                        L"The desktop inventory could not be counted for reordering.");
                }

                ComPtr<IVirtualDesktop24H2> target;
                for (UINT index = 0; index < count; ++index)
                {
                    ComPtr<IVirtualDesktop24H2> candidate;
                    result = desktops->GetAt(
                        index,
                        __uuidof(IVirtualDesktop24H2),
                        reinterpret_cast<void**>(candidate.ReleaseAndGetAddressOf()));
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageDesktopReorder,
                            L"A desktop entry could not be resolved for reordering.");
                    }

                    GUID candidateId{};
                    result = candidate->GetId(&candidateId);
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageDesktopReorder,
                            L"A desktop entry did not return its identifier for reordering.");
                    }

                    if (candidateId == id)
                    {
                        target = std::move(candidate);
                        break;
                    }
                }

                if (target == nullptr)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageDesktopReorder,
                        L"The requested desktop changed before it could be reordered.");
                }

                result = manager_->MoveDesktop(
                    target.Get(),
                    static_cast<int>(targetPosition));
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopReorder,
                        L"The Windows Shell rejected the virtual-desktop reorder.");
                }

                // A fresh read verifies Shell's committed ordering. Patching the
                // cache here would only report what the caller requested.
                ClearError(error);
                return S_OK;
            });
        }

        /// Names an existing desktop. Reordering is the only other mutation in
        /// this bridge that targets a desktop rather than a window.
        ///
        /// The target is resolved by walking GetDesktops rather than through
        /// FindDesktop, so naming leans on the enumeration path that every
        /// other operation already uses. FindDesktop earns its keep as the
        /// probe, not as a shortcut here.
        HRESULT SetDesktopName(
            const GUID& id,
            const wchar_t* name,
            DesktopShiftNativeError* error)
        {
            if (id == GUID_NULL)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageDesktopRename,
                    L"A non-empty target desktop identifier is required.");
            }

            if (name == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageDesktopRename,
                    L"A desktop name was not provided.");
            }

            std::wstring requestedName(name);

            return Invoke([this, id, requestedName, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageDesktopRename,
                        L"Desktop naming is disabled until harmless adapter validation succeeds.");
                }

                if (!extendedLayoutValidated_)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                        DesktopShiftNativeStageDesktopRename,
                        L"This Windows build family is not cleared for desktop naming.");
                }

                if (!layoutProbed_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageDesktopRename,
                        L"Desktop naming is disabled until the vtable layout probe has succeeded.");
                }

                HRESULT result = ReadSnapshot(error);
                if (FAILED(result))
                {
                    return result;
                }

                const bool isKnown = std::any_of(
                    snapshot_.begin(),
                    snapshot_.end(),
                    [&id](const DesktopShiftNativeDesktop& desktop)
                    {
                        return desktop.Id == id;
                    });
                if (!isKnown)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageDesktopRename,
                        L"The requested desktop was not present in the validated inventory.");
                }

                ComPtr<IObjectArray> desktops;
                result = manager_->GetDesktops(desktops.ReleaseAndGetAddressOf());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopRename,
                        L"The desktop inventory could not be resolved for naming.");
                }

                UINT count = 0;
                result = desktops->GetCount(&count);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopRename,
                        L"The desktop inventory could not be counted for naming.");
                }

                ComPtr<IVirtualDesktop24H2> target;
                for (UINT index = 0; index < count; ++index)
                {
                    ComPtr<IVirtualDesktop24H2> candidate;
                    result = desktops->GetAt(
                        index,
                        __uuidof(IVirtualDesktop24H2),
                        reinterpret_cast<void**>(candidate.ReleaseAndGetAddressOf()));
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageDesktopRename,
                            L"A desktop entry could not be resolved for naming.");
                    }

                    GUID candidateId{};
                    result = candidate->GetId(&candidateId);
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageDesktopRename,
                            L"A desktop entry did not return its identifier for naming.");
                    }

                    if (candidateId == id)
                    {
                        target = std::move(candidate);
                        break;
                    }
                }

                if (target == nullptr)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageDesktopRename,
                        L"The requested desktop changed before it could be named.");
                }

                HSTRING nameHandle = nullptr;
                result = WindowsCreateString(
                    requestedName.c_str(),
                    static_cast<UINT32>(requestedName.size()),
                    &nameHandle);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopRename,
                        L"The desktop name could not be marshalled for the Windows Shell.");
                }

                result = manager_->SetDesktopName(target.Get(), nameHandle);
                static_cast<void>(WindowsDeleteString(nameHandle));
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopRename,
                        L"The Windows Shell rejected the virtual-desktop name.");
                }

                // The cached snapshot deliberately is not patched here. The
                // caller reads the name back through a fresh snapshot to find
                // out what Windows actually stored, and a locally patched cache
                // would tell it what it hoped for instead.
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT SwitchDesktop(
            const GUID& id,
            DesktopShiftNativeError* error)
        {
            if (id == GUID_NULL)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageDesktopSwitch,
                    L"A non-empty target desktop identifier is required.");
            }

            return Invoke([this, id, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageDesktopSwitch,
                        L"Desktop switching is disabled until harmless adapter validation succeeds.");
                }

                HRESULT result = ReadSnapshot(error);
                if (FAILED(result))
                {
                    return result;
                }

                const bool isKnown = std::any_of(
                    snapshot_.begin(),
                    snapshot_.end(),
                    [&id](const DesktopShiftNativeDesktop& desktop)
                    {
                        return desktop.Id == id;
                    });
                if (!isKnown)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageDesktopSwitch,
                        L"The requested desktop was not present in the validated inventory.");
                }

                if (currentDesktop_ == id)
                {
                    ClearError(error);
                    return S_OK;
                }

                ComPtr<IObjectArray> desktops;
                result = manager_->GetDesktops(desktops.ReleaseAndGetAddressOf());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopSwitch,
                        L"The desktop inventory could not be resolved for switching.");
                }

                UINT count = 0;
                result = desktops->GetCount(&count);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopSwitch,
                        L"The desktop inventory could not be counted for switching.");
                }

                ComPtr<IVirtualDesktop24H2> target;
                for (UINT index = 0; index < count; ++index)
                {
                    ComPtr<IVirtualDesktop24H2> candidate;
                    result = desktops->GetAt(
                        index,
                        __uuidof(IVirtualDesktop24H2),
                        reinterpret_cast<void**>(candidate.ReleaseAndGetAddressOf()));
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageDesktopSwitch,
                            L"A desktop entry could not be resolved for switching.");
                    }

                    GUID candidateId{};
                    result = candidate->GetId(&candidateId);
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageDesktopSwitch,
                            L"A desktop entry did not return its identifier for switching.");
                    }

                    if (candidateId == id)
                    {
                        target = std::move(candidate);
                        break;
                    }
                }

                if (target == nullptr)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageDesktopSwitch,
                        L"The requested desktop changed before it could be switched.");
                }

                result = manager_->SwitchDesktop(target.Get());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageDesktopSwitch,
                        L"The Windows Shell rejected the virtual-desktop switch.");
                }

                // Switching is committed after the Shell call succeeds. Update
                // the cached marker without adding post-commit failure paths.
                currentDesktop_ = id;
                for (DesktopShiftNativeDesktop& desktop : snapshot_)
                {
                    desktop.IsCurrent = desktop.Id == id ? TRUE : FALSE;
                }

                ClearError(error);
                return S_OK;
            });
        }

        HRESULT MoveWindowToDesktop(
            HWND window,
            const GUID& id,
            DesktopShiftNativeError* error)
        {
            if (window == nullptr)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageWindowMove,
                    L"A nonzero top-level window handle is required.");
            }

            if (id == GUID_NULL)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageWindowMove,
                    L"A non-empty target desktop identifier is required.");
            }

            return Invoke([this, window, id, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageWindowMove,
                        L"Window movement is disabled until harmless adapter validation succeeds.");
                }

                if (!IsWindow(window))
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE),
                        DesktopShiftNativeStageWindowMove,
                        L"The top-level window handle is no longer live.");
                }

                const HWND root = GetAncestor(window, GA_ROOT);
                if (root == nullptr || !IsWindow(root))
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE),
                        DesktopShiftNativeStageWindowMove,
                        L"The top-level window handle is no longer live.");
                }

                if (root != window)
                {
                    return SetError(
                        error,
                        E_INVALIDARG,
                        DesktopShiftNativeStageWindowMove,
                        L"The window handle does not identify a top-level window.");
                }

                if (applicationViews_ == nullptr)
                {
                    return SetError(
                        error,
                        E_UNEXPECTED,
                        DesktopShiftNativeStageApplicationViewActivation,
                        L"The build-specific application-view collection is not active.");
                }

                ComPtr<IObjectArray> desktops;
                HRESULT result =
                    manager_->GetDesktops(desktops.ReleaseAndGetAddressOf());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowMove,
                        L"The desktop inventory could not be resolved for moving the window.");
                }

                UINT count = 0;
                result = desktops->GetCount(&count);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowMove,
                        L"The desktop inventory could not be counted for moving the window.");
                }

                ComPtr<IVirtualDesktop24H2> target;
                for (UINT index = 0; index < count; ++index)
                {
                    ComPtr<IVirtualDesktop24H2> candidate;
                    result = desktops->GetAt(
                        index,
                        __uuidof(IVirtualDesktop24H2),
                        reinterpret_cast<void**>(
                            candidate.ReleaseAndGetAddressOf()));
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageWindowMove,
                            L"A desktop entry could not be resolved for moving the window.");
                    }

                    GUID candidateId{};
                    result = candidate->GetId(&candidateId);
                    if (FAILED(result))
                    {
                        return SetError(
                            error,
                            result,
                            DesktopShiftNativeStageWindowMove,
                            L"A desktop entry did not return its identifier for moving the window.");
                    }

                    if (candidateId == id)
                    {
                        target = std::move(candidate);
                        break;
                    }
                }

                if (target == nullptr)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_FOUND),
                        DesktopShiftNativeStageWindowMove,
                        L"The requested desktop was not present in the current inventory.");
                }

                ComPtr<IApplicationView> view;
                result = ResolveApplicationView(window, view, error);
                if (FAILED(result))
                {
                    // Its own stage: a window the Shell has not registered yet
                    // fails here, and a caller reads that as "not yet" rather
                    // than "refused".
                    return result;
                }

                BOOL canMove = FALSE;
                result = manager_->CanViewMoveDesktops(view.Get(), &canMove);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowMove,
                        L"The Windows Shell could not determine whether the window can move.");
                }

                if (!canMove)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                        DesktopShiftNativeStageWindowMove,
                        L"The Windows Shell reports that this window cannot move between desktops.");
                }

                result = manager_->MoveViewToDesktop(view.Get(), target.Get());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowMove,
                        L"The Windows Shell rejected the application-view move.");
                }

                // Shell has committed the move. Return immediately rather than
                // adding a post-commit failure path that could invite a retry.
                ClearError(error);
                return S_OK;
            });
        }

        /// Whether the Shell currently shows this window on every desktop.
        ///
        /// The read-only half of the pin surface, gated exactly like the
        /// mutating half: it is asked only on a build whose pin surface was
        /// admitted and proved. The probe reaches IsViewPinned itself, one
        /// level below this, and that is the only use of the slot that does not
        /// depend on the probe having already answered.
        HRESULT IsWindowPinned(
            HWND window,
            int32_t* isPinned,
            DesktopShiftNativeError* error)
        {
            if (isPinned == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageWindowPin,
                    L"A pin-state output pointer was not provided.");
            }
            *isPinned = 0;

            if (window == nullptr)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageWindowPin,
                    L"A nonzero top-level window handle is required.");
            }

            return Invoke([this, window, isPinned, error]()
            {
                HRESULT result = ValidatePinRequest(error);
                if (FAILED(result))
                {
                    return result;
                }

                result = ValidatePinWindow(window, error);
                if (FAILED(result))
                {
                    return result;
                }

                ComPtr<IApplicationView> view;
                result = ResolveApplicationView(window, view, error);
                if (FAILED(result))
                {
                    return result;
                }

                BOOL pinned = FALSE;
                result = pinnedApps_->IsViewPinned(view.Get(), &pinned);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowPin,
                        L"The Windows Shell could not report whether the window is shown on every virtual desktop.");
                }

                *isPinned = pinned ? 1 : 0;
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT PinWindow(HWND window, DesktopShiftNativeError* error)
        {
            return SetWindowPinned(window, true, error);
        }

        HRESULT UnpinWindow(HWND window, DesktopShiftNativeError* error)
        {
            return SetWindowPinned(window, false, error);
        }

        /// Proves the pinned-apps surface is laid out where this build family
        /// says it is, without changing anything.
        ///
        /// IsViewPinned is a pure query, so a wrong answer costs nothing while a
        /// wrong *slot* would: the three slots ahead of the view trio are app-id
        /// pins, and reaching past them on a build whose layout has moved would
        /// pin the wrong thing. The subject is a window the Shell already owns —
        /// whatever is foreground now, or the Shell's own window when nothing is
        /// focused — so the probe needs nothing a caller has to set up.
        HRESULT ProbeWindowPin(DesktopShiftNativeError* error)
        {
            return Invoke([this, error]()
            {
                if (!behaviorValidated_)
                {
                    return SetError(
                        error,
                        HrAdapterNotValidated,
                        DesktopShiftNativeStageWindowPin,
                        L"The pin surface probe is disabled until harmless adapter validation succeeds.");
                }

                if (!pinnedAppsValidated_)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                        DesktopShiftNativeStageWindowPin,
                        L"This Windows build family is not cleared for the window pin surface.");
                }

                HRESULT result = EnsurePinnedApps(error);
                if (FAILED(result))
                {
                    return result;
                }

                // Nothing is chosen about the window beyond it being one the
                // Shell tracks: the question is whether the surface answers at
                // all, not what it answers.
                HWND subject = GetForegroundWindow();
                if (subject == nullptr)
                {
                    subject = GetShellWindow();
                }

                if (subject == nullptr)
                {
                    return SetError(
                        error,
                        HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE),
                        DesktopShiftNativeStageWindowPin,
                        L"The pin surface probe found no window it could ask about.");
                }

                ComPtr<IApplicationView> view;
                result = ResolveApplicationView(subject, view, error);
                if (FAILED(result))
                {
                    return result;
                }

                BOOL pinned = FALSE;
                result = pinnedApps_->IsViewPinned(view.Get(), &pinned);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowPin,
                        L"The pin surface did not answer its first question, so this build does not lay the pinned-apps vtable out where DesktopShift expects.");
                }

                pinProbed_ = true;
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT StartNotifications(
            DesktopShiftTopologyCallback callback,
            void* context,
            DesktopShiftNativeError* error)
        {
            if (callback == nullptr)
            {
                return SetError(
                    error,
                    E_POINTER,
                    DesktopShiftNativeStageNotificationRegistration,
                    L"A topology notification callback was not provided.");
            }

            return Invoke([this, callback, context, error]()
            {
                if (notificationCookie_ != 0)
                {
                    ClearError(error);
                    return S_OK;
                }

                HRESULT result = EnsureNotificationService(error);
                if (FAILED(result))
                {
                    return result;
                }

                notificationSink_.Attach(
                    new (std::nothrow) NotificationSink(callback, context));
                if (notificationSink_ == nullptr)
                {
                    return SetError(
                        error,
                        E_OUTOFMEMORY,
                        DesktopShiftNativeStageNotificationRegistration,
                        L"Could not allocate the topology notification sink.");
                }

                result = notificationService_->Register(
                    notificationSink_.Get(),
                    &notificationCookie_);
                if (FAILED(result))
                {
                    notificationSink_.Reset();
                    notificationCookie_ = 0;
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageNotificationRegistration,
                        L"Virtual-desktop topology notification registration failed.");
                }

                ClearError(error);
                return S_OK;
            });
        }

        void StopNotifications() noexcept
        {
            try
            {
                Invoke([this]()
                {
                    StopNotificationsCore();
                });
            }
            catch (...)
            {
            }
        }

    private:
        template<typename Function>
        auto Invoke(Function&& function) -> std::invoke_result_t<Function>
        {
            using Result = std::invoke_result_t<Function>;
            if (GetCurrentThreadId() == workerThreadId_.load())
            {
                if constexpr (std::is_void_v<Result>)
                {
                    std::invoke(std::forward<Function>(function));
                    return;
                }
                else
                {
                    return std::invoke(std::forward<Function>(function));
                }
            }

            auto task = std::make_shared<std::packaged_task<Result()>>(
                std::forward<Function>(function));
            std::future<Result> result = task->get_future();
            {
                std::lock_guard lock(workMutex_);
                work_.emplace_back([task]()
                {
                    (*task)();
                });
            }
            SetEvent(workEvent_);

            if constexpr (std::is_void_v<Result>)
            {
                result.get();
                return;
            }
            else
            {
                return result.get();
            }
        }

        void ThreadMain() noexcept
        {
            workerThreadId_.store(GetCurrentThreadId());
            comInitializationResult_ = CoInitializeEx(
                nullptr,
                COINIT_APARTMENTTHREADED | COINIT_DISABLE_OLE1DDE);
            const bool shouldUninitialize = SUCCEEDED(comInitializationResult_);

            MSG message{};
            static_cast<void>(PeekMessageW(&message, nullptr, 0, 0, PM_NOREMOVE));

            while (!stopping_.load())
            {
                const DWORD waitResult = MsgWaitForMultipleObjects(
                    1,
                    &workEvent_,
                    FALSE,
                    INFINITE,
                    QS_ALLINPUT);

                if (waitResult == WAIT_OBJECT_0)
                {
                    DrainWork();
                }
                else if (waitResult == WAIT_OBJECT_0 + 1)
                {
                    while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
                    {
                        TranslateMessage(&message);
                        DispatchMessageW(&message);
                    }
                }
                else
                {
                    break;
                }
            }

            DrainWork();
            if (shouldUninitialize)
            {
                CoUninitialize();
            }
        }

        void DrainWork()
        {
            std::deque<std::function<void()>> pending;
            {
                std::lock_guard lock(workMutex_);
                pending.swap(work_);
            }

            for (auto& action : pending)
            {
                action();
            }
        }

        HRESULT EnsureNotificationService(DesktopShiftNativeError* error)
        {
            if (notificationService_ != nullptr)
            {
                return S_OK;
            }

            HRESULT result = shell_->QueryService(
                CLSID_VirtualDesktopNotificationService,
                __uuidof(IVirtualDesktopNotificationService24H2),
                reinterpret_cast<void**>(notificationService_.ReleaseAndGetAddressOf()));
            if (FAILED(result))
            {
                return SetError(
                    error,
                    result,
                    DesktopShiftNativeStageNotificationActivation,
                    L"The virtual-desktop notification service was unavailable.");
            }

            return S_OK;
        }

        /// Resolves the pinned-apps service the same way the notification
        /// service is resolved: on first use, from the shell, and without
        /// making the adapter's activation depend on it. A machine that cannot
        /// answer for pinning is still a machine that enumerates, creates,
        /// switches and moves.
        HRESULT EnsurePinnedApps(DesktopShiftNativeError* error)
        {
            if (pinnedApps_ != nullptr)
            {
                return S_OK;
            }

            HRESULT result = shell_->QueryService(
                CLSID_VirtualDesktopPinnedApps,
                __uuidof(IVirtualDesktopPinnedApps),
                reinterpret_cast<void**>(pinnedApps_.ReleaseAndGetAddressOf()));
            if (FAILED(result))
            {
                return SetError(
                    error,
                    result,
                    DesktopShiftNativeStageWindowPin,
                    L"The virtual-desktop pinned-apps service was unavailable.");
            }

            return S_OK;
        }

        /// Resolves the application view the Shell keeps for a window.
        ///
        /// Every operation that targets a window needs one, and each gets it
        /// the same way: through the view collection the Shell already exposes.
        /// This is also the one step in such an operation that a window can
        /// fail simply by being too new — the Shell registers a view shortly
        /// after the window is shown — so it reports under its own stage and a
        /// caller can read it as "not yet" rather than "refused".
        HRESULT ResolveApplicationView(
            HWND window,
            ComPtr<IApplicationView>& view,
            DesktopShiftNativeError* error) noexcept
        {
            if (applicationViews_ == nullptr)
            {
                return SetError(
                    error,
                    E_UNEXPECTED,
                    DesktopShiftNativeStageApplicationViewActivation,
                    L"The build-specific application-view collection is not active.");
            }

            HRESULT result = applicationViews_->GetViewForHwnd(
                window,
                view.ReleaseAndGetAddressOf());
            if (FAILED(result) || view == nullptr)
            {
                return SetError(
                    error,
                    FAILED(result) ? result : HrContractMismatch,
                    DesktopShiftNativeStageApplicationViewLookup,
                    L"The Windows Shell could not resolve an application view for the window.");
            }

            return S_OK;
        }

        /// The gate every pin operation passes before a pin slot is reached.
        ///
        /// Three questions are asked in the order that keeps the pin surface
        /// unreachable until it is earned: harmless validation, the per-build
        /// admission, and the read-only probe. A family that never admitted the
        /// surface answers "not supported" rather than "failed", because
        /// pinning being unavailable on that build is a property of the machine
        /// and not something a caller can retry away.
        HRESULT ValidatePinRequest(DesktopShiftNativeError* error)
        {
            if (!behaviorValidated_)
            {
                return SetError(
                    error,
                    HrAdapterNotValidated,
                    DesktopShiftNativeStageWindowPin,
                    L"Window pinning is disabled until harmless adapter validation succeeds.");
            }

            if (!pinnedAppsValidated_)
            {
                return SetError(
                    error,
                    HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                    DesktopShiftNativeStageWindowPin,
                    L"This Windows build family is not cleared for the window pin surface.");
            }

            if (!pinProbed_)
            {
                return SetError(
                    error,
                    HrAdapterNotValidated,
                    DesktopShiftNativeStageWindowPin,
                    L"Window pinning is disabled until the pin surface probe has succeeded.");
            }

            return S_OK;
        }

        /// Validates the window a pin was asked about.
        ///
        /// The pin surface takes application views, and only a live top-level
        /// window has one, so the checks are the ones a move makes: the window
        /// must still exist, its root must exist, and the handle must be that
        /// root.
        HRESULT ValidatePinWindow(
            HWND window,
            DesktopShiftNativeError* error) noexcept
        {
            if (!IsWindow(window))
            {
                return SetError(
                    error,
                    HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE),
                    DesktopShiftNativeStageWindowPin,
                    L"The top-level window handle is no longer live.");
            }

            const HWND root = GetAncestor(window, GA_ROOT);
            if (root == nullptr || !IsWindow(root))
            {
                return SetError(
                    error,
                    HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE),
                    DesktopShiftNativeStageWindowPin,
                    L"The top-level window handle is no longer live.");
            }

            if (root != window)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageWindowPin,
                    L"The window handle does not identify a top-level window.");
            }

            return S_OK;
        }

        /// Applies or releases the pin for one window.
        ///
        /// Both directions are one call — PinView and UnpinView differ in which
        /// slot they are, not in what they need — so they share the gate, the
        /// window checks and the view lookup, and split only at the slot. A
        /// refusal is reported and never retried: the Shell's answer does not
        /// change between two identical calls.
        HRESULT SetWindowPinned(
            HWND window,
            bool pinned,
            DesktopShiftNativeError* error)
        {
            if (window == nullptr)
            {
                return SetError(
                    error,
                    E_INVALIDARG,
                    DesktopShiftNativeStageWindowPin,
                    L"A nonzero top-level window handle is required.");
            }

            return Invoke([this, window, pinned, error]()
            {
                HRESULT result = ValidatePinRequest(error);
                if (FAILED(result))
                {
                    return result;
                }

                result = ValidatePinWindow(window, error);
                if (FAILED(result))
                {
                    return result;
                }

                ComPtr<IApplicationView> view;
                result = ResolveApplicationView(window, view, error);
                if (FAILED(result))
                {
                    return result;
                }

                result = pinned
                    ? pinnedApps_->PinView(view.Get())
                    : pinnedApps_->UnpinView(view.Get());
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageWindowPin,
                        pinned
                            ? L"The Windows Shell rejected showing the window on every virtual desktop."
                            : L"The Windows Shell rejected returning the window to a single virtual desktop.");
                }

                // Shell has committed the change. Return immediately rather than
                // adding a post-commit failure path that could invite a retry,
                // and let the caller read the state back through the query
                // instead of reporting what it hoped for.
                ClearError(error);
                return S_OK;
            });
        }

        HRESULT ReadSnapshot(DesktopShiftNativeError* error)
        {
            if (manager_ == nullptr)
            {
                return SetError(
                    error,
                    E_UNEXPECTED,
                    DesktopShiftNativeStageManagerActivation,
                    L"The build-specific virtual-desktop manager is not active.");
            }

            UINT reportedCount = 0;
            HRESULT result = manager_->GetCount(&reportedCount);
            if (FAILED(result))
            {
                return SetError(
                    error,
                    result,
                    DesktopShiftNativeStageEnumeration,
                    L"The virtual-desktop count probe failed.");
            }

            ComPtr<IObjectArray> desktops;
            result = manager_->GetDesktops(desktops.ReleaseAndGetAddressOf());
            if (FAILED(result))
            {
                return SetError(
                    error,
                    result,
                    DesktopShiftNativeStageEnumeration,
                    L"The virtual-desktop inventory probe failed.");
            }

            UINT arrayCount = 0;
            result = desktops->GetCount(&arrayCount);
            if (FAILED(result))
            {
                return SetError(
                    error,
                    result,
                    DesktopShiftNativeStageEnumeration,
                    L"The returned virtual-desktop collection could not be counted.");
            }

            if (reportedCount == 0 || reportedCount != arrayCount)
            {
                return SetError(
                    error,
                    HrContractMismatch,
                    DesktopShiftNativeStageBehaviorValidation,
                    L"The manager and collection returned inconsistent desktop counts.");
            }

            ComPtr<IVirtualDesktop24H2> current;
            result = manager_->GetCurrentDesktop(current.ReleaseAndGetAddressOf());
            if (FAILED(result))
            {
                return SetError(
                    error,
                    result,
                    DesktopShiftNativeStageCurrentDesktop,
                    L"The current virtual desktop could not be queried.");
            }

            GUID currentId{};
            result = current->GetId(&currentId);
            if (FAILED(result) || currentId == GUID_NULL)
            {
                return SetError(
                    error,
                    FAILED(result) ? result : HrContractMismatch,
                    DesktopShiftNativeStageCurrentDesktop,
                    L"The current virtual desktop returned an invalid identifier.");
            }

            std::vector<DesktopShiftNativeDesktop> next;
            next.reserve(arrayCount);
            for (UINT index = 0; index < arrayCount; ++index)
            {
                ComPtr<IVirtualDesktop24H2> desktop;
                result = desktops->GetAt(
                    index,
                    __uuidof(IVirtualDesktop24H2),
                    reinterpret_cast<void**>(desktop.ReleaseAndGetAddressOf()));
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageEnumeration,
                        L"A virtual-desktop entry did not support the selected build ABI.");
                }

                DesktopShiftNativeDesktop item{};
                result = desktop->GetId(&item.Id);
                if (FAILED(result) || item.Id == GUID_NULL)
                {
                    return SetError(
                        error,
                        FAILED(result) ? result : HrContractMismatch,
                        DesktopShiftNativeStageBehaviorValidation,
                        L"A virtual desktop returned an invalid identifier.");
                }

                const auto duplicate = std::find_if(
                    next.begin(),
                    next.end(),
                    [&item](const DesktopShiftNativeDesktop& existing)
                    {
                        return existing.Id == item.Id;
                    });
                if (duplicate != next.end())
                {
                    return SetError(
                        error,
                        HrContractMismatch,
                        DesktopShiftNativeStageBehaviorValidation,
                        L"The virtual-desktop inventory returned a duplicate identifier.");
                }

                HSTRING name = nullptr;
                result = desktop->GetName(&name);
                if (FAILED(result))
                {
                    return SetError(
                        error,
                        result,
                        DesktopShiftNativeStageBehaviorValidation,
                        L"A virtual desktop did not support the expected display-name operation.");
                }

                if (name != nullptr)
                {
                    UINT32 length = 0;
                    const wchar_t* value = WindowsGetStringRawBuffer(name, &length);
                    const size_t copyLength = (std::min)(
                        static_cast<size_t>(length),
                        std::size(item.DisplayName) - 1);
                    if (copyLength > 0)
                    {
                        static_cast<void>(wmemcpy_s(
                            item.DisplayName,
                            std::size(item.DisplayName),
                            value,
                            copyLength));
                    }
                    item.DisplayName[copyLength] = L'\0';
                    static_cast<void>(WindowsDeleteString(name));
                }

                item.Position = index;
                item.IsCurrent = item.Id == currentId ? TRUE : FALSE;
                next.emplace_back(item);
            }

            const bool currentIsPresent = std::any_of(
                next.begin(),
                next.end(),
                [&currentId](const DesktopShiftNativeDesktop& desktop)
                {
                    return desktop.Id == currentId;
                });
            if (!currentIsPresent)
            {
                return SetError(
                    error,
                    HrContractMismatch,
                    DesktopShiftNativeStageBehaviorValidation,
                    L"The current virtual desktop was absent from the enumerated inventory.");
            }

            snapshot_ = std::move(next);
            currentDesktop_ = currentId;
            ClearError(error);
            return S_OK;
        }

        void StopNotificationsCore() noexcept
        {
            if (notificationService_ != nullptr && notificationCookie_ != 0)
            {
                static_cast<void>(notificationService_->Unregister(notificationCookie_));
            }
            notificationCookie_ = 0;
            notificationSink_.Reset();
        }

        HANDLE workEvent_;
        std::thread worker_;
        std::atomic<DWORD> workerThreadId_{0};
        std::atomic<bool> stopping_{false};
        std::mutex workMutex_;
        std::deque<std::function<void()>> work_;
        HRESULT comInitializationResult_{E_PENDING};

        ComPtr<IServiceProvider> shell_;
        ComPtr<IVirtualDesktopManagerInternal24H2> manager_;
        ComPtr<IApplicationViewCollection> applicationViews_;
        ComPtr<IVirtualDesktopPinnedApps> pinnedApps_;
        ComPtr<IVirtualDesktopNotificationService24H2> notificationService_;
        ComPtr<NotificationSink> notificationSink_;
        DWORD notificationCookie_{0};
        std::vector<DesktopShiftNativeDesktop> snapshot_;
        GUID currentDesktop_{};
        bool behaviorValidated_{false};
        bool extendedLayoutValidated_{false};
        bool pinnedAppsValidated_{false};
        bool layoutProbed_{false};
        bool pinProbed_{false};
    };

    NativeAdapter* AsAdapter(void* adapter) noexcept
    {
        return static_cast<NativeAdapter*>(adapter);
    }

    HRESULT ValidateHandle(
        void* adapter,
        DesktopShiftNativeError* error) noexcept
    {
        if (adapter != nullptr)
        {
            return S_OK;
        }

        return SetError(
            error,
            E_POINTER,
            DesktopShiftNativeStageBehaviorValidation,
            L"A native virtual-desktop adapter handle was not provided.");
    }
}

extern "C"
{
    int32_t __stdcall DesktopShiftNative_CreateAdapter(
        uint32_t windowsBuild,
        void** adapter,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (adapter == nullptr)
        {
            return SetError(
                error,
                E_POINTER,
                DesktopShiftNativeStageBuildSelection,
                L"An adapter output pointer was not provided.");
        }
        *adapter = nullptr;

        const AdapterProfile* profile = SelectProfile(windowsBuild);
        if (profile == nullptr)
        {
            return SetError(
                error,
                HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED),
                DesktopShiftNativeStageBuildSelection,
                L"This exact Windows build family has no DesktopShift native adapter.");
        }

        if (!profile->Validated)
        {
            return SetError(
                error,
                HrAdapterNotValidated,
                DesktopShiftNativeStageBuildSelection,
                L"The build family is recognized, but its native adapter is not release-validated.");
        }

        try
        {
            auto value = std::make_unique<NativeAdapter>(
                profile->ExtendedLayoutValidated,
                profile->PinnedAppsValidated);
            const HRESULT result = value->Activate(error);
            if (FAILED(result))
            {
                return result;
            }

            *adapter = value.release();
            return S_OK;
        }
        catch (const std::bad_alloc&)
        {
            return SetError(
                error,
                E_OUTOFMEMORY,
                DesktopShiftNativeStageShellActivation,
                L"The native virtual-desktop adapter could not be allocated.");
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    int32_t __stdcall DesktopShiftNative_ValidateAdapter(
        void* adapter,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->Validate(error);
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    int32_t __stdcall DesktopShiftNative_RefreshSnapshot(
        void* adapter,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->RefreshSnapshot(error);
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    int32_t __stdcall DesktopShiftNative_GetDesktopCount(
        void* adapter,
        uint32_t* count,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->GetDesktopCount(count, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    int32_t __stdcall DesktopShiftNative_GetDesktop(
        void* adapter,
        uint32_t index,
        DesktopShiftNativeDesktop* desktop,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->GetDesktop(index, desktop, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    int32_t __stdcall DesktopShiftNative_GetCurrentDesktopId(
        void* adapter,
        GUID* desktopId,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->GetCurrentDesktopId(desktopId, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    int32_t __stdcall DesktopShiftNative_CreateDesktop(
        void* adapter,
        GUID* desktopId,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->CreateDesktop(desktopId, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageDesktopCreation);
        }
    }

    int32_t __stdcall DesktopShiftNative_SwitchDesktop(
        void* adapter,
        const GUID* desktopId,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        if (desktopId == nullptr)
        {
            return SetError(
                error,
                E_POINTER,
                DesktopShiftNativeStageDesktopSwitch,
                L"A target desktop identifier was not provided.");
        }

        try
        {
            const GUID requestedDesktopId = *desktopId;
            return AsAdapter(adapter)->SwitchDesktop(requestedDesktopId, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageDesktopSwitch);
        }
    }

    int32_t __stdcall DesktopShiftNative_MoveDesktop(
        void* adapter,
        const GUID* desktopId,
        uint32_t targetPosition,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        if (desktopId == nullptr)
        {
            return SetError(
                error,
                E_POINTER,
                DesktopShiftNativeStageDesktopReorder,
                L"A target desktop identifier was not provided.");
        }

        try
        {
            const GUID requestedDesktopId = *desktopId;
            return AsAdapter(adapter)->MoveDesktop(
                requestedDesktopId,
                targetPosition,
                error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageDesktopReorder);
        }
    }

    int32_t __stdcall DesktopShiftNative_ProbeDesktopLookup(
        void* adapter,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->ProbeDesktopLookup(error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageLayoutProbe);
        }
    }

    int32_t __stdcall DesktopShiftNative_SetDesktopName(
        void* adapter,
        const GUID* desktopId,
        const wchar_t* name,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        if (desktopId == nullptr)
        {
            return SetError(
                error,
                E_POINTER,
                DesktopShiftNativeStageDesktopRename,
                L"A target desktop identifier was not provided.");
        }

        try
        {
            const GUID requestedDesktopId = *desktopId;
            return AsAdapter(adapter)->SetDesktopName(requestedDesktopId, name, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageDesktopRename);
        }
    }

    int32_t __stdcall DesktopShiftNative_MoveWindowToDesktop(
        void* adapter,
        intptr_t windowHandle,
        const GUID* desktopId,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        if (desktopId == nullptr)
        {
            return SetError(
                error,
                E_POINTER,
                DesktopShiftNativeStageWindowMove,
                L"A target desktop identifier was not provided.");
        }

        try
        {
            const GUID requestedDesktopId = *desktopId;
            const HWND requestedWindow =
                reinterpret_cast<HWND>(windowHandle);
            return AsAdapter(adapter)->MoveWindowToDesktop(
                requestedWindow,
                requestedDesktopId,
                error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageWindowMove);
        }
    }

    int32_t __stdcall DesktopShiftNative_IsWindowPinned(
        void* adapter,
        intptr_t windowHandle,
        int32_t* isPinned,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        if (isPinned == nullptr)
        {
            return SetError(
                error,
                E_POINTER,
                DesktopShiftNativeStageWindowPin,
                L"A pin-state output pointer was not provided.");
        }

        try
        {
            const HWND requestedWindow = reinterpret_cast<HWND>(windowHandle);
            return AsAdapter(adapter)->IsWindowPinned(
                requestedWindow,
                isPinned,
                error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageWindowPin);
        }
    }

    int32_t __stdcall DesktopShiftNative_PinWindow(
        void* adapter,
        intptr_t windowHandle,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            const HWND requestedWindow = reinterpret_cast<HWND>(windowHandle);
            return AsAdapter(adapter)->PinWindow(requestedWindow, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageWindowPin);
        }
    }

    int32_t __stdcall DesktopShiftNative_UnpinWindow(
        void* adapter,
        intptr_t windowHandle,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            const HWND requestedWindow = reinterpret_cast<HWND>(windowHandle);
            return AsAdapter(adapter)->UnpinWindow(requestedWindow, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageWindowPin);
        }
    }

    int32_t __stdcall DesktopShiftNative_ProbeWindowPin(
        void* adapter,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->ProbeWindowPin(error);
        }
        catch (...)
        {
            return SetUnexpectedError(error, DesktopShiftNativeStageWindowPin);
        }
    }

    int32_t __stdcall DesktopShiftNative_StartNotifications(
        void* adapter,
        DesktopShiftTopologyCallback callback,
        void* context,
        DesktopShiftNativeError* error) noexcept
    {
        ClearError(error);
        if (FAILED(ValidateHandle(adapter, error)))
        {
            return E_POINTER;
        }

        try
        {
            return AsAdapter(adapter)->StartNotifications(callback, context, error);
        }
        catch (...)
        {
            return SetUnexpectedError(error);
        }
    }

    void __stdcall DesktopShiftNative_StopNotifications(void* adapter) noexcept
    {
        if (adapter != nullptr)
        {
            AsAdapter(adapter)->StopNotifications();
        }
    }

    void __stdcall DesktopShiftNative_DestroyAdapter(void* adapter) noexcept
    {
        delete AsAdapter(adapter);
    }
}
