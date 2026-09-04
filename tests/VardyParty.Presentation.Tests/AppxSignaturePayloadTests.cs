using System;
using System.Text;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class AppxSignaturePayloadTests
{
    [Theory]
    [InlineData("PKCX")]
    [InlineData("APPX")]
    public void Unwrap_KnownSipFileId_StripsMagicPrefix(string magic)
    {
        // Arrange
        var pkcs7 = new byte[] { 0x30, 0x82, 0x06, 0x1B };
        var magicBytes = Encoding.ASCII.GetBytes(magic);
        var p7x = new byte[magicBytes.Length + pkcs7.Length];
        magicBytes.CopyTo(p7x, 0);
        pkcs7.CopyTo(p7x, magicBytes.Length);

        // Act
        var unwrapped = AppxSignaturePayload.Unwrap(p7x);

        // Assert
        Assert.Equal(pkcs7, unwrapped);
    }

    [Fact]
    public void Unwrap_AlreadyPkcs7_LeavesBytesUnchanged()
    {
        // Arrange
        var pkcs7 = new byte[] { 0x30, 0x82, 0x06, 0x1B };

        // Act
        var unwrapped = AppxSignaturePayload.Unwrap(pkcs7);

        // Assert
        Assert.Equal(pkcs7, unwrapped);
    }

    [Fact]
    public void Unwrap_Null_Throws()
    {
        // Arrange
        byte[]? data = null;

        // Act
        var ex = Assert.Throws<ArgumentNullException>(() => AppxSignaturePayload.Unwrap(data!));

        // Assert
        Assert.Equal("data", ex.ParamName);
    }
}
