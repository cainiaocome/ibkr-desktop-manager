namespace IbkrManager.Core;

/// <summary>
/// Tries drivers in order and stops at the first one that applies. UI Automation is preferred
/// (deterministic, no focus or tab-order dependency); SendInput remains the fallback.
/// </summary>
public sealed class LoginDriverChain(params ILoginDriver[] drivers) : ILoginDriver
{
    public string Name => string.Join(" -> ", drivers.Select(driver => driver.Name));

    public async Task<LoginResult> LoginAsync(LoginRequest request, CancellationToken cancellationToken = default)
    {
        LoginResult? last = null;
        foreach (var driver in drivers)
        {
            var result = await driver.LoginAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.NotApplicable)
            {
                return result;
            }

            last = result;
        }

        return last ?? LoginResult.Failed("No login driver is configured");
    }
}
