using Xunit;

namespace IbkrManager.Core.Tests;

public sealed class SecretTests
{
    [Fact]
    public void ToString_never_returns_the_value()
    {
        var secret = new Secret("hunter2");

        Assert.Equal("***", secret.ToString());
        Assert.DoesNotContain("hunter2", secret.ToString());
    }

    [Fact]
    public void Reveal_returns_the_value_and_dispose_clears_it()
    {
        var secret = new Secret("hunter2");
        Assert.Equal("hunter2", secret.Reveal());

        secret.Dispose();
        Assert.Throws<ObjectDisposedException>(() => secret.Reveal());
        Assert.True(secret.IsEmpty);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void From_returns_null_for_empty_values(string? value) => Assert.Null(Secret.From(value));

    [Fact]
    public void Matches_compares_values_without_leaking_them()
    {
        using var a = new Secret("same");
        using var b = new Secret("same");
        using var c = new Secret("other");

        Assert.True(a.Matches(b));
        Assert.False(a.Matches(c));
        Assert.False(a.Matches(null));
    }

    [Fact]
    public void Length_is_exposed_but_not_content()
    {
        var secret = new Secret("abcd");
        Assert.Equal(4, secret.Length);
    }
}
