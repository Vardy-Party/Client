using System;
using System.IO;
using System.Text;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class MinisignHashedTests
{
    [Fact]
    public void SignThenVerify_RoundTrips()
    {
        // Arrange
        var (keyId, seed, publicKey) = MinisignHashed.GenerateKeyPair();
        var pub = MinisignHashed.FormatPublicKey(keyId, publicKey);
        var payload = Encoding.UTF8.GetBytes("vardyparty-snap-bytes");
        using var stream = new MemoryStream(payload);

        // Act
        var signature = MinisignHashed.Sign(stream, keyId, seed, "timestamp:1");
        using var verify = new MemoryStream(payload);
        MinisignHashed.Verify(verify, signature, pub);

        // Assert
        Assert.Contains("trusted comment: timestamp:1", signature, StringComparison.Ordinal);
    }

    [Fact]
    public void EmbeddedLinuxPublicKey_IsNotAPlaceholder()
    {
        Assert.DoesNotContain("PLACEHOLDER", MinisignPublicKeys.Linux, StringComparison.Ordinal);
        Assert.DoesNotContain("REPLACE", MinisignPublicKeys.Linux, StringComparison.Ordinal);
        Assert.Contains("minisign public key", MinisignPublicKeys.Linux, StringComparison.Ordinal);
    }

    [Fact]
    public void Verify_TamperedPayload_Throws()
    {
        // Arrange
        var (keyId, seed, publicKey) = MinisignHashed.GenerateKeyPair();
        var pub = MinisignHashed.FormatPublicKey(keyId, publicKey);
        var payload = Encoding.UTF8.GetBytes("original");
        using var stream = new MemoryStream(payload);
        var signature = MinisignHashed.Sign(stream, keyId, seed, "t");

        // Act
        using var tampered = new MemoryStream(Encoding.UTF8.GetBytes("tampered"));
        var ex = Assert.Throws<InvalidOperationException>(() =>
            MinisignHashed.Verify(tampered, signature, pub));

        // Assert
        Assert.Contains("not valid", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
