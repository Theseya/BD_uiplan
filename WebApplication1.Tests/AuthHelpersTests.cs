using WebApplication1;
using Xunit;

namespace WebApplication1.Tests;

public class AuthHelpersTests
{
    [Fact]
    public void HashPassword_ReturnsNonEmptyBase64()
    {
        var hash = AuthHelpers.HashPassword("test");
        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
        Assert.True(Convert.FromBase64String(hash).Length > 0);
    }

    [Fact]
    public void HashPassword_DifferentCallsProduceDifferentHashes_DueToSalt()
    {
        var h1 = AuthHelpers.HashPassword("same");
        var h2 = AuthHelpers.HashPassword("same");
        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void VerifyPassword_ValidPassword_ReturnsTrue()
    {
        var password = "correct";
        var hash = AuthHelpers.HashPassword(password);
        Assert.True(AuthHelpers.VerifyPassword(password, hash));
    }

    [Fact]
    public void VerifyPassword_WrongPassword_ReturnsFalse()
    {
        var hash = AuthHelpers.HashPassword("correct");
        Assert.False(AuthHelpers.VerifyPassword("wrong", hash));
    }

    [Fact]
    public void VerifyPassword_InvalidBase64_ReturnsFalse()
    {
        Assert.False(AuthHelpers.VerifyPassword("any", "not-valid-base64!!!"));
    }

    [Fact]
    public void VerifyPassword_EmptyPassword_WithValidHash()
    {
        var hash = AuthHelpers.HashPassword("");
        Assert.True(AuthHelpers.VerifyPassword("", hash));
    }
}
