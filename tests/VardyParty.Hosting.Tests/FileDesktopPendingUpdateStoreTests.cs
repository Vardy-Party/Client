using System;
using System.IO;
using VardyParty.Hosting;
using VardyParty.Presentation;
using Xunit;

namespace VardyParty.Hosting.Tests;

public class FileDesktopPendingUpdateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "VardyPartyTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteReadClear_RoundTripsVersion()
    {
        // Arrange
        var path = Path.Combine(_dir, FileDesktopPendingUpdateStore.FileName);
        var sut = new FileDesktopPendingUpdateStore(path);
        var expected = new AppReleaseVersion(2, 1, 0, 160);

        // Act
        sut.Write(expected);
        var read = sut.Read();
        sut.Clear();
        var afterClear = sut.Read();

        // Assert
        Assert.Equal(expected, read);
        Assert.Null(afterClear);
    }

    [Fact]
    public void Read_MissingFile_ReturnsNull()
    {
        // Arrange
        var sut = new FileDesktopPendingUpdateStore(Path.Combine(_dir, "missing.txt"));

        // Act
        var read = sut.Read();

        // Assert
        Assert.Null(read);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
