namespace IbkrManager.Core;

/// <summary>Chooses the login driver at call time so a settings change applies immediately.</summary>
public sealed class SelectingLoginDriver(Func<string> driverName, ILoginDriver sendInput, ILoginDriver manual) : ILoginDriver
{
    public string Name => IsManual() ? manual.Name : sendInput.Name;

    public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        (IsManual() ? manual : sendInput).LoginAsync(request, cancellationToken);

    private bool IsManual() => string.Equals(driverName(), "manual", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Never types anything; used when auto-login is disabled in settings.</summary>
public sealed class ManualLoginDriver : ILoginDriver
{
    public string Name => "manual";

    public Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(LoginResult.Failed(
            "Auto Login is disabled in Settings (login driver is \"manual\"). Type the credentials in the IBKR window."));
}
