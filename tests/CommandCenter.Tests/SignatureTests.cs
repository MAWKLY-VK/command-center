using System.Security.Cryptography;
using System.Text;

namespace CommandCenter.Tests
{
    // Signed files (ADR 0007, Format): ECDSA P-256 with SHA-256 over "command-center:<kind>", a line feed and the
    // content; the signature in IEEE P1363 form, written as base64. Keys are made here; the real key never is.
    public sealed class SignatureTests : IDisposable
    {
        private static readonly byte[] Content = Encoding.UTF8.GetBytes("{\"schema\":1,\"api\":\"https://example.org/\"}");
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly ECDsa _otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        public SignatureTests() => ManifestVerifier.TestKey = _key.ExportSubjectPublicKeyInfo();

        private static string Sign(ECDsa key, string kind, byte[] content)
        {
            byte[] message = Encoding.UTF8.GetBytes("command-center:" + kind + "\n").Concat(content).ToArray();
            return Convert.ToBase64String(key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        [Theory]
        [InlineData(SignedKind.Update, "update")]
        [InlineData(SignedKind.GoProfile, "go-profile")]
        [InlineData(SignedKind.MapCatalog, "map-catalog")]
        public void A_signature_for_its_kind_passes(SignedKind kind, string name)
        {
            Assert.True(ManifestVerifier.Verify(kind, Content, Sign(_key, name, Content), out string reason));
            Assert.Equal("", reason);
        }

        [Fact]
        public void The_signature_file_may_end_with_a_line_break() =>
            Assert.True(ManifestVerifier.Verify(SignedKind.GoProfile, Content, Sign(_key, "go-profile", Content) + "\r\n", out _));

        [Fact]
        public void Changed_content_fails()
        {
            byte[] changed = Encoding.UTF8.GetBytes("{\"schema\":1,\"api\":\"https://example.com/\"}");
            Assert.False(ManifestVerifier.Verify(SignedKind.GoProfile, changed, Sign(_key, "go-profile", Content), out string reason));
            Assert.NotEqual("", reason);
        }

        [Fact]
        public void A_signature_for_another_kind_fails() =>
            Assert.False(ManifestVerifier.Verify(SignedKind.Update, Content, Sign(_key, "go-profile", Content), out _));

        [Fact]
        public void A_signature_from_another_key_fails() =>
            Assert.False(ManifestVerifier.Verify(SignedKind.MapCatalog, Content, Sign(_otherKey, "map-catalog", Content), out _));

        [Fact]
        public void Without_a_test_key_only_the_built_in_key_is_trusted()
        {
            ManifestVerifier.TestKey = null;
            Assert.False(ManifestVerifier.Verify(SignedKind.Update, Content, Sign(_key, "update", Content), out _));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("not base64 at all!")]
        [InlineData("AAAA")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA==")]
        public void A_missing_or_damaged_signature_fails_with_a_reason(string? signature)
        {
            Assert.False(ManifestVerifier.Verify(SignedKind.Update, Content, signature, out string reason));
            Assert.NotEqual("", reason);
        }

        [Fact]
        public void The_built_in_key_is_the_one_recorded_in_ADR_0007() =>
            Assert.Equal("7167E066645BA73012D79201B9D8845AE2D407625985FBF2F52811B5F35C5233",
                Convert.ToHexString(SHA256.HashData(ManifestVerifier.BuiltInKey)));

        [Fact]
        public void A_release_build_ignores_a_test_key_on_the_command_line()
        {
            ManifestVerifier.TestKey = null;
            using var pem = new TempFile(_otherKey.ExportSubjectPublicKeyInfoPem());
            AppServices.Configure(["--test-key", pem.Path]);
            Assert.Null(ManifestVerifier.TestKey);
        }

        public void Dispose()
        {
            ManifestVerifier.TestKey = null;
            _key.Dispose();
            _otherKey.Dispose();
        }
    }
}
