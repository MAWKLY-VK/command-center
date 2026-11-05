using System.Text;
using Microsoft.Win32;

namespace CommandCenter.Tests
{
    // BackupService is the only way Command Center changes anything. These tests pin what it does (ticket 05 adds the
    // operation log on top of it): every change can be undone, only the game and its Documents folder can be changed,
    // and nothing the game reads is changed while it runs.
    public sealed class BackupServiceTests : IDisposable
    {
        private readonly TempGame _game = new();

        private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

        private static bool IsReadOnly(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly);

        [Fact]
        public void A_write_keeps_a_copy_and_undo_puts_the_old_content_back()
        {
            string target = _game.Write(@"game\Data\INI\Test.ini", "old");

            var entry = BackupService.WriteFile("Test write", target, Bytes("new"), "a detail");

            Assert.Equal("new", File.ReadAllText(target));
            Assert.NotNull(entry.BackupFile);
            Assert.Equal("old", File.ReadAllText(entry.BackupFile));
            Assert.Contains(BackupService.Load(), e => e.Id == entry.Id && e.Title == "Test write" && e.Detail == "a detail");

            Assert.True(BackupService.Undo(entry));
            Assert.Equal("old", File.ReadAllText(target));
            Assert.False(File.Exists(entry.BackupFile));
            Assert.DoesNotContain(BackupService.Load(), e => e.Id == entry.Id);
        }

        [Fact]
        public void A_file_changed_after_the_write_is_not_undone()
        {
            string target = _game.Write(@"game\Data\INI\Test.ini", "old");
            var entry = BackupService.WriteFile("Test write", target, Bytes("new"));
            File.WriteAllText(target, "changed by someone else");

            Assert.False(BackupService.CanUndo(entry));
            Assert.False(BackupService.Undo(entry));
            Assert.Equal("changed by someone else", File.ReadAllText(target));
        }

        [Fact]
        public void Quarantine_moves_a_file_away_and_undo_moves_it_back()
        {
            string target = _game.Write(@"game\d3d8.dll", "wrapper");

            var entry = BackupService.MoveToQuarantine("Test quarantine", target, "a detail");

            Assert.Equal("move", entry.Kind);
            Assert.False(File.Exists(target));
            Assert.Equal("wrapper", File.ReadAllText(entry.BackupFile!));
            Assert.True(BackupService.Undo(entry));
            Assert.Equal("wrapper", File.ReadAllText(target));
        }

        [Fact]
        public void Clearing_read_only_can_be_undone()
        {
            string a = _game.Write(@"game\a.big", "a");
            string b = _game.Write(@"game\b.big", "b");
            File.SetAttributes(a, FileAttributes.ReadOnly);
            File.SetAttributes(b, FileAttributes.ReadOnly);

            var entry = BackupService.ClearReadOnly("Test read-only", _game.Game, [a, b]);

            Assert.False(IsReadOnly(a));
            Assert.False(IsReadOnly(b));
            Assert.Equal(2, entry.Files!.Count);
            Assert.True(BackupService.Undo(entry));
            Assert.True(IsReadOnly(a));
            Assert.True(IsReadOnly(b));
        }

        [Fact]
        public void Clearing_read_only_leaves_nothing_half_done_when_one_file_is_refused()
        {
            string inside = _game.Write(@"game\a.big", "a");
            string outside = _game.Write(@"elsewhere\b.big", "b");
            File.SetAttributes(inside, FileAttributes.ReadOnly);
            File.SetAttributes(outside, FileAttributes.ReadOnly);

            Assert.Throws<InvalidOperationException>(() => BackupService.ClearReadOnly("Test read-only", _game.Game, [inside, outside]));

            Assert.True(IsReadOnly(inside));
            Assert.True(IsReadOnly(outside));
            Assert.Empty(BackupService.Load());
        }

        [Fact]
        public void Only_the_game_folder_and_its_documents_folder_can_be_changed()
        {
            BackupService.EnsureAllowed(Path.Combine(_game.Game, @"Data\INI\Test.ini"));
            BackupService.EnsureAllowed(Path.Combine(_game.UserData, "Options.ini"));

            // A folder whose name only starts like the game folder's is somewhere else
            var outside = Assert.Throws<InvalidOperationException>(() => BackupService.EnsureAllowed(_game.Game + @"2\Test.ini"));
            Assert.Equal(BackupService.OutsideMessage, outside.Message);
            Assert.Throws<InvalidOperationException>(() => BackupService.EnsureAllowed(Path.Combine(_game.Root, @"elsewhere\Test.ini")));
            Assert.Throws<InvalidOperationException>(() =>
                BackupService.EnsureAllowed(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "win.ini")));
        }

        [Fact]
        public void Nothing_the_game_reads_is_changed_while_it_runs()
        {
            string inGame = _game.Write(@"game\Data\INI\Test.ini", "old");
            string options = _game.Write(@"user\Options.ini", "old");
            string notReadByGame = Path.Combine(_game.UserData, "Maps", "Note.txt");

            using (new GameRunning())
            {
                var refused = Assert.Throws<InvalidOperationException>(() => BackupService.WriteFile("Test write", inGame, Bytes("new")));
                Assert.Equal(BackupService.GameRunningMessage, refused.Message);
                Assert.Throws<InvalidOperationException>(() => BackupService.WriteFile("Test write", options, Bytes("new")));
                BackupService.WriteFile("Test write", notReadByGame, Bytes("new"));
            }

            Assert.Equal("old", File.ReadAllText(inGame));
            Assert.Equal("old", File.ReadAllText(options));
            Assert.Equal("new", File.ReadAllText(notReadByGame));
            Assert.Single(BackupService.Load());
        }

        [Fact]
        public void A_registry_value_keeps_its_old_value_for_undo()
        {
            string key = @"Software\CommandCenterTests\" + Guid.NewGuid().ToString("N");
            try
            {
                using (var created = Registry.CurrentUser.CreateSubKey(key))
                    created.SetValue("Value", "old");

                var entry = BackupService.SetUserRegistryValue("Test registry", key, "Value", "new");

                Assert.Equal("old", entry.OldValue);
                Assert.Equal("new", Read(key));
                Assert.True(BackupService.Undo(entry));
                Assert.Equal("old", Read(key));
            }
            finally
            {
                Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
                using var parent = Registry.CurrentUser.OpenSubKey(@"Software\CommandCenterTests");
                if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                {
                    parent.Dispose();
                    Registry.CurrentUser.DeleteSubKey(@"Software\CommandCenterTests", throwOnMissingSubKey: false);
                }
            }
        }

        private static string? Read(string key)
        {
            using var opened = Registry.CurrentUser.OpenSubKey(key);
            return opened?.GetValue("Value") as string;
        }

        public void Dispose() => _game.Dispose();
    }
}
