namespace IbkrManager.Core;

/// <summary>Finds the installed IBKR Desktop executable.</summary>
public interface IAppLocator
{
    /// <summary>Returns the installation, or <c>null</c> when the app is not installed.</summary>
    AppInstallation? Locate();
}
