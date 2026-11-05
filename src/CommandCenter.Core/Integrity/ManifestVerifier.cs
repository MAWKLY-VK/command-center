using System.Security.Cryptography;
using System.Text;

namespace CommandCenter.Core.Integrity
{
    // What a signed file is for. The kind is part of what is signed, so a signature for one kind never passes for another.
    public enum SignedKind
    {
        Update,
        GoProfile,
        MapCatalog,
    }

    // Checks the maintainer's signatures (ADR 0007, Format): ECDSA P-256 with SHA-256 over "command-center:<kind>", a
    // line feed and the content, the signature in IEEE P1363 form written as base64. Nothing here signs: the private key
    // never leaves the maintainer's PC.
    public static class ManifestVerifier
    {
        // The maintainer's public key, exactly as recorded in ADR 0007
        private const string PublicKeyPem =
            "-----BEGIN PUBLIC KEY-----\n" +
            "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEwn5YzHoO5ed3WvhQ+UyGIJ/eacXk\n" +
            "SDLHjH9varfLZbZ2HzPJB4VeY5iPT/W4rrRasFpEeVGJqbUmxYGBvMKsAg==\n" +
            "-----END PUBLIC KEY-----";

        // Its SubjectPublicKeyInfo (DER)
        internal static readonly byte[] BuiltInKey = KeyFromPem(PublicKeyPem);

        // A second key that only test builds (--test-key) and the tests set, so the update path can be tried without the
        // real key. Release builds never set it.
        internal static byte[]? TestKey { get; set; }

        private const int SignatureLength = 64;

        // True when the signature is the maintainer's (or the test key's) for this kind and content; otherwise false, with
        // the reason. It never throws for bad input.
        public static bool Verify(SignedKind kind, ReadOnlySpan<byte> content, string? signature, out string reason)
        {
            if (string.IsNullOrWhiteSpace(signature))
            {
                reason = "It has no signature.";
                return false;
            }
            Span<byte> decoded = stackalloc byte[SignatureLength + 3];
            if (!Convert.TryFromBase64String(signature.Trim(), decoded, out int length) || length != SignatureLength)
            {
                reason = "Its signature is damaged.";
                return false;
            }

            byte[] prefix = Encoding.UTF8.GetBytes("command-center:" + KindName(kind) + "\n");
            byte[] message = new byte[prefix.Length + content.Length];
            prefix.CopyTo(message, 0);
            content.CopyTo(message.AsSpan(prefix.Length));

            foreach (byte[]? key in new[] { BuiltInKey, TestKey })
            {
                if (key != null && Matches(key, message, decoded[..SignatureLength]))
                {
                    reason = "";
                    return true;
                }
            }
            reason = "Its signature does not match: it was changed, or it was not signed by Command Center's maintainer.";
            return false;
        }

        internal static string KindName(SignedKind kind) => kind switch
        {
            SignedKind.Update => "update",
            SignedKind.GoProfile => "go-profile",
            SignedKind.MapCatalog => "map-catalog",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        // A public key in PEM form, as its SubjectPublicKeyInfo
        internal static byte[] KeyFromPem(string pem)
        {
            using var key = ECDsa.Create();
            key.ImportFromPem(pem);
            return key.ExportSubjectPublicKeyInfo();
        }

        private static bool Matches(byte[] key, byte[] message, ReadOnlySpan<byte> signature)
        {
            try
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(key, out _);
                return ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (CryptographicException)
            {
                return false;
            }
        }
    }
}
