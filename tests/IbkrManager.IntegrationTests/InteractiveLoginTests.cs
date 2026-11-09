using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// The one check that needs a real input desktop: SendInput keystrokes must reach the focused
/// control. Skipped (with a logged reason) on service sessions such as GitHub-hosted Windows
/// runners; run on a console/RDP session or a self-hosted interactive runner.
/// </summary>
[Collection("windows-ui")]
[Trait("Category", "InteractiveDesktop")]
public sealed class InteractiveLoginTests(TestHostFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Auto_login_types_both_fields_and_reaches_the_two_factor_prompt()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(fixture.HostAvailable, $"test host was not copied to {fixture.HostPath}");

        if (!DesktopSession.IsInteractive())
        {
            output.WriteLine(
                "SKIPPED: SendInput needs an interactive desktop ({0}). " +
                "This check runs on a console/RDP session or a self-hosted interactive runner.",
                DesktopSession.Describe());
            return;
        }

        // Start the stub with its output path, then let the manager attach to it.
        var host = fixture.StartHost([]);
        var harness = fixture.CreateHarness("test-user", "test-password");
        try
        {
            await harness.Manager.InitializeAsync();
            await harness.Manager.StartAsync();
            var state = await harness.Manager.AutoLoginAsync();
            output.WriteLine(string.Join(Environment.NewLine, harness.Log.Messages));

            Assert.True(File.Exists(host.OutputPath),
                "the stub received no keystrokes at all; log:\n" + string.Join(Environment.NewLine, harness.Log.Messages));
            var payload = TestHostFixture.ReadPayload(host.OutputPath);
            Assert.True(payload.GetProperty("submitted").GetBoolean(), $"the form was never submitted: {payload}");
            Assert.Equal("test-user", payload.GetProperty("username").GetString());
            Assert.Equal("test-password", payload.GetProperty("password").GetString());
            Assert.Equal(AppStatus.AwaitingTwoFactor, state.Status);
            Assert.Contains("2FA", state.LastAction, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }
}
