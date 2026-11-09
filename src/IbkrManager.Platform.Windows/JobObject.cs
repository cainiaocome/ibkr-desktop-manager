using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// A kill-on-close job object. When the manager dies for any reason, Windows tears down every
/// process in the job, so an IBKR Desktop session can never be left orphaned.
/// </summary>
public sealed class JobObject : IDisposable
{
    private readonly SafeFileHandle _handle;

    private JobObject(SafeFileHandle handle) => _handle = handle;

    public static JobObject CreateKillOnClose()
    {
        var handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            throw new InvalidOperationException($"CreateJobObject failed ({Marshal.GetLastWin32Error()})");
        }

        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var length = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var pointer = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, pointer, false);
            if (!NativeMethods.SetInformationJobObject(
                    handle,
                    NativeMethods.JobObjectInfoType.ExtendedLimitInformation,
                    pointer,
                    (uint)length))
            {
                var error = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new InvalidOperationException($"SetInformationJobObject failed ({error})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }

        return new JobObject(handle);
    }

    public bool TryAssign(System.Diagnostics.Process process) =>
        NativeMethods.AssignProcessToJobObject(_handle, process.Handle);

    public void Dispose() => _handle.Dispose();
}
