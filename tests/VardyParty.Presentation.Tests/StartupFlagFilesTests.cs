using System;
using System.IO;
using VardyParty.Ports;
using Xunit;

namespace VardyParty.Presentation.Tests;

public class StartupFlagFilesTests
{
    [Fact]
    public void Find_FlagFilePresent_ReturnsItsPath()
    {
        // Arrange
        var flagDir = Path.Combine(Path.GetTempPath(), "vardyparty-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flagDir);
        var flagPath = Path.Combine(flagDir, "no-sound");
        File.WriteAllText(flagPath, string.Empty);

        try
        {
            // Act
            var found = StartupFlagFiles.Find("no-sound", [flagDir]);

            // Assert
            Assert.Equal(flagPath, found);
        }
        finally
        {
            Directory.Delete(flagDir, recursive: true);
        }
    }

    [Fact]
    public void Find_FlagFileAbsent_ReturnsNull()
    {
        // Arrange
        var flagDir = Path.Combine(Path.GetTempPath(), "vardyparty-flags-" + Guid.NewGuid().ToString("N"));

        // Act
        var found = StartupFlagFiles.Find("no-chrome", [flagDir]);

        // Assert
        Assert.Null(found);
    }

    [Fact]
    public void Find_LaterDirectoryHoldsTheFlag_ProbesAllCandidates()
    {
        // Arrange
        var missingDir = Path.Combine(Path.GetTempPath(), "vardyparty-flags-" + Guid.NewGuid().ToString("N"));
        var flagDir = Path.Combine(Path.GetTempPath(), "vardyparty-flags-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(flagDir);
        var flagPath = Path.Combine(flagDir, "no-chrome");
        File.WriteAllText(flagPath, string.Empty);

        try
        {
            // Act
            var found = StartupFlagFiles.Find("no-chrome", [missingDir, flagDir]);

            // Assert
            Assert.Equal(flagPath, found);
        }
        finally
        {
            Directory.Delete(flagDir, recursive: true);
        }
    }

    [Fact]
    public void CandidateFlagDirectories_EndInVardyPartyFlags()
    {
        // Arrange
        // (candidates derive from per-user special folders on the current machine)

        // Act
        var candidates = StartupFlagFiles.CandidateFlagDirectories();

        // Assert
        Assert.All(candidates, dir =>
            Assert.EndsWith(Path.Combine("VardyParty", "flags"), dir));
    }
}
