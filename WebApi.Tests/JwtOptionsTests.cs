using WebApi.Auth;

namespace WebApi.Tests;

[TestClass]
public class JwtOptionsTests
{
    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("short")]
    public void MissingOrShortSigningKeyIsRejected(string? key)
    {
        var options = new JwtOptions { Key = key! };
        Assert.ThrowsException<InvalidOperationException>(() => options.Validate());
    }

    [TestMethod]
    public void SigningKeyWithAtLeastThirtyTwoBytesIsAccepted()
    {
        var options = new JwtOptions { Key = new string('x', 32) };
        options.Validate();
    }

    [TestMethod]
    public void TemporarySigningKeysAreGeneratedWhenAllowedAndAreDifferent()
    {
        var first = new JwtOptions();
        var second = new JwtOptions();
        first.PrepareSigningKey(allowTemporaryKey: true);
        second.PrepareSigningKey(allowTemporaryKey: true);
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(first.Key) >= 32);
        Assert.AreNotEqual(first.Key, second.Key);
    }

    [TestMethod]
    public void MissingOrShortKeyIsRejectedWhenTemporaryKeyIsNotAllowed()
    {
        Assert.ThrowsException<InvalidOperationException>(() => new JwtOptions().PrepareSigningKey(false));
        Assert.ThrowsException<InvalidOperationException>(() => new JwtOptions { Key = "short" }.PrepareSigningKey(true));
    }

    [TestMethod]
    public void ConfiguredKeyIsKeptForDevelopmentAndProduction()
    {
        var expected = new string('x', 32);
        var options = new JwtOptions { Key = expected };
        options.PrepareSigningKey(true);
        options.PrepareSigningKey(false);
        Assert.AreEqual(expected, options.Key);
    }
}
