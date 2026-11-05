namespace CommandCenter.Tests
{
    // What Command Center may start, and how (seam 7). Nothing is started here: only the decision is checked.
    public sealed class ProcessRunnerTests : IDisposable
    {
        private readonly TempGame _game = new();
        private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        [Fact]
        public void Show_in_folder_selects_the_file_in_explorer_by_its_full_path()
        {
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.ShowInFolder, @"C:\Games\Zero Hour\Maps\Alpha\Alpha.map"))!;
            Assert.Equal(Path.Combine(Windows, "explorer.exe"), info.FileName);
            Assert.Equal(["/select,", @"C:\Games\Zero Hour\Maps\Alpha\Alpha.map"], info.ArgumentList);
            Assert.False(info.UseShellExecute);
        }

        [Fact]
        public void A_folder_opens_in_explorer()
        {
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.OpenFolder, @"C:\Games\Zero Hour"))!;
            Assert.Equal(Path.Combine(Windows, "explorer.exe"), info.FileName);
            Assert.Equal([@"C:\Games\Zero Hour"], info.ArgumentList);
        }

        [Theory]
        [InlineData(StartKind.ShowInFolder, @"Maps\Alpha.map")]
        [InlineData(StartKind.OpenFolder, "")]
        [InlineData(StartKind.OpenFolder, @"..\Windows")]
        public void Explorer_gets_only_full_paths(StartKind kind, string path) =>
            Assert.Null(ProcessRunner.Prepare(new StartRequest(kind, path)));

        [Fact]
        public void The_firewall_settings_open_through_control_by_its_full_path()
        {
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.FirewallSettings))!;
            Assert.Equal(Path.Combine(Environment.SystemDirectory, "control.exe"), info.FileName);
            Assert.Equal(["firewall.cpl"], info.ArgumentList);
            Assert.False(info.UseShellExecute);
        }

        [Theory]
        [InlineData("https://www.playgenerals.online/")]
        [InlineData("https://github.com/MAWKLY-VK/command-center/releases/latest")]
        [InlineData("steam://validate/2732960")]
        [InlineData("ms-settings:windowsupdate")]
        public void Web_pages_and_the_known_windows_and_steam_pages_open_through_the_shell(string target)
        {
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.Shell, target))!;
            Assert.True(info.UseShellExecute);
            Assert.Equal(target, info.FileName);
        }

        [Theory]
        [InlineData("http://example.org/")]
        [InlineData("file:///C:/Windows/System32/cmd.exe")]
        [InlineData(@"C:\Windows\System32\cmd.exe")]
        [InlineData("cmd.exe")]
        [InlineData("steam://run/2732960")]
        [InlineData("ms-settings:privacy")]
        [InlineData("javascript:alert(1)")]
        [InlineData("")]
        public void Anything_else_is_not_opened_through_the_shell(string target) =>
            Assert.Null(ProcessRunner.Prepare(new StartRequest(StartKind.Shell, target)));

        [Fact]
        public void A_program_in_the_game_folder_starts_there_with_its_arguments_as_a_list()
        {
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.InGameFolder, "GeneralsOnlineZH_60.exe", ["-win", "-xres", "1600"]))!;
            Assert.Equal(Path.Combine(GamePaths.Game, "GeneralsOnlineZH_60.exe"), info.FileName);
            Assert.Equal(GamePaths.Game, info.WorkingDirectory);
            Assert.Equal(["-win", "-xres", "1600"], info.ArgumentList);
            Assert.Equal("", info.Arguments);
            Assert.False(info.UseShellExecute);
        }

        [Theory]
        [InlineData(@"..\evil.exe")]
        [InlineData(@"C:\evil.exe")]
        [InlineData(@"plugins\evil.exe")]
        [InlineData("evil.bat")]
        [InlineData("")]
        public void Only_plain_program_names_start_in_the_game_folder(string name) =>
            Assert.Null(ProcessRunner.Prepare(new StartRequest(StartKind.InGameFolder, name)));

        [Fact]
        public void Nothing_starts_in_the_game_folder_when_there_is_no_game()
        {
            GamePaths.Game = "";
            Assert.Null(ProcessRunner.Prepare(new StartRequest(StartKind.InGameFolder, "GeneralsOnlineZH_60.exe")));
        }

        [Fact]
        public void A_restart_starts_this_program_again_with_its_arguments()
        {
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.Restart, "", ["--game", @"C:\Game", "--restart-of", "12"]))!;
            Assert.Equal(Environment.ProcessPath, info.FileName);
            Assert.Equal(["--game", @"C:\Game", "--restart-of", "12"], info.ArgumentList);
            Assert.False(info.UseShellExecute);
        }

        [Fact]
        public void The_update_step_asks_windows_to_start_this_program_as_administrator()
        {
            string self = Environment.ProcessPath!;
            string download = @"C:\Users\Some One\AppData\Local\Temp\CommandCenter-1.exe.download";
            string sha = new('a', 64);
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.AsAdministrator, self, [UpdateService.ReplaceArgument, download, sha]))!;
            Assert.Equal(self, info.FileName);
            Assert.True(info.UseShellExecute);
            Assert.Equal("runas", info.Verb);
            Assert.Equal($"\"--replace-program\" \"{download}\" \"{sha}\"", info.Arguments);
            Assert.Empty(info.ArgumentList);
        }

        [Fact]
        public void The_previous_version_beside_this_program_starts_as_administrator_only_to_put_itself_back()
        {
            string self = Environment.ProcessPath!;
            string old = Path.Combine(Path.GetDirectoryName(self)!, Path.GetFileNameWithoutExtension(self) + ".old.exe");
            var info = ProcessRunner.Prepare(new StartRequest(StartKind.AsAdministrator, old, [UpdateService.PutBackArgument]))!;
            Assert.Equal(old, info.FileName);
            Assert.Equal("runas", info.Verb);
            Assert.Equal("\"--put-back-program\"", info.Arguments);
        }

        [Fact]
        public void Nothing_else_starts_as_administrator()
        {
            string self = Environment.ProcessPath!;
            string old = Path.Combine(Path.GetDirectoryName(self)!, Path.GetFileNameWithoutExtension(self) + ".old.exe");
            string download = @"C:\Temp\CommandCenter-1.exe.download";
            string sha = new('a', 64);
            Assert.Null(Prepare(Path.Combine(Environment.SystemDirectory, "cmd.exe"), UpdateService.ReplaceArgument, download, sha));
            Assert.Null(Prepare(@"Tools\CommandCenter.exe", UpdateService.ReplaceArgument, download, sha));
            Assert.Null(Prepare(self));
            Assert.Null(Prepare(self, "--game", @"C:\Game"));
            Assert.Null(Prepare(self, UpdateService.ReplaceArgument, download));
            Assert.Null(Prepare(self, UpdateService.ReplaceArgument, "C:\\Temp\\a\" --game \"b", sha));
            Assert.Null(Prepare(self, UpdateService.ReplaceArgument, @"C:\Temp\", sha));
            Assert.Null(Prepare(self, UpdateService.PutBackArgument));
            Assert.Null(Prepare(old, UpdateService.ReplaceArgument, download, sha));

            static System.Diagnostics.ProcessStartInfo? Prepare(string target, params string[] arguments) =>
                ProcessRunner.Prepare(new StartRequest(StartKind.AsAdministrator, target, arguments));
        }

        public void Dispose() => _game.Dispose();
    }
}
