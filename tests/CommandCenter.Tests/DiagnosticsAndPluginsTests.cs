using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Settings, part 2 (ticket 09): the network diagnostics and the anti-cheat plugins on view models. These pin what the
    // Options page showed and copied before the move; the diagnostics keep their own direct route (not tested here).
    public sealed class DiagnosticsAndPluginsTests : IDisposable
    {
        private readonly TempGame _game = new();

        private static NetworkDiagnosticsResult Result(int ping = 40, int cloudflareLoss = 0, NatType nat = NatType.FullCone) => new()
        {
            Timestamp = new DateTime(2026, 11, 6, 21, 30, 5),
            GeoSuccess = true, GeoCountryCode = "JO",
            CloudflareSuccess = true, CloudflareAvgMs = 20, CloudflareLossPercent = cloudflareLoss,
            DnsSuccess = true, DnsAddresses = "104.21.32.1",
            PingSuccess = true, PingAvgMs = ping, PingMinMs = ping - 10, PingMaxMs = ping + 10, PingLossPercent = 0,
            Protocol = "IPv4",
            HttpSuccess = true, HttpLatencyMs = 90,
            ServerOnline = true, PlayersOnline = 335, Lobbies = 88,
            CdnSuccess = true, CdnLatencyMs = 30,
            StunSuccess = true, StunLatencyMs = 45, StunExternalEndpoint = "203.0.113.45:51000",
            TurnSuccess = false,
            NatType = nat, NatTypeDetail = "Peers can reach you",
        };

        // ── Diagnostics ──

        [Fact]
        public void The_copied_report_keeps_its_lines_and_hides_the_players_address()
        {
            string report = NetworkDiagnosticsViewModel.Report(Result());
            Assert.Contains("═══ Generals Online Network Diagnostics ═══", report);
            Assert.Contains("Time:         2026-11-06 21:30:05", report);
            Assert.Contains("Country:      Jordan", report);
            Assert.Contains("Ping (ICMP):  40ms avg (min 30 / max 50), 0% loss", report);
            Assert.Contains("Server:       Online — 335 players, 88 lobbies", report);
            Assert.Contains("TURN:         Unreachable", report);
            Assert.DoesNotContain("203.0.113.45", report);
        }

        [Theory]
        [InlineData(40, DiagnosticLevel.Good)]
        [InlineData(100, DiagnosticLevel.Fair)]
        [InlineData(200, DiagnosticLevel.Bad)]
        public void Ping_is_rated_by_how_long_it_takes(int ping, DiagnosticLevel level) =>
            Assert.Equal(level, NetworkDiagnosticsViewModel.RowsFor(Result(ping)).Single(r => r.Name == "Server ping (ICMP)").Level);

        [Fact]
        public void Loss_nat_and_the_address_on_screen_are_shown_as_before()
        {
            var rows = NetworkDiagnosticsViewModel.RowsFor(Result(cloudflareLoss: 10, nat: NatType.Symmetric));
            Assert.Equal(DiagnosticLevel.Fair, rows.Single(r => r.Name == "Cloudflare").Level);
            var nat = rows.Single(r => r.Name == "NAT type");
            Assert.Equal((DiagnosticLevel.Bad, "Problem", "Symmetric — Peers can reach you"), (nat.Level, nat.LevelText, nat.Text));
            Assert.DoesNotContain("203.0.113.45", rows.Single(r => r.Name.StartsWith("STUN")).Text);
            Assert.Equal((DiagnosticLevel.Bad, "Unreachable"), (rows.Single(r => r.Name.StartsWith("TURN")).Level, rows.Single(r => r.Name.StartsWith("TURN")).Text));
        }

        [Fact]
        public void The_country_and_the_protocol_are_facts_and_get_no_rating()
        {
            var rows = NetworkDiagnosticsViewModel.RowsFor(Result());
            foreach (string name in new[] { "Your country", "Protocol" })
            {
                var row = rows.Single(r => r.Name == name);
                Assert.Equal((DiagnosticLevel.Info, ""), (row.Level, row.LevelText));
            }
            Assert.Equal("Jordan", rows.Single(r => r.Name == "Your country").Text);
        }

        [Fact]
        public async Task A_problem_in_the_results_brings_a_next_step()
        {
            var good = Result();
            good.TurnSuccess = true;
            good.TurnLatencyMs = 30;
            bool withProblem = false;
            var model = new NetworkDiagnosticsViewModel(_ => Task.FromResult(withProblem ? Result() : good), _ => { });

            await model.RunCommand.ExecuteAsync(null);
            Assert.False(model.HasProblems);
            withProblem = true;
            await model.RunCommand.ExecuteAsync(null);
            Assert.True(model.HasProblems);
        }

        [Fact]
        public async Task Each_run_adds_a_row_to_the_log_which_keeps_the_last_ten()
        {
            int runs = 0;
            string copied = "";
            var model = new NetworkDiagnosticsViewModel(_ => Task.FromResult(Result(ping: 40 + runs++)), text => copied = text);
            for (int i = 0; i < 11; i++)
                await model.RunCommand.ExecuteAsync(null);

            Assert.Equal(10, model.History.Count);
            var last = model.History[0];
            Assert.Equal(("21:30:05", "20 ms", "50 ms", "0%", "90 ms", "Full Cone", "Online"),
                (last.Time, last.Internet, last.Ping, last.Loss, last.Http, last.Nat, last.Server));
            Assert.Equal(["Internet", "Game server", "Connectivity"], model.Groups.Select(g => g.Name));
            model.CopyCommand.Execute(null);
            Assert.Contains("Ping (ICMP):  50ms avg", copied);
        }

        [Fact]
        public async Task A_run_that_fails_says_what_happened_and_keeps_the_last_results()
        {
            bool fail = false;
            var model = new NetworkDiagnosticsViewModel(_ => fail ? throw new InvalidOperationException("No network adapter") : Task.FromResult(Result()), _ => { });
            await model.RunCommand.ExecuteAsync(null);
            fail = true;
            await model.RunCommand.ExecuteAsync(null);

            Assert.Equal("The check could not run (No network adapter). Check the internet connection and try again.", model.Failure);
            Assert.DoesNotContain(model.Rows, r => r.Level == DiagnosticLevel.Checking);
            Assert.Single(model.History);
            Assert.False(model.IsRunning);
        }

        // ── Plugins ──

        private void Plugin(string folder, string file, string json) => _game.Write($@"game\plugins\{folder}\{file}", json);

        [Fact]
        public void Plugins_are_listed_by_name_and_the_one_settings_names_is_chosen()
        {
            Plugin("easyanticheat", "config.json", """{"plugin_name":"Easy Anti-Cheat"}""");
            Plugin("generalsonline", "settings.json", """{"plugin_name":"GeneralsOnline AntiCheat","version":"2","limits":{"cpu":1}}""");
            _game.Write(@"game\plugins\empty\readme.txt", "no json here");
            _game.Write(@"user\GeneralsOnlineData\settings.json", """{"plugins":{"anticheat":"generalsonline"}}""");
            var settings = new GameSettingsViewModel(new FakeShell(), () => false, () => [], () => { });
            settings.Load();

            var plugins = new PluginsViewModel(settings, () => _game.Game);
            Assert.Equal(["Easy Anti-Cheat", "GeneralsOnline AntiCheat"], plugins.Plugins.Select(p => p.Label));
            Assert.Equal("generalsonline", plugins.Selected);
            Assert.Equal([false, true], plugins.Plugins.Select(p => p.Selected));
            Assert.Equal("GeneralsOnline AntiCheat", plugins.Details[0].Text);
            Assert.Contains(plugins.Details, line => line.Text == "version: 2");
            Assert.Contains(plugins.Details, line => line.Text == "cpu: 1" && line.Indent == 1);

            plugins.Plugins[0].Selected = true;
            Assert.Equal(("easyanticheat", "easyanticheat"), (plugins.Selected, settings.AntiCheat));
            Assert.Equal("Easy Anti-Cheat", plugins.Details[0].Text);
        }

        [Fact]
        public void Until_one_is_chosen_no_files_are_shown_and_a_damaged_file_is_named_in_words()
        {
            Plugin("generalsonline", "settings.json", """{"plugin_name":"GeneralsOnline AntiCheat"}""");
            Plugin("broken", "settings.json", "{ not json");
            var settings = new GameSettingsViewModel(new FakeShell(), () => false, () => [], () => { });
            settings.Load();
            var plugins = new PluginsViewModel(settings, () => _game.Game);

            Assert.Null(plugins.Selected);
            Assert.Equal((true, false), (plugins.NoneChosen, plugins.ShowDetails));
            plugins.Selected = "broken";
            Assert.Equal((false, true), (plugins.NoneChosen, plugins.ShowDetails));
            var line = Assert.Single(plugins.Details, l => !l.IsHeading);
            Assert.True(line.IsNote);
            Assert.StartsWith("Could not read settings.json", line.Text);
        }

        [Fact]
        public void Without_plugins_the_section_says_where_it_looked()
        {
            var settings = new GameSettingsViewModel(new FakeShell(), () => false, () => [], () => { });
            settings.Load();
            var plugins = new PluginsViewModel(settings, () => _game.Game);

            Assert.Empty(plugins.Plugins);
            Assert.True(plugins.IsEmpty);
            Assert.Equal(Path.Combine(_game.Game, "plugins"), plugins.Folder);
        }

        public void Dispose() => _game.Dispose();
    }
}
