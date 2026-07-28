using System.Runtime.InteropServices;
using DesktopShift.Windows.Observation;
using Microsoft.Win32.SafeHandles;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowsProcessCommandLineReaderTests
{
    [TestMethod]
    public async Task TryRead_UsesQueryLimitedInformationAndReadsUnicodeResult()
    {
        FakeCommandLineApi nativeApi = new("Code.exe --profile work");
        WindowsProcessCommandLineReader reader = new(nativeApi);

        string? result = await reader.TryReadAsync(42);

        Assert.AreEqual("Code.exe --profile work", result);
        Assert.AreEqual(0x1000U, nativeApi.RequestedAccess);
        Assert.AreEqual(42U, nativeApi.RequestedProcessId);
        Assert.AreEqual(2, nativeApi.QueryCount);
    }

    private sealed class FakeCommandLineApi : IWindowsCommandLineApi
    {
        private readonly string commandLine;

        public FakeCommandLineApi(string commandLine)
        {
            this.commandLine = commandLine;
        }

        public uint RequestedAccess { get; private set; }

        public uint RequestedProcessId { get; private set; }

        public int QueryCount { get; private set; }

        public SafeProcessHandle OpenProcess(
            uint desiredAccess,
            uint processId)
        {
            RequestedAccess = desiredAccess;
            RequestedProcessId = processId;
            return new SafeProcessHandle((nint)1, ownsHandle: false);
        }

        public int QueryCommandLine(
            SafeProcessHandle process,
            nint buffer,
            uint bufferLength,
            out uint requiredLength)
        {
            QueryCount++;
            int characterBytes = checked(commandLine.Length * sizeof(char));
            int headerSize = Marshal.SizeOf<
                WindowsProcessCommandLineReader.NativeUnicodeString>();
            requiredLength = checked((uint)(headerSize + characterBytes + 2));
            if (buffer == 0)
            {
                return unchecked((int)0xC0000004);
            }

            nint stringBuffer = buffer + headerSize;
            Marshal.Copy(
                commandLine.ToCharArray(),
                0,
                stringBuffer,
                commandLine.Length);
            Marshal.WriteInt16(stringBuffer + characterBytes, 0);
            Marshal.StructureToPtr(
                new WindowsProcessCommandLineReader.NativeUnicodeString(
                    checked((ushort)characterBytes),
                    checked((ushort)(characterBytes + 2)),
                    stringBuffer),
                buffer,
                fDeleteOld: false);
            return 0;
        }
    }
}
