using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace CommandCenter.Tests
{
    // Writes, quarantine moves and Undo stay inside the game folder and the game's Documents folder (seam 4, through the
    // BackupService API): also through junctions, and also when the journal on disk says otherwise.
    public sealed class PathGuardTests : IDisposable
    {
        private readonly TempGame _game = new();
        private readonly string _outside;

        public PathGuardTests() => _outside = Path.Combine(_game.Root, "outside");

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        // A directory junction, which any user can make
        private static void Junction(string link, string target)
        {
            Directory.CreateDirectory(target);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
                info.ArgumentList.Add(argument);
            using var mklink = Process.Start(info)!;
            mklink.StandardOutput.ReadToEnd();
            mklink.WaitForExit();
            Assert.True(Directory.Exists(link), "The junction could not be made.");
        }

        // backups.json with this entry changed, as anything that can write the user's files could do
        private static BackupEntry Tamper(BackupEntry entry, Action<BackupEntry> change)
        {
            var entries = BackupService.Load();
            var tampered = entries.Single(e => e.Id == entry.Id);
            change(tampered);
            File.WriteAllText(Path.Combine(BackupService.Root, "backups.json"), JsonSerializer.Serialize(entries));
            return tampered;
        }

        [Fact]
        public void A_change_through_a_junction_that_leads_outside_is_refused()
        {
            Junction(Path.Combine(_game.Game, @"Data\Linked"), _outside);
            string through = Path.Combine(_game.Game, @"Data\Linked\Victim.ini");
            File.WriteAllText(Path.Combine(_outside, "Victim.ini"), "theirs");

            Assert.Throws<InvalidOperationException>(() => BackupService.WriteFile("Test write", through, Bytes("ours")));
            Assert.Throws<InvalidOperationException>(() => BackupService.MoveToQuarantine("Test quarantine", through));
            Assert.Throws<InvalidOperationException>(() => BackupService.ClearReadOnly("Test read-only", _game.Game, [through]));
            Assert.Equal("theirs", File.ReadAllText(Path.Combine(_outside, "Victim.ini")));
            Assert.Empty(BackupService.Load());
        }

        [Fact]
        public void A_junction_that_stays_inside_the_game_folder_is_followed()
        {
            string real = _game.Write(@"game\Data\INI\Test.ini", "old");
            Junction(Path.Combine(_game.Game, @"Data\Alias"), Path.Combine(_game.Game, @"Data\INI"));
            var entry = BackupService.WriteFile("Test write", Path.Combine(_game.Game, @"Data\Alias\Test.ini"), Bytes("ours"));
            Assert.Equal("ours", File.ReadAllText(real));
            Assert.True(BackupService.Undo(entry));
            Assert.Equal("old", File.ReadAllText(real));
        }

        [Fact]
        public void Undo_and_restore_refuse_a_journal_entry_whose_target_is_outside()
        {
            string target = _game.Write(@"game\Data\INI\Test.ini", "old");
            var entry = BackupService.WriteFile("Test write", target, Bytes("new"));
            Directory.CreateDirectory(_outside);
            string victim = Path.Combine(_outside, "Victim.ini");
            File.WriteAllText(victim, "new");

            var tampered = Tamper(entry, e => e.Target = victim);

            Assert.False(BackupService.CanUndo(tampered));
            Assert.False(BackupService.Undo(tampered));
            Assert.Throws<InvalidOperationException>(() => BackupService.RestoreOriginal(victim));
            Assert.Equal("new", File.ReadAllText(victim));
            Assert.True(File.Exists(entry.BackupFile));
        }

        [Fact]
        public void A_journal_entry_whose_backup_is_outside_the_backup_folder_is_refused()
        {
            string target = _game.Write(@"game\Data\INI\Test.ini", "old");
            var entry = BackupService.WriteFile("Test write", target, Bytes("new"));
            Directory.CreateDirectory(_outside);
            string elsewhere = Path.Combine(_outside, "Elsewhere.ini");
            File.WriteAllText(elsewhere, "someone else's");

            var tampered = Tamper(entry, e => e.BackupFile = elsewhere);

            Assert.False(BackupService.Undo(tampered));
            Assert.Throws<InvalidOperationException>(() => BackupService.RestoreOriginal(target));
            Assert.Equal("new", File.ReadAllText(target));
            Assert.Equal("someone else's", File.ReadAllText(elsewhere));
        }

        [Fact]
        public void A_quarantine_entry_whose_file_is_outside_the_quarantine_is_refused()
        {
            string target = _game.Write(@"game\d3d8.dll", "wrapper");
            var entry = BackupService.MoveToQuarantine("Test quarantine", target);
            Directory.CreateDirectory(_outside);
            string elsewhere = Path.Combine(_outside, "Elsewhere.dll");
            File.WriteAllText(elsewhere, "someone else's");

            var tampered = Tamper(entry, e => e.BackupFile = elsewhere);

            Assert.False(BackupService.Undo(tampered));
            Assert.False(File.Exists(target));
            Assert.True(File.Exists(elsewhere));
        }

        [Fact]
        public void A_registry_entry_for_another_key_is_refused()
        {
            string key = @"Software\CommandCenterHostileTest\" + Guid.NewGuid().ToString("N");
            try
            {
                using (var created = Registry.CurrentUser.CreateSubKey(key))
                    created.SetValue("Value", "new");
                string target = _game.Write(@"game\Data\INI\Test.ini", "old");
                var entry = BackupService.WriteFile("Test write", target, Bytes("new"));

                var tampered = Tamper(entry, e =>
                {
                    e.Kind = "registry";
                    e.Target = key;
                    e.BackupFile = "Value";
                    e.OldValue = "old";
                    e.NewValue = "new";
                });

                Assert.False(BackupService.CanUndo(tampered));
                Assert.False(BackupService.Undo(tampered));
                using (var opened = Registry.CurrentUser.OpenSubKey(key))
                    Assert.Equal("new", opened?.GetValue("Value"));
                Assert.Throws<InvalidOperationException>(() => BackupService.SetUserRegistryValue("Test registry", key, "Value", "other"));
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\CommandCenterHostileTest", throwOnMissingSubKey: false);
            }
        }

        [Fact]
        public void A_file_placed_at_the_old_temporary_name_cannot_take_the_write()
        {
            string target = _game.Write(@"game\Data\INI\Test.ini", "old");
            string planted = _game.Write(@"game\Data\INI\Test.ini.tmp", "planted");
            File.SetAttributes(planted, FileAttributes.ReadOnly);

            BackupService.WriteFile("Test write", target, Bytes("new"));

            Assert.Equal("new", File.ReadAllText(target));
            Assert.Equal("planted", File.ReadAllText(planted));
            Assert.Equal(["Test.ini", "Test.ini.tmp"], Directory.EnumerateFiles(Path.GetDirectoryName(target)!).Select(f => Path.GetFileName(f)).Order().ToArray());
        }

        public void Dispose() => _game.Dispose();
    }
}
