using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace VardyParty.Presentation;

/// <summary>
/// Minisign hashed signatures (<c>ED</c> / BLAKE2b-512 then Ed25519).
/// Public key is the same role as <c>vardyparty.cer</c> on Windows releases.
/// </summary>
public static class MinisignHashed
{
    public const string PublicKeyFileName = "minisign.pub";
    public const string SignatureSuffix = ".minisig";

    public static string SignFile(string path, byte[] keyId, byte[] seed, string? trustedComment = null)
    {
        using var stream = File.OpenRead(path);
        var comment = trustedComment ?? $"timestamp:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        return Sign(stream, keyId, seed, comment);
    }

    public static string Sign(Stream content, byte[] keyId, byte[] seed, string trustedComment)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(keyId);
        ArgumentNullException.ThrowIfNull(seed);
        if (keyId.Length != 8)
        {
            throw new ArgumentException("Minisign key id must be 8 bytes.", nameof(keyId));
        }

        if (seed.Length != Ed25519.SecretKeySize)
        {
            throw new ArgumentException("Minisign seed must be 32 bytes.", nameof(seed));
        }

        var hash = Blake2b512(content);
        var signature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(seed, 0, hash, 0, hash.Length, signature, 0);

        var commentBytes = Encoding.UTF8.GetBytes(trustedComment);
        var globalMessage = new byte[signature.Length + commentBytes.Length];
        Buffer.BlockCopy(signature, 0, globalMessage, 0, signature.Length);
        Buffer.BlockCopy(commentBytes, 0, globalMessage, signature.Length, commentBytes.Length);
        var globalSignature = new byte[Ed25519.SignatureSize];
        Ed25519.Sign(seed, 0, globalMessage, 0, globalMessage.Length, globalSignature, 0);

        var packed = new byte[2 + 8 + Ed25519.SignatureSize];
        packed[0] = (byte)'E';
        packed[1] = (byte)'D';
        Buffer.BlockCopy(keyId, 0, packed, 2, 8);
        Buffer.BlockCopy(signature, 0, packed, 10, signature.Length);

        return "untrusted comment: signature from Vardy Party minisign key\n"
            + Convert.ToBase64String(packed) + "\n"
            + "trusted comment: " + trustedComment + "\n"
            + Convert.ToBase64String(globalSignature) + "\n";
    }

    public static void VerifyFile(string path, string signatureText, string publicKeyText)
    {
        using var stream = File.OpenRead(path);
        Verify(stream, signatureText, publicKeyText);
    }

    public static void Verify(Stream content, string signatureText, string publicKeyText)
    {
        var publicKey = ParsePublicKey(publicKeyText);
        var signature = ParseSignature(signatureText);
        if (!publicKey.KeyId.AsSpan().SequenceEqual(signature.KeyId))
        {
            throw new InvalidOperationException("Minisign key id does not match the embedded public key.");
        }

        if (signature.Algorithm[0] != (byte)'E' || signature.Algorithm[1] != (byte)'D')
        {
            throw new InvalidOperationException("Only hashed minisign signatures (ED) are accepted.");
        }

        var hash = Blake2b512(content);
        if (!Ed25519.Verify(signature.Signature, 0, publicKey.PublicKey, 0, hash, 0, hash.Length))
        {
            throw new InvalidOperationException("Minisign signature is not valid for this file.");
        }

        var commentBytes = Encoding.UTF8.GetBytes(signature.TrustedComment);
        var globalMessage = new byte[signature.Signature.Length + commentBytes.Length];
        Buffer.BlockCopy(signature.Signature, 0, globalMessage, 0, signature.Signature.Length);
        Buffer.BlockCopy(commentBytes, 0, globalMessage, signature.Signature.Length, commentBytes.Length);
        if (!Ed25519.Verify(
                signature.GlobalSignature,
                0,
                publicKey.PublicKey,
                0,
                globalMessage,
                0,
                globalMessage.Length))
        {
            throw new InvalidOperationException("Minisign trusted-comment signature is not valid.");
        }
    }

    public static string FormatPublicKey(byte[] keyId, byte[] publicKey)
    {
        var packed = new byte[2 + 8 + Ed25519.PublicKeySize];
        packed[0] = (byte)'E';
        packed[1] = (byte)'d';
        Buffer.BlockCopy(keyId, 0, packed, 2, 8);
        Buffer.BlockCopy(publicKey, 0, packed, 10, Ed25519.PublicKeySize);
        var idHex = Convert.ToHexString(keyId);
        return "untrusted comment: minisign public key " + idHex + "\n"
            + Convert.ToBase64String(packed) + "\n";
    }

    public static (byte[] KeyId, byte[] Seed, byte[] PublicKey) GenerateKeyPair()
    {
        var keyId = RandomNumberGenerator.GetBytes(8);
        var seed = RandomNumberGenerator.GetBytes(Ed25519.SecretKeySize);
        var publicKey = new byte[Ed25519.PublicKeySize];
        Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);
        return (keyId, seed, publicKey);
    }

    public static string PackSecret(byte[] keyId, byte[] seed) =>
        Convert.ToBase64String(keyId.Concat(seed).ToArray());

    public static (byte[] KeyId, byte[] Seed) UnpackSecret(string packed)
    {
        var raw = Convert.FromBase64String(packed.Trim());
        if (raw.Length != 8 + Ed25519.SecretKeySize)
        {
            throw new InvalidOperationException("MINISIGN_SECRET_KEY must be 40 bytes base64 (key id + seed).");
        }

        return (raw[..8], raw[8..]);
    }

    private static byte[] Blake2b512(Stream content)
    {
        var digest = new Blake2bDigest(512);
        var buffer = new byte[81920];
        while (true)
        {
            var read = content.Read(buffer, 0, buffer.Length);
            if (read == 0)
            {
                break;
            }

            digest.BlockUpdate(buffer, 0, read);
        }

        var hash = new byte[digest.GetDigestSize()];
        digest.DoFinal(hash, 0);
        return hash;
    }

    private static ParsedPublicKey ParsePublicKey(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count < 2)
        {
            throw new InvalidOperationException("Minisign public key is incomplete.");
        }

        var bin = Convert.FromBase64String(lines[1]);
        if (bin.Length != 42 || bin[0] != (byte)'E' || bin[1] != (byte)'d')
        {
            throw new InvalidOperationException("Minisign public key is not an Ed25519 key.");
        }

        return new ParsedPublicKey(bin[2..10], bin[10..42]);
    }

    private static ParsedSignature ParseSignature(string text)
    {
        var lines = SplitLines(text);
        if (lines.Count < 4)
        {
            throw new InvalidOperationException("Minisign signature is incomplete.");
        }

        var bin = Convert.FromBase64String(lines[1]);
        if (bin.Length != 74)
        {
            throw new InvalidOperationException("Minisign signature blob is the wrong size.");
        }

        var trusted = lines[2];
        const string prefix = "trusted comment: ";
        if (!trusted.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Minisign signature is missing a trusted comment.");
        }

        var global = Convert.FromBase64String(lines[3]);
        if (global.Length != Ed25519.SignatureSize)
        {
            throw new InvalidOperationException("Minisign global signature is the wrong size.");
        }

        return new ParsedSignature(
            bin[0..2],
            bin[2..10],
            bin[10..74],
            trusted[prefix.Length..],
            global);
    }

    private static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        using var reader = new StringReader(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private sealed record ParsedPublicKey(byte[] KeyId, byte[] PublicKey);

    private sealed record ParsedSignature(
        byte[] Algorithm,
        byte[] KeyId,
        byte[] Signature,
        string TrustedComment,
        byte[] GlobalSignature);
}
