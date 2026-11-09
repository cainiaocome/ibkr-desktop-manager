using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class SecretRedactorTests
{
    [Fact]
    public void Redact_replaces_registered_secrets()
    {
        var redactor = new SecretRedactor();
        redactor.Register("sup3rSecret");

        var result = redactor.Redact("typed sup3rSecret into the field");

        Assert.Equal("typed *** into the field", result);
    }

    [Fact]
    public void Redact_prefers_the_longest_match()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abcd");
        redactor.Register("abcdef");

        Assert.Equal("***", redactor.Redact("abcdef"));
    }

    [Fact]
    public void Short_values_are_ignored()
    {
        var redactor = new SecretRedactor();
        redactor.Register("abc");

        Assert.Equal("abc", redactor.Redact("abc"));
    }

    [Fact]
    public void Redact_handles_empty_input()
    {
        var redactor = new SecretRedactor();
        Assert.Equal(string.Empty, redactor.Redact(null));
        Assert.Equal(string.Empty, redactor.Redact(string.Empty));
    }

    [Fact]
    public void RedactingLog_scrubs_messages_and_exception_wrapping()
    {
        var sink = new RecordingLog();
        var redactor = new SecretRedactor();
        var log = new RedactingLog(sink, redactor);
        redactor.Register("topsecret");

        log.Info("using topsecret now");
        log.Warn("warn topsecret");
        log.Error("boom topsecret", new InvalidOperationException("topsecret"));

        Assert.Equal("using *** now", sink.InfoMessages.Single());
        Assert.Equal("warn ***", sink.Warnings.Single());
        Assert.Equal("boom ***", sink.Errors.Single());
    }
}
