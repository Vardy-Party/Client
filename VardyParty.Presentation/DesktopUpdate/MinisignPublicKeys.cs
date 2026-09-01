namespace VardyParty.Presentation;

/// <summary>
/// Embedded minisign public key (same role as the installed MSIX signer).
/// Also published on GitHub releases as <c>minisign.pub</c>.
/// Generated locally; secret stays in gitignored <c>.env</c> and GitHub Actions.
/// </summary>
public static class MinisignPublicKeys
{
    public static string Linux { get; } = """
        untrusted comment: minisign public key 6CD0A7CEA4CEE4FC
        RWRs0KfOpM7k/N+qmjdXlqDoAlJq4maZRwxX1esZhjxQjbvMACVC7O/r
        """;
}
