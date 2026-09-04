using System;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class AppxSignaturePayloadTests
{
    [Fact]
    public void Unwrap_PkcxFileId_StripsFourByteHeader()
    {
        // Arrange
        var pkcs7 = new byte[] { 0x30, 0x82, 0x06, 0x1B };
        var p7x = new byte[] { (byte)'P', (byte)'K', (byte)'C', (byte)'X', 0x30, 0x82, 0x06, 0x1B };

        // Act
        var unwrapped = AppxSignaturePayload.Unwrap(p7x);

        // Assert
        Assert.Equal(pkcs7, unwrapped);
    }

    [Fact]
    public void Unwrap_AppxFileId_StripsFourByteHeader()
    {
        // Arrange
        var pkcs7 = new byte[] { 0x30, 0x82, 0x00, 0x01 };
        var p7x = new byte[] { (byte)'A', (byte)'P', (byte)'P', (byte)'X', 0x30, 0x82, 0x00, 0x01 };

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
