using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace DLPPCController;

internal sealed record NvidiaIdentity(
    ushort DeviceId,
    uint SubsystemId,
    uint Bus,
    string Name,
    string DriverVersion);

internal sealed record DlppcState(
    bool Active,
    int PolicyIndex,
    uint ControllerMask);

/// <summary>
/// Minimal NVAPI wrapper for the PERF_CF DLPPC policy.
/// The PERF_CF interfaces and layouts are undocumented/private and may change between drivers.
/// All writes are guarded by identity, version, bounds and readback checks.
/// </summary>
internal sealed class NvidiaApi : IDisposable
{
    private const uint DlppcLabel = 7;
    private const int GuardBytes = 4096;

    private IntPtr _library;
    private IntPtr _gpu;
    private QueryInterface? _query;
    private bool _initialized;
    private bool _disposed;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterface(uint id);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NoArgs();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Enumerate([Out] IntPtr[] handles, out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int BufferCall(IntPtr gpu, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PciIds(IntPtr gpu, out uint device, out uint subsystem,
        out uint revision, out uint extDevice);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetUInt(IntPtr gpu, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DriverVersion(out uint version, [Out] byte[] branch);

    public NvidiaIdentity Identity { get; private set; } = null!;

    public NvidiaApi()
    {
        try
        {
            Initialize();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Initialize()
    {
        if (!Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("DLPPC Controller must run as x64.");

        string nvapiPath = Path.Combine(Environment.SystemDirectory, "nvapi64.dll");
        if (!File.Exists(nvapiPath))
            throw new FileNotFoundException("NVIDIA NVAPI library was not found. Install/update the NVIDIA display driver.", nvapiPath);

        _library = NativeLibrary.Load(nvapiPath);
        _query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(
            NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));

        Check(Resolve<NoArgs>(0x0150e828)(), "NvAPI_Initialize");
        _initialized = true;

        var handles = new IntPtr[64];
        Check(Resolve<Enumerate>(0xe5ac921f)(handles, out uint count), "NvAPI_EnumPhysicalGPUs");

        if (count == 0)
            throw new NotSupportedException("NVAPI did not report an NVIDIA physical GPU.");

        if (count != 1)
            throw new NotSupportedException(
                $"NVAPI reported {count} NVIDIA GPUs. This standalone build refuses to guess which adapter to modify.");

        _gpu = handles[0];

        Check(Resolve<PciIds>(0x2ddfb66e)(
            _gpu, out uint pci, out uint subsystem, out _, out _), "NvAPI_GPU_GetPCIIdentifiers");

        Check(Resolve<GetUInt>(0x1be0b8e5)(_gpu, out uint bus), "NvAPI_GPU_GetBusId");
        Check(Resolve<DriverVersion>(0x2926aaad)(out uint driver, new byte[64]),
            "NvAPI_SYS_GetDriverAndBranchVersion");

        string name = ReadString(0xceee8e9f);
        Identity = new NvidiaIdentity(
            (ushort)(pci >> 16),
            subsystem,
            bus,
            string.IsNullOrWhiteSpace(name) ? "Unknown NVIDIA GPU" : name,
            $"{driver / 100}.{driver % 100:00}");
    }

    public DlppcState? ReadDlppc()
    {
        var policy = FindDlppcPolicy();
        if (policy is null)
            return null;

        int p = policy.Value.PolicyIndex;
        byte[] control = ReadPolicyControl(p);
        int activateOffset = checked(72 + p * 168 + 4);

        return new DlppcState(control[activateOffset] != 0, p, policy.Value.ControllerMask);
    }


    public void SetDlppc(bool active)
    {
        var policy = FindDlppcPolicy()
            ?? throw new NotSupportedException(
                "This GPU/driver does not expose the DLPPC_1X policy.");
    
        // The null-coalescing operator already unwraps the nullable tuple.
        int p = policy.PolicyIndex;
    
        byte[] control = ReadPolicyControl(p);
        int activateOffset = checked(72 + p * 168 + 4);
    
        // Change only bActivate in the selected policy's GET structure.
        control[activateOffset] = active ? (byte)1 : (byte)0;
    
        Call(0x4d1c7d6e, control, "PerfCfPolicySetControl");
    
        DlppcState? readback = ReadDlppc();
    
        if (readback is null ||
            readback.Active != active ||
            readback.PolicyIndex != p)
        {
            throw new InvalidOperationException(
                "DLPPC readback verification failed. " +
                "The driver did not retain the requested state.");
        }
    }


    private (int PolicyIndex, uint ControllerMask)? FindDlppcPolicy()
    {
        byte[] info = NewBuffer(0x164c, 0x1164c);
        Call(0x1cac3865, info, "PerfCfPolicyGetInfo(V1)");
        RequireVersion(info, 0x1164c);

        int p = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(10 + (int)DlppcLabel * 2));
        uint policyMask = U32(info, 4);

        if (p >= 32 || (policyMask & (1u << p)) == 0)
            return null;

        int entry = checked(76 + p * 176);
        EnsureRange(info, entry, 176);

        uint type = U32(info, entry);
        uint label = U32(info, entry + 8);

        if (label != DlppcLabel || type != 0)
            throw Unsupported("PERF_CF policy identity did not match DLPPC_1X/type 0");

        return (p, U32(info, entry + 48));
    }

    private byte[] ReadPolicyControl(int policyIndex)
    {
        if (policyIndex is < 0 or >= 32)
            throw Unsupported("policy index");

        byte[] control = NewBuffer(0x1548, 0x11548);
        uint selectedMask = 1u << policyIndex;
        Put(control, 4, selectedMask);

        Call(0x69de2c54, control, "PerfCfPolicyGetControl(V1)");
        RequireVersion(control, 0x11548);

        int entry = checked(72 + policyIndex * 168);
        EnsureRange(control, entry, 168);

        if (U32(control, 4) != selectedMask || U32(control, entry) != 0)
            throw Unsupported("PERF_CF policy control identity/selection");

        return control;
    }

    private string ReadString(uint id)
    {
        byte[] data = new byte[64];
        Call(id, data, "GPU identity string");
        int end = Array.IndexOf(data, (byte)0);
        return Encoding.ASCII.GetString(data, 0, end < 0 ? data.Length : end).Trim();
    }

    private T Resolve<T>(uint id) where T : Delegate
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_query is null)
            throw new ObjectDisposedException(nameof(NvidiaApi));

        IntPtr address = _query(id);
        if (address == IntPtr.Zero)
            throw new NotSupportedException($"NVAPI interface 0x{id:X8} is unavailable.");

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    private void Call(uint id, byte[] data, string name)
    {
        byte[] guarded = new byte[data.Length + GuardBytes];
        data.CopyTo(guarded, 0);
        guarded.AsSpan(data.Length).Fill(0xCD);

        GCHandle pin = GCHandle.Alloc(guarded, GCHandleType.Pinned);
        int status;
        try
        {
            status = Resolve<BufferCall>(id)(_gpu, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }

        if (guarded.AsSpan(data.Length).IndexOfAnyExcept((byte)0xCD) >= 0)
            throw Unsupported($"{name} buffer overrun");

        Check(status, name);
        guarded.AsSpan(0, data.Length).CopyTo(data);
    }

    private static byte[] NewBuffer(int size, uint version)
    {
        byte[] buffer = new byte[size];
        Put(buffer, 0, version);
        return buffer;
    }

    private static uint U32(byte[] buffer, int offset)
    {
        EnsureRange(buffer, offset, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
    }

    private static void Put(byte[] buffer, int offset, uint value)
    {
        EnsureRange(buffer, offset, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), value);
    }

    private static void EnsureRange(byte[] buffer, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > buffer.Length - length)
            throw Unsupported("structure bounds");
    }

    private static void RequireVersion(byte[] buffer, uint expected)
    {
        if (U32(buffer, 0) != expected)
            throw Unsupported($"structure version (expected 0x{expected:X}, received 0x{U32(buffer, 0):X})");
    }

    private static Exception Unsupported(string detail) =>
        new NotSupportedException($"Unsupported NVIDIA {detail}. No write was attempted.");

    private static void Check(int status, string operation)
    {
        if (status != 0)
            throw new InvalidOperationException($"{operation} failed (NVAPI status {status}, 0x{unchecked((uint)status):X8}).");
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_initialized && _query is not null)
        {
            try
            {
                IntPtr unload = _query(0xd22bdd7e);
                if (unload != IntPtr.Zero)
                    Marshal.GetDelegateForFunctionPointer<NoArgs>(unload)();
            }
            catch
            {
                // Best-effort NVAPI unload.
            }
        }

        _gpu = IntPtr.Zero;
        _query = null;

        if (_library != IntPtr.Zero)
        {
            NativeLibrary.Free(_library);
            _library = IntPtr.Zero;
        }
    }
}
