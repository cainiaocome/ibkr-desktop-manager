namespace IbkrManager.Core;

/// <summary>Where the IBKR Desktop executable was found and which version it claims to be.</summary>
public sealed record AppInstallation(string ExecutablePath, string InstallDirectory, string? Version)
{
    public string FileName => Path.GetFileName(ExecutablePath);

    public override string ToString() => $"{ExecutablePath} ({Version ?? "unknown version"})";
}
