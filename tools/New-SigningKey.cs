// Creates the ECDSA P-256 key that signs update.json, go-profile.json and the map catalog (docs/adr/0007).
//
// Run it in an interactive terminal at the repository root, naming a folder outside any repository:
//     dotnet run --file tools/New-SigningKey.cs -- <folder>
// Without the folder it asks for one first. It then asks for a password (typed twice, hidden) and writes:
//     command-center-signing.key.pem   the private key: PKCS#8, encrypted with AES-256-CBC and PBKDF2-SHA256
//                                      (600,000 iterations). Keep it offline, never commit it, never upload it.
//     command-center-signing.pub.pem   the public key (SubjectPublicKeyInfo). Safe to share; the app embeds it.
// It never prints or stores the password, refuses to overwrite existing key files, and refuses any folder inside a
// git repository. Before writing, it proves the encrypted key opens with the password and signs data that the
// public key verifies.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

const string KeyFile = "command-center-signing.key.pem";
const string PubFile = "command-center-signing.pub.pem";
const int MinPasswordLength = 16;
const int Pbkdf2Iterations = 600_000;

if (Console.IsInputRedirected)
    return Fail("Run this in an interactive terminal, so the password can be typed hidden.");

if (args.Length > 1)
    return Fail("Give at most one folder. Nothing was changed.");

Console.WriteLine("Command Center signing key (ECDSA P-256)");
Console.WriteLine();
string? input = args.Length == 1 ? args[0] : null;
if (input is null)
{
    Console.Write("Folder for the key files, outside the repository (for example a private folder or a USB drive): ");
    input = Console.ReadLine();
}
input = input?.Trim().Trim('"');
if (string.IsNullOrEmpty(input))
    return Fail("No folder given. Nothing was changed.");

string folder = Path.GetFullPath(input);
if (InsideGitRepository(folder))
    return Fail("That folder is inside a git repository. Choose a folder outside any repository. Nothing was changed.");

string keyPath = Path.Combine(folder, KeyFile);
string pubPath = Path.Combine(folder, PubFile);
if (File.Exists(keyPath) || File.Exists(pubPath))
    return Fail("Key files already exist in that folder. Nothing was changed.");

Console.WriteLine($"Folder: {folder}");
char[] password = ReadHidden($"Password (at least {MinPasswordLength} characters): ");
char[] repeat = ReadHidden("Repeat the password: ");
try
{
    if (password.Length < MinPasswordLength)
        return Fail($"The password is shorter than {MinPasswordLength} characters. Nothing was changed.");
    if (!password.AsSpan().SequenceEqual(repeat))
        return Fail("The two passwords differ. Nothing was changed.");

    using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pbe = new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, Pbkdf2Iterations);
    byte[] encrypted = key.ExportEncryptedPkcs8PrivateKey(password, pbe);
    string keyPem = new string(PemEncoding.Write("ENCRYPTED PRIVATE KEY", encrypted)) + Environment.NewLine;
    string pubPem = key.ExportSubjectPublicKeyInfoPem() + Environment.NewLine;

    // Prove the round trip before anything is written: the saved form must open with this password,
    // and its signatures must verify against the public key that will be embedded in the app.
    using (ECDsa reopened = ECDsa.Create())
    using (ECDsa publicOnly = ECDsa.Create())
    {
        reopened.ImportFromEncryptedPem(keyPem, password);
        publicOnly.ImportFromPem(pubPem);
        byte[] probe = RandomNumberGenerator.GetBytes(64);
        byte[] signature = reopened.SignData(probe, HashAlgorithmName.SHA256);
        if (!publicOnly.VerifyData(probe, signature, HashAlgorithmName.SHA256))
            return Fail("Self-check failed: the encrypted key did not verify. Nothing was written.");
    }

    Directory.CreateDirectory(folder);
    WriteNew(keyPath, keyPem);
    WriteNew(pubPath, pubPem);

    string fingerprint = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
    Console.WriteLine();
    Console.WriteLine("Done.");
    Console.WriteLine($"  Private key (encrypted): {keyPath}");
    Console.WriteLine($"  Public key:              {pubPath}");
    Console.WriteLine($"  Public key SHA-256:      {fingerprint}");
    Console.WriteLine();
    Console.WriteLine("Next:");
    Console.WriteLine("  1. Copy the private key file to two offline places (for example two USB drives) and check that both copies open.");
    Console.WriteLine("  2. Keep the password in a password manager. Without it the key cannot be used, and it cannot be recovered.");
    Console.WriteLine("  3. Never commit, upload or email the private key file.");
    Console.WriteLine("  4. Give Claude only the public key file path and the SHA-256 line above.");
    return 0;
}
finally
{
    // Strings cannot be wiped, so the password lives only in these arrays, which are cleared here
    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan()));
    CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(repeat.AsSpan()));
}

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

static bool InsideGitRepository(string folder)
{
    for (var dir = new DirectoryInfo(folder); dir != null; dir = dir.Parent)
    {
        if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")))
            return true;
    }
    return false;
}

static void WriteNew(string path, string text)
{
    // CreateNew fails instead of replacing a file that appeared meanwhile
    using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    writer.Write(text);
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}
