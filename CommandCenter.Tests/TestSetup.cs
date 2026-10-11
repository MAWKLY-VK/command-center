// The language is a setting for the whole program (Loc), so tests that switch it must not run at the same time as others
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace CommandCenter.Tests
{
    // A file in the temporary folder that is deleted when the test ends
    internal sealed class TempFile : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));

        public TempFile(byte[] content) => File.WriteAllBytes(Path, content);

        public TempFile(string content) => File.WriteAllText(Path, content);

        public void Dispose()
        {
            try { File.Delete(Path); } catch { }
        }
    }

    // Runs a test in Arabic and puts English back afterwards
    internal sealed class Arabic : IDisposable
    {
        public Arabic() => Loc.Init("ar");

        public void Dispose() => Loc.Init("en");
    }
}
