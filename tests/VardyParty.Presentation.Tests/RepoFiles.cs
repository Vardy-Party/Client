using System;
using System.IO;

namespace VardyParty.Presentation.Tests;

/// <summary>Paths into the repository, for tests that pin source or resource files.</summary>
internal static class RepoFiles
{
    private static readonly Lazy<string> Root = new(FindRoot);

    /// <summary>Absolute path of <paramref name="parts"/> under the repository root.</summary>
    public static string Path(params string[] parts) =>
        System.IO.Path.Combine([Root.Value, .. parts]);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "VardyParty.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("VardyParty.slnx not found above the test output.");
    }
}