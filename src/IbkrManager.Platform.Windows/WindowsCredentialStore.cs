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

    private readonly IUserEnvironment _environment;
    private readonly Func<AppSettings> _settings;
    private readonly IAppLog _log;

    public WindowsCredentialStore(IUserEnvironment environment, Func<AppSettings> settings, IAppLog log)
    {
        _environment = environment;
        _settings = settings;
        _log = log;
    }

    public CredentialSource Source => CredentialSource.CredentialManager;

    public string DisplayName => "stored credential";

    public bool IsSupported => OperatingSystem.IsWindows();

    public bool IsAvailable => IsSupported;

    public string? Read(CredentialKind kind)
    {
        if (!IsSupported)
        {
            return null;
        }

        var target = TargetFor(kind);
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

    public bool HasStoredValue(CredentialKind kind) => Read(kind) is { Length: > 0 };

    public Task SaveAsync(Secret? username, Secret? password, CredentialWriteOptions options, CancellationToken cancellationToken = default)
    {
        if (options.PersistInCredentialManager)
        {
            WriteOrDelete(CredentialKind.Username, username);
            WriteOrDelete(CredentialKind.Password, password);
            _log.Info("Stored credentials in Windows Credential Manager");
        }
        else
        {
            Delete(CredentialKind.Username);
            Delete(CredentialKind.Password);
        }

        if (options.WriteUserEnvironment)
        {
            var settings = _settings();
            WriteEnvironment(settings.UsernameVariable, username);
            WriteEnvironment(settings.PasswordVariable, password);
            _log.Warn("Wrote credentials to the user environment (plaintext in HKCU\\Environment)");
        }

        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        Delete(CredentialKind.Username);
        Delete(CredentialKind.Password);

        var settings = _settings();
        _environment.Delete(settings.UsernameVariable);
        _environment.Delete(settings.PasswordVariable);

        _log.Info("Cleared stored credentials and managed user environment variables");
        return Task.CompletedTask;
    }

    private void WriteOrDelete(CredentialKind kind, Secret? secret)
    {
        if (secret is null || secret.IsEmpty)
        {
            Delete(kind);
            return;
        }

        var target = TargetFor(kind);
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

    private void Delete(CredentialKind kind)
    {
        // ERROR_NOT_FOUND (1168) simply means nothing was stored.
        if (!NativeMethods.CredDelete(TargetFor(kind), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
            {
                _log.Warn($"CredDelete failed for {kind} ({error})");
            }
        }
    }

    private void WriteEnvironment(string name, Secret? secret)
    {
        if (secret is null || secret.IsEmpty)
        {
            _environment.Delete(name);
            return;
        }

        _environment.Write(name, secret.Reveal());
    }

    private static string TargetFor(CredentialKind kind) => $"{TargetPrefix}/{kind.ToString().ToLowerInvariant()}";
}
