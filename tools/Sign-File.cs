// Signs the files Command Center checks with the maintainer's key, in the format of docs/adr/0007 (Format).
//
// Run it in an interactive terminal at the repository root:
//     dotnet run --file tools/Sign-File.cs -- go-profile  go-profile.json  <key file>   writes go-profile.json.sig
//     dotnet run --file tools/Sign-File.cs -- map-catalog catalog.json     <key file>   writes catalog.json.sig
//     dotnet run --file tools/Sign-File.cs -- update      update.json      <key file>   adds "signed" and "signature"
// The key file is command-center-signing.key.pem from tools/New-SigningKey.cs. The tool asks for its password
// (hidden), never prints the key or the password, and checks every signature against the key before it writes.
// An update.json keeps its old fields for 0.4.0; its signed payload carries an expiry date one year ahead.
// --stdin-password reads the password from redirected input instead; it is meant for throwaway test keys only.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

const string TrustedKeySha256 = "7167E066645BA73012D79201B9D8845AE2D407625985FBF2F52811B5F35C5233";
const string ReleaseDownloads = "https://github.com/MAWKLY-VK/command-center/releases/download/";
string[] kinds = ["update", "go-profile", "map-catalog"];

bool stdinPassword = args.Contains("--stdin-password");
string[] rest = args.Where(a => a != "--stdin-password").ToArray();
if (rest.Length != 3 || !kinds.Contains(rest[0]))
    return Fail("Usage: dotnet run --file tools/Sign-File.cs -- <update|go-profile|map-catalog> <file> <key file>");
if (Console.IsInputRedirected && !stdinPassword)
    return Fail("Run this in an interactive terminal, so the password can be typed hidden.");

string kind = rest[0];
string file = Path.GetFullPath(rest[1]);
string keyFile = Path.GetFullPath(rest[2]);
if (!File.Exists(file))
    return Fail($"{file} does not exist. Nothing was signed.");
if (!File.Exists(keyFile))
    return Fail($"{keyFile} does not exist. Nothing was signed.");

char[] password = stdinPassword ? (Console.ReadLine() ?? "").ToCharArray() : ReadHidden("Password of the signing key: ");
try
{
    using ECDsa key = ECDsa.Create();
    try
    {
        key.ImportFromEncryptedPem(File.ReadAllText(keyFile), password);
    }
    catch (Exception ex) when (ex is CryptographicException or ArgumentException)
    {
        return Fail("The password does not open this key, or the file is not an encrypted signing key. Nothing was signed.");
    }

    string fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    if (kind == "update")
    {
        var manifest = JsonNode.Parse(File.ReadAllText(file)) as JsonObject;
        string Field(string name) => manifest?[name] is JsonValue value && value.TryGetValue(out string? text) ? text.Trim() : "";
        string version = Field("version"), url = Field("url"), sha256 = Field("sha256").ToLowerInvariant(), notes = Field("notes");
        if (manifest == null || version.Length == 0 || sha256.Length != 64 || !sha256.All(Uri.IsHexDigit))
            return Fail("update.json needs version, url and a 64-character sha256. Nothing was signed.");
        if (!url.StartsWith(ReleaseDownloads, StringComparison.Ordinal))
            return Fail($"The url must start with {ReleaseDownloads}. Nothing was signed.");

        string expires = DateTime.UtcNow.AddYears(1).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        byte[] payload = Encoding.UTF8.GetBytes(new JsonObject
        {
            ["version"] = version,
            ["url"] = url,
            ["sha256"] = sha256,
            ["notes"] = notes,
            ["expires"] = expires,
        }.ToJsonString());
        string signature = Sign(key, kind, payload);
        manifest["signed"] = Convert.ToBase64String(payload);
        manifest["signature"] = signature;
        WriteText(file, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Signed {file} for version {version}. It is valid until {expires}.");
    }
    else
    {
        string signature = Sign(key, kind, File.ReadAllBytes(file));
        WriteText(file + ".sig", signature + "\n");
        Console.WriteLine($"Signed {file} as {kind}: {file}.sig");
    }

    Console.WriteLine($"Key SHA-256: {fingerprint}");
    if (fingerprint != TrustedKeySha256)
        Console.WriteLine("Warning: this is not the key Command Center trusts (docs/adr/0007). Released copies refuse this signature.");
    return 0;
}
finally
{
    // Strings cannot be wiped, so the password lives only in this array, which is cleared here
    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
}

// Signs "command-center:<kind>\n" + content, and proves the signature verifies before it is written anywhere
static string Sign(ECDsa key, string kind, byte[] content)
{
    byte[] message = Encoding.UTF8.GetBytes("command-center:" + kind + "\n").Concat(content).ToArray();
    byte[] signature = key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    using ECDsa publicOnly = ECDsa.Create();
    publicOnly.ImportSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
    if (!publicOnly.VerifyData(message, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
        throw new CryptographicException("Self-check failed: the signature did not verify. Nothing was written.");
    return Convert.ToBase64String(signature);
}

static void WriteText(string path, string text) =>
    File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

static char[] ReadHidden(string prompt)
{
    Console.Write(prompt);
    var buffer = new List<char>();
    while (true)
    {
        ConsoleKeyInfo k = Console.ReadKey(intercept: true);
        if (k.Key == ConsoleKey.Enter) break;
        if (k.Key == ConsoleKey.Backspace) { if (buffer.Count > 0) buffer.RemoveAt(buffer.Count - 1); continue; }
        if (!char.IsControl(k.KeyChar)) buffer.Add(k.KeyChar);
    }
    Console.WriteLine();
    char[] result = buffer.ToArray();
    CollectionsMarshal.AsSpan(buffer).Clear();
    return result;
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
