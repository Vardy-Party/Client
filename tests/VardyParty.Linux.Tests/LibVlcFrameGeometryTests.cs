using System;
using System.Runtime.InteropServices;
using VardyParty.Linux.Services;
using Xunit;

namespace VardyParty.Linux.Tests;

public class LibVlcFrameGeometryTests
{
    [Theory]
    [InlineData(1u, 32u)]
    [InlineData(32u, 32u)]
    [InlineData(33u, 64u)]
    public void Align32_RoundsUpToMultipleOf32(uint value, uint expected)
    {
        // Arrange
        // Act
        var aligned = LibVlcFrameGeometry.Align32(value);

        // Assert
        Assert.Equal(expected, aligned);
        Assert.Equal(0u, aligned % 32);
    }

    [Fact]
    public void PitchAndLines_Are32ByteAlignedForTypicalHd()
    {
        // Arrange
        const int width = 1920;
        const int height = 1080;

        // Act
        var pitch = LibVlcFrameGeometry.Pitch(width);
        var lines = LibVlcFrameGeometry.Lines(height);
        var bytes = LibVlcFrameGeometry.BufferByteLength(width, height);

        // Assert
        Assert.Equal(1920 * 4, pitch);
        Assert.Equal(1088, lines);
        Assert.Equal(pitch * lines, bytes);
        Assert.Equal(0, pitch % 32);
        Assert.Equal(0, lines % 32);
    }

    [Fact]
    public void Pitch_NarrowWidth_PadsTo32()
    {
        // Arrange
        // Act
        var pitch = LibVlcFrameGeometry.Pitch(1);

        // Assert
        Assert.Equal(32, pitch);
    }

    [Fact]
    public void BufferByteLength_RejectsEmptyFrames()
    {
        // Arrange
        // Act
        // Assert
        Assert.Throws<ArgumentOutOfRangeException>(() => LibVlcFrameGeometry.BufferByteLength(0, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => LibVlcFrameGeometry.BufferByteLength(1920, 0));
    }

    [Fact]
    public void WriteRv32Chroma_WritesFourcc()
    {
        // Arrange
        var chroma = Marshal.AllocHGlobal(4);
        try
        {
            // Act
            LibVlcFrameGeometry.WriteRv32Chroma(chroma);

            // Assert
            Assert.Equal((byte)'R', Marshal.ReadByte(chroma, 0));
            Assert.Equal((byte)'V', Marshal.ReadByte(chroma, 1));
            Assert.Equal((byte)'3', Marshal.ReadByte(chroma, 2));
            Assert.Equal((byte)'2', Marshal.ReadByte(chroma, 3));
        }
        finally
        {
            Marshal.FreeHGlobal(chroma);
        }
    }

    [Fact]
    public void WriteRv32Chroma_Null_Throws()
    {
        // Arrange
        // Act
        // Assert
        Assert.Throws<ArgumentNullException>(() => LibVlcFrameGeometry.WriteRv32Chroma(IntPtr.Zero));
    }

    [Fact]
    public void CapMaxWidth_LeavesSmallerFramesAlone()
    {
        // Arrange
        // Act
        var same = LibVlcFrameGeometry.CapMaxWidth(1280, 720, 1280);
        var unused = LibVlcFrameGeometry.CapMaxWidth(1920, 1080, 0);

        // Assert
        Assert.Equal((1280u, 720u), same);
        Assert.Equal((1920u, 1080u), unused);
    }

    [Fact]
    public void CapMaxWidth_Scales1080pTo720pAndKeepsEvenHeight()
    {
        // Arrange
        // Act
        var capped = LibVlcFrameGeometry.CapMaxWidth(1920, 1080, 1280);

        // Assert
        Assert.Equal(1280u, capped.Width);
        Assert.Equal(720u, capped.Height);
        Assert.Equal(0u, capped.Height % 2);
    }
}

public class LibVlcFramePresentGateTests
{
    [Fact]
    public void TryBeginPresent_IsOneInFlight()
    {
        // Arrange
        var sut = new LibVlcFramePresentGate();

        // Act
        var first = sut.TryBeginPresent();
        var second = sut.TryBeginPresent();
        sut.EndPresent();
        var third = sut.TryBeginPresent();

        // Assert
        Assert.True(first);
        Assert.False(second);
        Assert.True(third);
    }

    [Fact]
    public void TryBeginPresent_MinInterval_RejectsUntilElapsed()
    {
        // Arrange
        var sut = new LibVlcFramePresentGate();

        // Act
        var first = sut.TryBeginPresent(minIntervalMs: 10_000);
        sut.EndPresent();
        var second = sut.TryBeginPresent(minIntervalMs: 10_000);

        // Assert
        Assert.True(first);
        Assert.False(second);
    }
}
