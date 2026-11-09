using Xunit;
using Xunit.Abstractions;

namespace IbkrManager.IntegrationTests;

/// <summary>
/// End-to-end check of the pointer login driver against a real WinForms window: UI Automation
/// supplies the field rectangles, the driver clicks them and types with SendInput, and the stub
/// reports what it actually received. Needs an input desktop, so it is skipped on service sessions.
/// </summary>
[Collection("windows-ui")]
[Trait("Category", "InteractiveDesktop")]
public sealed class PointerLoginTests(TestHostFixture fixture, ITestOutputHelper output)
{
    [Fact]
    public async Task Clicking_the_fields_and_typing_reaches_the_app()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.True(fixture.HostAvailable, $"test host was not copied to {fixture.HostPath}");

        if (!InteractiveSession.IsInteractive())
        {
            output.WriteLine(
                "SKIPPED: synthetic mouse/keyboard input needs an interactive desktop ({0}).",
                InteractiveSession.Describe());
            return;
        }

        var harness = fixture.CreateHarness();
        try
        {
            await harness.Manager.InitializeAsync();
            await harness.Manager.StartAsync();
            await harness.Manager.TickAsync();
            harness.Log.Info(string.Join(Environment.NewLine, harness.Log.Messages));

            var controls = await WaitForControlsAsync(harness);
            Assert.NotNull(controls);
            output.WriteLine($"controls: {controls}");
            Assert.False(controls!.UsernameBounds.IsEmpty, "UI Automation should report the username field rectangle");
            Assert.False(controls.PasswordBounds.IsEmpty, "UI Automation should report the password field rectangle");

            var result = await harness.PointerDriver.LoginAsync(
                new LoginRequest(new Secret("pointer-user"), new Secret("pointer-password"), fixture.Profile));

            output.WriteLine(string.Join(Environment.NewLine, harness.Log.Messages));
            Assert.True(result.Success, result.Message);

            var payload = TestHostFixture.ReadPayload(harness.OutputPath);
            Assert.True(payload.GetProperty("submitted").GetBoolean(), $"the form was never submitted: {payload}");
            Assert.Equal("pointer-user", payload.GetProperty("username").GetString());
            Assert.Equal("pointer-password", payload.GetProperty("password").GetString());
        }
        finally
        {
            await harness.Manager.QuitAsync(force: true);
        }
    }

    private static async Task<UiLoginControls?> WaitForControlsAsync(Harness harness)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        UiLoginControls? controls = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            controls = harness.Uia.FindLoginControls();
            if (controls is not null)
            {
                return controls;
            }

            await Task.Delay(500);
        }

        return controls;
    }
}
