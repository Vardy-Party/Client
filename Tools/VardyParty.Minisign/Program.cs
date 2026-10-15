using VardyParty.Presentation;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: generate | sign <file>");
    return 1;
}

if (args[0] == "generate")
{
    var (keyId, seed, publicKey) = MinisignHashed.GenerateKeyPair();
    var repo = FindRepoRoot();
    var pubPath = Path.Combine(repo, "packaging", MinisignHashed.PublicKeyFileName);
    Directory.CreateDirectory(Path.GetDirectoryName(pubPath)!);
    File.WriteAllText(pubPath, MinisignHashed.FormatPublicKey(keyId, publicKey));
    UpsertDotEnv(
        Path.Combine(repo, ".env"),
        "MINISIGN_SECRET_KEY",
        MinisignHashed.PackSecret(keyId, seed));
    Console.WriteLine($"Wrote {pubPath}");
    Console.WriteLine("Wrote MINISIGN_SECRET_KEY to .env (gitignored). Sync with scripts/sync-github-secrets-from-env.ps1.");
    return 0;
}

if (args[0] == "sign" && args.Length >= 2)
{
    var secret = ResolveSecret();
    if (string.IsNullOrWhiteSpace(secret))
    {
        Console.Error.WriteLine("MINISIGN_SECRET_KEY is not set in the environment or .env.");
        return 1;
    }

    var (keyId, seed) = MinisignHashed.UnpackSecret(secret);
    var file = Path.GetFullPath(args[1]);
    var sig = MinisignHashed.SignFile(file, keyId, seed);
    var sigPath = file + MinisignHashed.SignatureSuffix;
    File.WriteAllText(sigPath, sig);
    Console.WriteLine(sigPath);
    return 0;
}

Console.Error.WriteLine("Usage: generate | sign <file>");
return 1;

static string? ResolveSecret()
{
    var fromEnv = Environment.GetEnvironmentVariable("MINISIGN_SECRET_KEY");
    if (!string.IsNullOrWhiteSpace(fromEnv))
    {
        return fromEnv.Trim();
    }

    return ReadDotEnvValue(Path.Combine(FindRepoRoot(), ".env"), "MINISIGN_SECRET_KEY");
}

static string? ReadDotEnvValue(string envPath, string key)
{
    if (!File.Exists(envPath))
    {
        return null;
    }

    foreach (var line in File.ReadAllLines(envPath))
    {
        var trim = line.Trim();
        if (trim.Length == 0 || trim.StartsWith('#'))
        {
            continue;
        }

        var eq = trim.IndexOf('=');
        if (eq < 1)
        {
            continue;
        }

        if (!string.Equals(trim[..eq].Trim(), key, StringComparison.Ordinal))
        {
            continue;
        }

        return trim[(eq + 1)..].Trim().Trim('"').Trim('\'');
    }

    return null;
}

static void UpsertDotEnv(string envPath, string key, string value)
{
    var lines = File.Exists(envPath)
        ? File.ReadAllLines(envPath).ToList()
        : [];
    var found = false;
    for (var i = 0; i < lines.Count; i++)
    {
        var trim = lines[i].Trim();
        if (trim.Length == 0 || trim.StartsWith('#'))
        {
            continue;
        }

        var eq = trim.IndexOf('=');
        if (eq < 1)
        {
            continue;
        }

        if (!string.Equals(trim[..eq].Trim(), key, StringComparison.Ordinal))
        {
            continue;
        }

        lines[i] = key + "=" + value;
        found = true;
        break;
    }

    if (!found)
    {
        if (lines.Count > 0 && lines[^1].Length > 0)
        {
            lines.Add(string.Empty);
        }

        lines.Add(key + "=" + value);
    }

    File.WriteAllLines(envPath, lines);
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Version.props")))
        {
            return dir.FullName;
        }

        dir = dir.Parent;
    }

    return Directory.GetCurrentDirectory();
}
