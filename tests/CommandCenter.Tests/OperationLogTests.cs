using System.Text;
using System.Text.Json;
using CommandCenter.Core.Diagnostics;

namespace CommandCenter.Tests
{
    // The operation log: one JSON line for each change Command Center makes and each attempt that failed (decision D3)
    public sealed class OperationLogTests : IDisposable
    {
        private readonly TempGame _game = new();

        private static List<JsonElement> Lines(string path) =>
            File.ReadAllLines(path).Where(l => l.Length > 0).Select(l => JsonDocument.Parse(l).RootElement.Clone()).ToList();

        private static string Text(JsonElement line, string name) => line.GetProperty(name).GetString()!;

        [Fact]
        public void A_change_is_one_line_with_its_time_operation_target_result_and_detail()
        {
            var before = DateTimeOffset.Now.AddSeconds(-1);
            OperationLog.Done("write", @"C:\Game\Data\Test.ini", "Test detail");

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.InRange(DateTimeOffset.Parse(Text(line, "time")), before, DateTimeOffset.Now.AddSeconds(1));
            Assert.Equal("write", Text(line, "operation"));
            Assert.Equal(@"C:\Game\Data\Test.ini", Text(line, "target"));
            Assert.Equal("done", Text(line, "result"));
            Assert.Equal("Test detail", Text(line, "detail"));
        }

        [Fact]
        public void A_failure_keeps_its_reason()
        {
            OperationLog.Failed("quarantine", @"C:\Game\d3d8.dll", "Access is denied.");

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("failed", Text(line, "result"));
            Assert.Equal("Access is denied.", Text(line, "detail"));
        }

        [Fact]
        public void A_recorded_change_returns_its_result_and_is_logged_as_done()
        {
            Assert.Equal(42, OperationLog.Record("install map", "Tournament Desert", "From a zip", () => 42));

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("done", Text(line, "result"));
            Assert.Equal("From a zip", Text(line, "detail"));
        }

        [Fact]
        public void A_recorded_change_that_fails_is_logged_with_the_reason_and_the_error_goes_on()
        {
            var error = Assert.Throws<IOException>(() =>
                OperationLog.Record<int>("install map", "Tournament Desert", "From a zip", () => throw new IOException("The disk is full.")));

            Assert.Equal("The disk is full.", error.Message);
            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("failed", Text(line, "result"));
            Assert.Equal("From a zip: The disk is full.", Text(line, "detail"));
        }

        [Fact]
        public void Past_one_megabyte_the_log_starts_again_and_keeps_only_the_previous_file()
        {
            Directory.CreateDirectory(GamePaths.AppData);
            string first = new string('a', (int)OperationLog.MaxBytes - 10);
            File.WriteAllText(OperationLog.FilePath, first);
            OperationLog.Done("write", "target", "");

            Assert.Equal(first, File.ReadAllText(OperationLog.PreviousPath));
            Assert.Single(Lines(OperationLog.FilePath));

            string second = new string('b', (int)OperationLog.MaxBytes - 10);
            File.WriteAllText(OperationLog.FilePath, second);
            OperationLog.Done("write", "target", "");

            Assert.Equal(second, File.ReadAllText(OperationLog.PreviousPath));
            Assert.Single(Lines(OperationLog.FilePath));
        }

        [Fact]
        public void A_log_that_cannot_be_written_never_stops_the_change()
        {
            // A file where Command Center's data folder should be: the log has nowhere to go
            File.WriteAllText(GamePaths.AppData, "not a folder");

            OperationLog.Done("write", "target", "");
            Assert.Equal(7, OperationLog.Record("write", "target", "", () => 7));
        }

        [Fact]
        public void A_backup_write_is_logged_with_its_title_and_detail()
        {
            string target = _game.Write(@"game\Data\INI\Test.ini", "old");
            BackupService.WriteFile("Test write", target, Encoding.UTF8.GetBytes("new"), "Test.ini · one key");

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("write", Text(line, "operation"));
            Assert.Equal(target, Text(line, "target"));
            Assert.Equal("done", Text(line, "result"));
            Assert.Equal("Test write · Test.ini · one key", Text(line, "detail"));
        }

        [Fact]
        public void A_refused_backup_write_is_logged_as_failed_with_the_reason()
        {
            string outside = Path.Combine(_game.Root, @"elsewhere\Test.ini");
            Assert.Throws<InvalidOperationException>(() => BackupService.WriteFile("Test write", outside, Encoding.UTF8.GetBytes("new")));

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("failed", Text(line, "result"));
            Assert.Equal("Test write: " + BackupService.OutsideMessage, Text(line, "detail"));
        }

        [Fact]
        public void Quarantine_and_undo_are_logged()
        {
            string target = _game.Write(@"game\d3d8.dll", "wrapper");
            var entry = BackupService.MoveToQuarantine("Test quarantine", target);
            BackupService.Undo(entry);

            var lines = Lines(OperationLog.FilePath);
            Assert.Equal(["quarantine", "undo"], lines.Select(l => Text(l, "operation")));
            Assert.All(lines, l => Assert.Equal("done", Text(l, "result")));
            Assert.All(lines, l => Assert.Equal(target, Text(l, "target")));
        }

        [Fact]
        public void Installing_maps_is_logged_with_their_names()
        {
            string source = Path.Combine(_game.Root, "download");
            _game.Write(@"download\Desert Fury\Desert Fury.map", "map");
            _game.Write(@"download\Desert Fury\Desert Fury.tga", "preview");

            Assert.Equal(["Desert Fury"], MapService.Install(source));

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("install map", Text(line, "operation"));
            Assert.Equal(source, Text(line, "target"));
            Assert.Equal("done", Text(line, "result"));
            Assert.Equal("Desert Fury", Text(line, "detail"));
        }

        [Fact]
        public void Removing_a_map_is_logged_with_where_it_went()
        {
            string folder = Path.GetDirectoryName(_game.Write(@"user\Maps\Desert Fury\Desert Fury.map", "map"))!;
            var map = new MapInfo { Name = "Desert Fury", Folder = folder, MapFile = Path.Combine(folder, "Desert Fury.map") };

            string moved = MapCatalog.Remove(map);

            var line = Assert.Single(Lines(OperationLog.FilePath));
            Assert.Equal("remove map", Text(line, "operation"));
            Assert.Equal(folder, Text(line, "target"));
            Assert.Equal("done", Text(line, "result"));
            Assert.Equal("Moved to " + moved, Text(line, "detail"));
        }

        public void Dispose() => _game.Dispose();
    }
}
