using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DesktopShift.Windows.Assignments;

internal sealed record DocumentedDesktopIdResult(
    Guid DesktopId,
    int HResult,
    string Stage)
{
    public bool IsSuccess => HResult >= 0;
}

internal sealed record DocumentedDesktopOperationResult(
    int HResult,
    string Stage)
{
    public bool IsSuccess => HResult >= 0;
}

internal interface IDocumentedVirtualDesktopManagerApi : IDisposable
{
    DocumentedDesktopIdResult GetWindowDesktopId(nint windowHandle);

    DocumentedDesktopOperationResult MoveWindowToDesktop(
        nint windowHandle,
        Guid desktopId);
}

internal sealed class DocumentedVirtualDesktopManagerApi :
    IDocumentedVirtualDesktopManagerApi
{
    private readonly ComManagerApartment apartment = new();

    public DocumentedDesktopIdResult GetWindowDesktopId(nint windowHandle)
    {
        try
        {
            return apartment.Invoke(
                manager =>
                {
                    int hResult = manager.GetWindowDesktopId(
                        windowHandle,
                        out Guid desktopId);
                    return new DocumentedDesktopIdResult(
                        desktopId,
                        hResult,
                        "GetWindowDesktopId");
                });
        }
        catch (Exception exception)
        {
            return new DocumentedDesktopIdResult(
                Guid.Empty,
                exception.HResult,
                "ManagerActivation");
        }
    }

    public DocumentedDesktopOperationResult MoveWindowToDesktop(
        nint windowHandle,
        Guid desktopId)
    {
        try
        {
            return apartment.Invoke(
                manager =>
                {
                    Guid requestedDesktopId = desktopId;
                    int hResult = manager.MoveWindowToDesktop(
                        windowHandle,
                        ref requestedDesktopId);
                    return new DocumentedDesktopOperationResult(
                        hResult,
                        "MoveWindowToDesktop");
                });
        }
        catch (Exception exception)
        {
            return new DocumentedDesktopOperationResult(
                exception.HResult,
                "ManagerActivation");
        }
    }

    public void Dispose() => apartment.Dispose();

    private sealed class ComManagerApartment : IDisposable
    {
        private const uint MultiThreadedApartment = 0;
        private const uint InProcessOrLocalServer = 0x1 | 0x4;

        private static readonly Guid VirtualDesktopManagerClassId =
            Guid.Parse("AA509086-5CA9-4C25-8F95-589D3C07B48A");
        private static readonly Guid VirtualDesktopManagerInterfaceId =
            Guid.Parse("A5CD92FF-29BE-454C-8D04-D82879FB3F1B");

        private readonly BlockingCollection<Action> work = new();
        private readonly Thread worker;
        private IVirtualDesktopManager? manager;
        private Exception? initializationException;
        private int disposed;

        public ComManagerApartment()
        {
            worker = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "DesktopShift documented desktop manager",
            };
            worker.Start();
        }

        public T Invoke<T>(Func<IVirtualDesktopManager, T> operation)
        {
            ArgumentNullException.ThrowIfNull(operation);
            ObjectDisposedException.ThrowIf(
                Volatile.Read(ref disposed) != 0,
                this);

            TaskCompletionSource<T> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                work.Add(
                    () =>
                    {
                        try
                        {
                            if (initializationException is not null)
                            {
                                completion.SetException(initializationException);
                                return;
                            }

                            completion.SetResult(operation(manager!));
                        }
                        catch (Exception exception)
                        {
                            completion.SetException(exception);
                        }
                    });
            }
            catch (InvalidOperationException)
            {
                throw new ObjectDisposedException(GetType().FullName);
            }

            return completion.Task.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0)
            {
                return;
            }

            work.CompleteAdding();
            worker.Join();
            work.Dispose();
        }

        private void ThreadMain()
        {
            int initializationResult = NativeMethods.CoInitializeEx(
                0,
                MultiThreadedApartment);
            bool shouldUninitialize = initializationResult >= 0;
            if (initializationResult < 0)
            {
                initializationException = Marshal.GetExceptionForHR(
                    initializationResult) ??
                    new COMException(
                        "COM initialization failed.",
                        initializationResult);
            }
            else
            {
                InitializeManager();
            }

            foreach (Action action in work.GetConsumingEnumerable())
            {
                action();
            }

            if (manager is not null)
            {
                Marshal.FinalReleaseComObject(manager);
                manager = null;
            }

            if (shouldUninitialize)
            {
                NativeMethods.CoUninitialize();
            }
        }

        private void InitializeManager()
        {
            int result = NativeMethods.CoCreateInstance(
                in VirtualDesktopManagerClassId,
                0,
                InProcessOrLocalServer,
                in VirtualDesktopManagerInterfaceId,
                out nint managerPointer);
            if (result < 0 || managerPointer == 0)
            {
                int failureResult = result < 0
                    ? result
                    : unchecked((int)0x8000FFFF);
                initializationException = Marshal.GetExceptionForHR(failureResult) ??
                    new COMException(
                        "The documented virtual desktop manager could not be activated.",
                        failureResult);
                return;
            }

            try
            {
                manager = (IVirtualDesktopManager)
                    Marshal.GetObjectForIUnknown(managerPointer);
            }
            catch (Exception exception)
            {
                initializationException = exception;
            }
            finally
            {
                Marshal.Release(managerPointer);
            }
        }
    }

    // Public Windows contract:
    // https://learn.microsoft.com/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager
    [ComImport]
    [Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopManager
    {
        [PreserveSig]
        int IsWindowOnCurrentVirtualDesktop(
            nint topLevelWindow,
            [MarshalAs(UnmanagedType.Bool)] out bool onCurrentDesktop);

        [PreserveSig]
        int GetWindowDesktopId(
            nint topLevelWindow,
            out Guid desktopId);

        [PreserveSig]
        int MoveWindowToDesktop(
            nint topLevelWindow,
            [In] ref Guid desktopId);
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll")]
        internal static extern int CoInitializeEx(
            nint reserved,
            uint concurrencyModel);

        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        internal static extern int CoCreateInstance(
            in Guid classId,
            nint outer,
            uint context,
            in Guid interfaceId,
            out nint instance);
    }
}
