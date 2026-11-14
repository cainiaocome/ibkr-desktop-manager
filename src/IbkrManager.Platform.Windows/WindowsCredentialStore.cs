using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace IbkrManager.Platform.Windows;

/// <summary>
/// Credential storage backed by Windows Credential Manager (DPAPI, per user). This is the default
/// place for the password because, unlike an environment variable, it is not plaintext at rest
/// and is not inherited by every child process.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const string TargetPrefix = "IbkrDesktopManager";

    private readonly IAppLog _log;

    public WindowsCredentialStore(IAppLog log) => _log = log;

    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "stored credential";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsAvailable => IsSupported;

    public string? Read(string accountId, CredentialKind kind) => ReadTarget(TargetFor(accountId, kind));

    public string? ReadLegacy(CredentialKind kind) => ReadTarget(LegacyTargetFor(kind));

    private string? ReadTarget(string target)
    {
        if (!IsSupported)
        {
            return null;
        }

        var handle = IntPtr.Zero;
        try
        {
            if (!NativeMethods.CredRead(target, CredentialTypeGeneric, 0, out handle))
            {
                return null;
            }

            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(handle);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var value = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            CryptographicOperations.ZeroMemory(bytes);
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _log.Warn("Credential Manager API is unavailable on this system");
            return null;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeMethods.CredFree(handle);
            }
        }
    }

    public bool HasStoredValue(string accountId, CredentialKind kind) => Read(accountId, kind) is { Length: > 0 };

    public Task SaveAsync(string accountId, Secret? username, Secret? password, CancellationToken cancellationToken = default)
    {
        WriteOrDelete(TargetFor(accountId, CredentialKind.Username), username);
        WriteOrDelete(TargetFor(accountId, CredentialKind.Password), password);
        _log.Info("Stored the account's credentials in Windows Credential Manager");
        return Task.CompletedTask;
    }

    public Task ClearAsync(string accountId, CancellationToken cancellationToken = default)
    {
        Delete(TargetFor(accountId, CredentialKind.Username));
        Delete(TargetFor(accountId, CredentialKind.Password));
        _log.Info("Cleared the account's stored credentials");
        return Task.CompletedTask;
    }

    private void WriteOrDelete(string target, Secret? secret)
    {
        if (secret is null || secret.IsEmpty)
        {
            Delete(target);
            return;
        }

        var value = secret.Reveal();

        // CredWrite expects the blob as a UTF-16 string without a terminator.
        var bytes = Encoding.Unicode.GetBytes(value);
        var blob = Marshal.AllocHGlobal(bytes.Length);
        var targetPtr = Marshal.StringToHGlobalUni(target);
        var userPtr = Marshal.StringToHGlobalUni(Environment.UserName);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new NativeMethods.CREDENTIAL
            {
                Type = CredentialTypeGeneric,
                TargetName = targetPtr,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)bytes.Length,
                Persist = CredentialPersistLocalMachine,
                UserName = userPtr,
                Comment = IntPtr.Zero,
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new InvalidOperationException($"CredWrite failed ({Marshal.GetLastWin32Error()})");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blob);
            Marshal.FreeHGlobal(targetPtr);
            Marshal.FreeHGlobal(userPtr);
        }
    }

    private void Delete(string target)
    {
        // ERROR_NOT_FOUND (1168) simply means nothing was stored.
        if (!NativeMethods.CredDelete(target, CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                _log.Warn($"CredDelete failed for {target} ({error})");
            }
        }
    }

    private static string TargetFor(string accountId, CredentialKind kind) =>
        $"{TargetPrefix}/{accountId}/{kind.ToString().ToLowerInvariant()}";

    /// <summary>The 0.3.x target: one un-named pair.</summary>
    private static string LegacyTargetFor(CredentialKind kind) =>
        $"{TargetPrefix}/{kind.ToString().ToLowerInvariant()}";
}
