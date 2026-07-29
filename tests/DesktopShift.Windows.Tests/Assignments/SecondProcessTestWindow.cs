using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopShift.Windows.Tests.Assignments;

internal sealed class SecondProcessTestWindow : IAsyncDisposable
{
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly Process process;
    private readonly Guid nonce;
    private bool disposed;

    private SecondProcessTestWindow(
        Process process,
        Guid nonce,
        nint windowHandle)
    {
        this.process = process;
        this.nonce = nonce;
        Handle = windowHandle;
    }

    internal nint Handle { get; }

    internal int ProcessId => process.Id;

    internal bool IsAliveAndOwned =>
        !process.HasExited &&
        NativeMethods.IsWindow(Handle) &&
        NativeMethods.GetWindowThreadProcessId(Handle, out uint processId) != 0 &&
        processId == checked((uint)process.Id);

    internal static async Task<SecondProcessTestWindow> StartAsync(
        CancellationToken cancellationToken)
    {
        string helperAssembly = Path.Combine(
            AppContext.BaseDirectory,
            "DesktopShift.TestWindowHost.dll");
        string runtimeConfiguration = Path.Combine(
            AppContext.BaseDirectory,
            "DesktopShift.Windows.Tests.runtimeconfig.json");
        if (!File.Exists(helperAssembly))
        {
            throw new FileNotFoundException(
                "The isolated window-host assembly was not copied to the test output.",
                helperAssembly);
        }

        if (!File.Exists(runtimeConfiguration))
        {
            throw new FileNotFoundException(
                "The test runtime configuration was not found.",
                runtimeConfiguration);
        }

        Guid nonce = Guid.NewGuid();
        ProcessStartInfo startInfo = new()
        {
            FileName =
                Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ??
                "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("exec");
        startInfo.ArgumentList.Add("--runtimeconfig");
        startInfo.ArgumentList.Add(runtimeConfiguration);
        startInfo.ArgumentList.Add(helperAssembly);
        startInfo.ArgumentList.Add(nonce.ToString("D"));

        Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException(
                "The isolated window-host process did not start.");
        }

        SecondProcessTestWindow? result = null;
        try
        {
            using CancellationTokenSource startup = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(StartupTimeout);
            string? handshakeLine = await process.StandardOutput
                .ReadLineAsync(startup.Token)
                .ConfigureAwait(false);
            WindowHandshake? handshake = string.IsNullOrWhiteSpace(handshakeLine)
                ? null
                : JsonSerializer.Deserialize<WindowHandshake>(handshakeLine);
            if (handshake is null ||
                handshake.Nonce != nonce ||
                handshake.ProcessId != process.Id ||
                handshake.WindowHandle == 0)
            {
                throw new InvalidOperationException(
                    "The isolated window host returned an invalid handshake.");
            }

            result = new SecondProcessTestWindow(
                process,
                nonce,
                checked((nint)handshake.WindowHandle));
            if (!result.IsAliveAndOwned)
            {
                throw new InvalidOperationException(
                    "The reported HWND does not belong to the isolated child process.");
            }

            return result;
        }
        catch
        {
            if (result is not null)
            {
                await result.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                await TerminateAsync(process).ConfigureAwait(false);
                process.Dispose();
            }

            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (!process.HasExited)
        {
            try
            {
                await process.StandardInput
                    .WriteLineAsync($"EXIT {nonce:D}")
                    .ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
                process.StandardInput.Close();
            }
            catch (InvalidOperationException)
            {
                // The child exited between the liveness check and the write.
            }
            catch (IOException)
            {
                // A closed pipe means the dedicated child has already exited.
            }
        }

        await TerminateAsync(process).ConfigureAwait(false);
        process.Dispose();
    }

    private static async Task TerminateAsync(Process process)
    {
        if (process.HasExited)
        {
            return;
        }

        using CancellationTokenSource shutdown = new(ShutdownTimeout);
        try
        {
            await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            return;
        }
        catch (OperationCanceledException)
        {
            // The kill below is scoped to the dedicated helper process tree.
        }

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
    }

    private sealed record WindowHandshake(
        Guid Nonce,
        int ProcessId,
        long WindowHandle);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint GetWindowThreadProcessId(
            nint windowHandle,
            out uint processId);
    }
}
