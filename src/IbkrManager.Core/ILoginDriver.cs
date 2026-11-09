namespace IbkrManager.Core;

/// <summary>Injects credentials into the login window. Implementations must never log secrets.</summary>
public interface ILoginDriver
{
    string Name { get; }

    Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default);
}

public sealed record LoginRequest(Secret Username, Secret Password, AppProfile Profile)
{
    public override string ToString() => $"LoginRequest(profile={Profile.DisplayName})";
}

/// <summary>
/// Outcome of one injection attempt. <see cref="ReachedTwoFactor"/> means the credentials were
/// accepted far enough for IBKR to ask for a security code — the manager stops there by design.
/// </summary>
public sealed record LoginResult(bool Success, string Message, bool ReachedTwoFactor = false)
{
    public static LoginResult Failed(string message) => new(false, message);

    public static LoginResult AwaitingTwoFactor(string message) => new(true, message, ReachedTwoFactor: true);

    public static LoginResult Submitted(string message) => new(true, message);
}
