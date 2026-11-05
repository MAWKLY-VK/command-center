using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CommandCenter.Tests
{
    // Replacing the program, keeping the previous one until the new one has started, and putting it back when the new one
    // cannot start at all (seam 5, P3-D1). Everything happens in a temporary program folder; nothing is started.
    public sealed class UpdateRollbackTests : IDisposable
    {
        private static readonly byte[] OldProgram = Encoding.ASCII.GetBytes("previous program");
        private static readonly byte[] NewProgram = Encoding.ASCII.GetBytes("new program");

        private readonly TempGame _game = new();
        private readonly string _exe;
        private readonly string _old;

        public UpdateRollbackTests()
        {
            _exe = _game.Write(@"program\CommandCenter.exe", OldProgram);
            _old = Path.Combine(Path.GetDirectoryName(_exe)!, "CommandCenter.old.exe");
        }

        private string Download(byte[] content) => _game.Write("download-" + Guid.NewGuid().ToString("N") + ".exe.download", content);

        private static string Sha(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

        [Fact]
        public void The_replace_keeps_the_previous_program_next_to_the_new_one()
        {
            string download = Download(NewProgram);
            UpdateService.ReplaceProgram(_exe, download);
            Assert.Equal(NewProgram, File.ReadAllBytes(_exe));
            Assert.Equal(OldProgram, File.ReadAllBytes(_old));
            Assert.False(File.Exists(download));
        }

        [Fact]
        public void The_previous_program_is_removed_once_the_new_one_has_started()
        {
            UpdateService.ReplaceProgram(_exe, Download(NewProgram));
            UpdateService.RemoveOldProgram(_exe);
            Assert.False(File.Exists(_old));
            Assert.Equal(NewProgram, File.ReadAllBytes(_exe));
        }

        [Fact]
        public void A_new_program_that_starts_leaves_everything_in_place()
        {
            UpdateService.ReplaceProgram(_exe, Download(NewProgram));
            UpdateService.RestartOrPutBack(_exe, new Version(9, 0, 0), () => { });
            Assert.Equal(NewProgram, File.ReadAllBytes(_exe));
            Assert.True(File.Exists(_old));
        }

        [Fact]
        public void A_new_program_that_cannot_start_is_replaced_by_the_previous_one()
        {
            UpdateService.ReplaceProgram(_exe, Download(NewProgram));
            var error = Assert.Throws<PreviousVersionRestoredException>(() =>
                UpdateService.RestartOrPutBack(_exe, new Version(9, 0, 0), () => throw new Win32Exception(193)));
            Assert.Equal(OldProgram, File.ReadAllBytes(_exe));
            Assert.False(File.Exists(_old));
            Assert.Single(Directory.EnumerateFiles(Path.GetDirectoryName(_exe)!));
            Assert.Contains("previous version", error.Message);
        }

        [Fact]
        public void The_update_screen_gets_windows_reason_without_the_paths_around_it()
        {
            UpdateService.ReplaceProgram(_exe, Download(NewProgram));
            const string Started = @"An error occurred trying to start process 'C:\Users\someone\CommandCenter.exe' with working directory 'C:\Users\someone'.";
            var error = Assert.Throws<PreviousVersionRestoredException>(() =>
                UpdateService.RestartOrPutBack(_exe, new Version(9, 0, 0), () => throw new Win32Exception(193, Started + " The file cannot run here.")));
            Assert.DoesNotContain(@"C:\Users\someone", error.Message);
            Assert.StartsWith("Version 9.0.0 could not start (", error.Message);
            Assert.EndsWith("), so the previous version was put back.", error.Message);
        }

        [Fact]
        public void A_new_program_that_is_gone_is_replaced_by_the_previous_one()
        {
            UpdateService.ReplaceProgram(_exe, Download(NewProgram));
            File.Delete(_exe);
            Assert.Throws<PreviousVersionRestoredException>(() =>
                UpdateService.RestartOrPutBack(_exe, new Version(9, 0, 0), () => throw new Win32Exception(2)));
            Assert.Equal(OldProgram, File.ReadAllBytes(_exe));
            Assert.False(File.Exists(_old));
        }

        [Fact]
        public async Task A_version_that_could_not_start_is_not_offered_again_in_this_run()
        {
            using var trust = TestKeys.Trust();
            var version = new Version(UpdateService.Current.Major, UpdateService.Current.Minor + 1, 0);
            var payload = new JsonObject
            {
                ["version"] = version.ToString(3),
                ["url"] = UpdateService.ReleaseDownloads + "v9/CommandCenter.exe",
                ["sha256"] = Sha(NewProgram),
                ["notes"] = "",
                ["expires"] = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
            byte[] signed = Encoding.UTF8.GetBytes(payload.ToJsonString());
            string manifest = _game.Write("update.json", new JsonObject
            {
                ["signed"] = Convert.ToBase64String(signed),
                ["signature"] = TestKeys.Sign(SignedKind.Update, signed),
            }.ToJsonString());
            Assert.NotNull(await UpdateService.CheckAsync(manifest));

            UpdateService.ReplaceProgram(_exe, Download(NewProgram));
            Assert.Throws<PreviousVersionRestoredException>(() => UpdateService.RestartOrPutBack(_exe, version, () => throw new Win32Exception(2)));
            Assert.Null(await UpdateService.CheckAsync(manifest));
        }

        [Fact]
        public void The_elevated_step_replaces_only_a_download_that_matches_its_checksum()
        {
            string download = Download(NewProgram);
            Assert.NotEqual(0, UpdateService.ReplaceAsAdministrator(_exe, download, Sha(OldProgram)));
            Assert.Equal(OldProgram, File.ReadAllBytes(_exe));
            Assert.False(File.Exists(_old));

            Assert.Equal(0, UpdateService.ReplaceAsAdministrator(_exe, download, Sha(NewProgram)));
            Assert.Equal(NewProgram, File.ReadAllBytes(_exe));
            Assert.Equal(OldProgram, File.ReadAllBytes(_old));
        }

        [Fact]
        public void A_wrongly_started_administrator_step_ends_with_an_error_and_never_opens_the_window()
        {
            string self = Environment.ProcessPath!;
            Assert.Null(UpdateService.AdministratorStep([self]));
            Assert.Null(UpdateService.AdministratorStep([self, "--game", _game.Game]));
            Refused(UpdateService.AdministratorStep([self, UpdateService.ReplaceArgument]));
            Refused(UpdateService.AdministratorStep([self, UpdateService.ReplaceArgument, "a", "b", "c"]));
            Refused(UpdateService.AdministratorStep([self, UpdateService.PutBackArgument, "a"]));

            static void Refused(int? exitCode)
            {
                Assert.NotNull(exitCode);
                Assert.NotEqual(0, exitCode.Value);
            }
        }

        [Fact]
        public void Leftovers_of_an_update_are_cleared_at_start_but_the_previous_program_stays()
        {
            string folder = Path.GetDirectoryName(_exe)!;
            File.WriteAllBytes(_old, OldProgram);
            File.WriteAllBytes(Path.Combine(folder, "CommandCenter.new.exe"), NewProgram);
            File.WriteAllBytes(Path.Combine(folder, "CommandCenter.failed.exe"), NewProgram);
            UpdateService.CleanUpLeftovers(_exe);
            Assert.Equal(["CommandCenter.exe", "CommandCenter.old.exe"],
                Directory.EnumerateFiles(folder).Select(file => Path.GetFileName(file)).Order().ToArray());
        }

        public void Dispose()
        {
            UpdateService.ForgetFailedVersion();
            _game.Dispose();
        }
    }
}
