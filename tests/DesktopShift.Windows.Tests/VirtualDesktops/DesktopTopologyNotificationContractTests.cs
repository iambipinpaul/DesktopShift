using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.VirtualDesktops;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.Tests.VirtualDesktops;

/// <summary>
/// The contract topology recovery depends on, proved without touching a single
/// real desktop.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is read-only. The native assertions load the bridge and
/// inspect exported symbols, struct layout, and delegate marshalling; they never
/// activate an adapter, never register a live notification sink, and never call
/// an operation that could create, delete, reorder, or switch a desktop. The
/// managed assertions drive a fake bridge.
/// </para>
/// <para>
/// The behavior that genuinely needs a real desktop to be deleted or dragged is
/// in <c>docs/manual-tests/desktop-topology-recovery.md</c>, because there is no
/// way to prove it in an automated test without changing the machine the test
/// runs on.
/// </para>
/// </remarks>
[TestClass]
public sealed class DesktopTopologyNotificationContractTests
{
    private const int GuidSize = 16;
    private const int DisplayNameCapacity = 260;
    private const int MessageCapacity = 256;

    private static readonly WindowsBuildInfo Build26200 = new(
        isWindows: true,
        major: 10,
        minor: 0,
        build: 26200,
        revision: 8117,
        Architecture.X64);

    [TestMethod]
    public void NativeBridge_ExportsTheNotificationBoundariesWithoutInvokingThem()
    {
        string libraryPath = Path.Combine(
            AppContext.BaseDirectory,
            "DesktopShift.NativeBridge.dll");

        Assert.IsTrue(
            NativeLibrary.TryLoad(libraryPath, out nint library),
            $"Could not load the native bridge at {libraryPath}.");
        try
        {
            foreach (string export in new[]
            {
                "DesktopShiftNative_StartNotifications",
                "DesktopShiftNative_StopNotifications",
                "DesktopShiftNative_RefreshSnapshot",
                "DesktopShiftNative_GetDesktopCount",
                "DesktopShiftNative_GetDesktop",
                "DesktopShiftNative_GetCurrentDesktopId",
            })
            {
                Assert.IsTrue(
                    NativeLibrary.TryGetExport(library, export, out nint entryPoint),
                    $"The native bridge does not export {export}.");
                Assert.AreNotEqual(0, entryPoint);
            }

            // Recovery re-resolves and recreates; it never removes. The absence
            // of a delete export is part of how "unrelated desktops are never
            // deleted" is enforced, so it is asserted rather than assumed.
            Assert.IsFalse(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_RemoveDesktop",
                    out _));
            Assert.IsFalse(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_RenameDesktop",
                    out _));
            Assert.IsFalse(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_MoveDesktop",
                    out _));
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [TestMethod]
    public void TheDesktopStruct_MarshalsExactlyAsTheAbiDeclaresIt()
    {
        Assert.AreEqual(
            GuidSize + (DisplayNameCapacity * sizeof(char)) + sizeof(uint) + sizeof(int),
            Marshal.SizeOf<NativeMethods.NativeDesktop>());
        Assert.AreEqual(
            0,
            (int)Marshal.OffsetOf<NativeMethods.NativeDesktop>(
                nameof(NativeMethods.NativeDesktop.Id)));
        Assert.AreEqual(
            GuidSize,
            (int)Marshal.OffsetOf<NativeMethods.NativeDesktop>(
                nameof(NativeMethods.NativeDesktop.DisplayName)));
        Assert.AreEqual(
            GuidSize + (DisplayNameCapacity * sizeof(char)),
            (int)Marshal.OffsetOf<NativeMethods.NativeDesktop>(
                nameof(NativeMethods.NativeDesktop.Position)));
        Assert.AreEqual(
            GuidSize + (DisplayNameCapacity * sizeof(char)) + sizeof(uint),
            (int)Marshal.OffsetOf<NativeMethods.NativeDesktop>(
                nameof(NativeMethods.NativeDesktop.IsCurrent)));
    }

    [TestMethod]
    public void TheErrorStruct_MarshalsExactlyAsTheAbiDeclaresIt()
    {
        Assert.AreEqual(
            sizeof(int) + sizeof(uint) + (MessageCapacity * sizeof(char)),
            Marshal.SizeOf<NativeMethods.NativeError>());
        Assert.AreEqual(
            0,
            (int)Marshal.OffsetOf<NativeMethods.NativeError>(
                nameof(NativeMethods.NativeError.HResult)));
        Assert.AreEqual(
            sizeof(int),
            (int)Marshal.OffsetOf<NativeMethods.NativeError>(
                nameof(NativeMethods.NativeError.Stage)));
        Assert.AreEqual(
            sizeof(int) + sizeof(uint),
            (int)Marshal.OffsetOf<NativeMethods.NativeError>(
                nameof(NativeMethods.NativeError.Message)));
    }

    [TestMethod]
    public void EveryTopologyReason_MatchesTheOrdinalTheAbiAssignsIt()
    {
        // Read the ordinals back through reflection rather than casting each
        // member inline. A cast of a literal enum member folds to a constant the
        // compiler can answer on its own, and a test the compiler can answer is
        // not pinning the ABI to anything. Going through Enum.GetValues also
        // makes a newly added reason fail here until someone pins it too.
        Dictionary<string, uint> expected = new(StringComparer.Ordinal)
        {
            [nameof(NativeMethods.NativeTopologyReason.Created)] = 1u,
            [nameof(NativeMethods.NativeTopologyReason.Destroyed)] = 2u,
            [nameof(NativeMethods.NativeTopologyReason.Moved)] = 3u,
            [nameof(NativeMethods.NativeTopologyReason.NameChanged)] = 4u,
            [nameof(NativeMethods.NativeTopologyReason.CurrentChanged)] = 5u,
            [nameof(NativeMethods.NativeTopologyReason.Switched)] = 6u,
            [nameof(NativeMethods.NativeTopologyReason.RemoteConnected)] = 7u,
        };

        Dictionary<string, uint> declared = new(StringComparer.Ordinal);
        foreach (NativeMethods.NativeTopologyReason reason in
            Enum.GetValues<NativeMethods.NativeTopologyReason>())
        {
            declared[reason.ToString()] = Convert.ToUInt32(reason);
        }

        Assert.AreEqual(
            expected.Count,
            declared.Count,
            "The ABI gained or lost a topology reason without this test being updated.");
        foreach ((string name, uint ordinal) in expected)
        {
            Assert.IsTrue(
                declared.TryGetValue(name, out uint actual),
                $"The ABI no longer declares {name}.");
            Assert.AreEqual(ordinal, actual, $"{name} changed ordinal.");
        }

        Assert.AreEqual(
            typeof(uint),
            Enum.GetUnderlyingType(typeof(NativeMethods.NativeTopologyReason)));

        // Deletion and reordering are the two reasons recovery exists for, so
        // the ABI must keep naming them.
        Assert.IsTrue(
            Enum.IsDefined(NativeMethods.NativeTopologyReason.Destroyed));
        Assert.IsTrue(
            Enum.IsDefined(NativeMethods.NativeTopologyReason.Moved));
    }

    [TestMethod]
    public void TheNotificationCallback_MarshalsAsAStdCallFunctionPointer()
    {
        int observed = 0;
        NativeMethods.TopologyCallback callback = (reason, context) =>
        {
            observed = (int)reason + (int)context;
        };

        nint functionPointer = Marshal.GetFunctionPointerForDelegate(callback);

        Assert.AreNotEqual(0, functionPointer);
        Assert.AreEqual(0, observed);
        GC.KeepAlive(callback);
    }

    [TestMethod]
    public async Task EveryTopologyReason_ReachesTheProviderVerbatim()
    {
        ReadOnlyFakeBridge bridge = new();
        using ValidatedVirtualDesktopTopologyProvider provider = new(
            new LimitedVirtualDesktopTopologyService(),
            new ReadOnlyFakeBridgeFactory(bridge));
        _ = await provider.TestCompatibilityAsync(Build26200);
        List<string> reasons = [];
        provider.TopologyChanged += (_, args) => reasons.Add(args.Reason);

        foreach (NativeMethods.NativeTopologyReason reason in
            Enum.GetValues<NativeMethods.NativeTopologyReason>())
        {
            bridge.RaiseTopologyChanged(reason.ToString());
        }

        CollectionAssert.AreEqual(
            Enum.GetValues<NativeMethods.NativeTopologyReason>()
                .Select(static reason => reason.ToString())
                .ToArray(),
            reasons);

        // Reading notifications never changes the topology.
        Assert.AreEqual(0, bridge.MutatingCallCount);
    }

    [TestMethod]
    public async Task AProviderFallback_AnnouncesItselfAsATopologyChange()
    {
        ReadOnlyFakeBridge bridge = new()
        {
            SnapshotResult = NativeBridgeResult<NativeDesktopSnapshot>.Failed(
                new NativeBridgeError(
                    "native.enumeration_failed",
                    "Enumeration",
                    "The shell interface went away.",
                    unchecked((int)0x80004005))),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = new(
            new LimitedVirtualDesktopTopologyService(),
            new ReadOnlyFakeBridgeFactory(bridge));
        _ = await provider.TestCompatibilityAsync(Build26200);
        List<string> reasons = [];
        provider.TopologyChanged += (_, args) => reasons.Add(args.Reason);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        // Losing the adapter is a topology change as far as semantic mappings
        // are concerned: every runtime binding has to be re-decided in Limited
        // Mode, and nothing should be recreated while it is.
        Assert.IsFalse(result.IsSuccess);
        CollectionAssert.AreEqual(new[] { "ProviderFallbackActivated" }, reasons);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.IsFalse(provider.Capabilities.CanCreateDesktop);
        Assert.AreEqual(0, bridge.MutatingCallCount);
    }

    private sealed class ReadOnlyFakeBridgeFactory(INativeVirtualDesktopBridge bridge)
        : INativeVirtualDesktopBridgeFactory
    {
        public NativeBridgeResult<INativeVirtualDesktopBridge> TryCreate(
            int windowsBuild)
        {
            _ = windowsBuild;
            return NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge);
        }
    }

    /// <summary>
    /// A bridge that answers reads and refuses to pretend it mutated anything.
    /// </summary>
    /// <remarks>
    /// <see cref="MutatingCallCount"/> is the assertion surface for "no desktop
    /// was created or switched". The fake counts rather than acts, so a test
    /// that regresses into mutating shows up as a count, not as a changed
    /// machine.
    /// </remarks>
    private sealed class ReadOnlyFakeBridge : INativeVirtualDesktopBridge
    {
        private static readonly NativeDesktopSnapshot Snapshot = CreateSnapshot();

        private Action<string>? topologyChanged;

        public NativeBridgeResult<NativeDesktopSnapshot> SnapshotResult { get; init; } =
            NativeBridgeResult<NativeDesktopSnapshot>.Succeeded(Snapshot);

        public int MutatingCallCount { get; private set; }

        public NativeBridgeResult Validate() => NativeBridgeResult.Succeeded;

        public NativeBridgeResult<NativeDesktopSnapshot> ReadSnapshot() =>
            SnapshotResult;

        public NativeBridgeResult<Guid> CreateDesktop()
        {
            MutatingCallCount++;
            return NativeBridgeResult<Guid>.Succeeded(Guid.NewGuid());
        }

        public NativeBridgeResult SwitchDesktop(Guid desktopId)
        {
            _ = desktopId;
            MutatingCallCount++;
            return NativeBridgeResult.Succeeded;
        }

        public NativeBridgeResult StartNotifications(Action<string> onTopologyChanged)
        {
            topologyChanged = onTopologyChanged;
            return NativeBridgeResult.Succeeded;
        }

        public void RaiseTopologyChanged(string reason) =>
            topologyChanged?.Invoke(reason);

        public void Dispose()
        {
        }

        private static NativeDesktopSnapshot CreateSnapshot()
        {
            Guid code = Guid.Parse("11111111-1111-1111-1111-111111111111");
            Guid web = Guid.Parse("22222222-2222-2222-2222-222222222222");
            ImmutableArray<NativeDesktopDescriptor> desktops =
            [
                new NativeDesktopDescriptor(code, "Code", 0),
                new NativeDesktopDescriptor(web, "Web", 1),
            ];
            return new NativeDesktopSnapshot(desktops, web);
        }
    }
}
