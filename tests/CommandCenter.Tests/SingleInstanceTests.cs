using System.Diagnostics;
using CommandCenter.Core.Diagnostics;

namespace CommandCenter.Tests
{
    // One Command Center per data folder (decision D2). The lock belongs to a thread, so a second copy is played by a
    // thread of its own. Every test uses fresh folder names, so a Command Center running on this PC is never involved.
    public sealed class SingleInstanceTests
    {
        private readonly string _folder = Path.Combine(Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));
        private readonly string _other = Path.Combine(Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));

        [Fact]
        public void The_first_copy_for_a_data_folder_gets_it()
        {
            using var first = SingleInstance.Acquire(_folder);
            Assert.True(first.IsFirst);
        }

        [Fact]
        public void A_second_copy_for_the_same_data_folder_does_not_get_it_whatever_the_letter_case()
        {
            using var first = SingleInstance.Acquire(_folder);
            Assert.False(OnItsOwnThread(() =>
            {
                using var second = SingleInstance.Acquire(_folder.ToUpperInvariant() + @"\");
                return second.IsFirst;
            }));
        }

        [Fact]
        public void A_copy_with_another_data_folder_runs_alongside()
        {
            using var first = SingleInstance.Acquire(_folder);
            Assert.True(OnItsOwnThread(() =>
            {
                using var other = SingleInstance.Acquire(_other);
                return other.IsFirst;
            }));
        }

        [Fact]
        public void A_later_copy_asks_the_first_to_show_its_window()
        {
            using var first = SingleInstance.Acquire(_folder);
            using var shown = new ManualResetEventSlim();
            first.OnShowRequested(shown.Set);

            OnItsOwnThread(() =>
            {
                using var second = SingleInstance.Acquire(_folder);
                second.AskFirstToShow();
                return true;
            });

            Assert.True(shown.Wait(TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void When_the_first_copy_closes_the_next_one_gets_it()
        {
            SingleInstance.Acquire(_folder).Dispose();
            Assert.True(OnItsOwnThread(() =>
            {
                using var next = SingleInstance.Acquire(_folder);
                return next.IsFirst;
            }));
        }

        [Fact]
        public void A_copy_that_ended_without_letting_go_does_not_block_the_next()
        {
            // Like a copy that crashed: its thread takes the lock and ends without releasing it
            OnItsOwnThread(() => SingleInstance.Acquire(_folder).IsFirst);

            using var next = SingleInstance.Acquire(_folder);
            Assert.True(next.IsFirst);
        }

        [Fact]
        public void A_restarted_copy_waits_for_the_copy_it_replaces()
        {
            // A short-lived process stands in for the copy being replaced
            using var old = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 2 127.0.0.1 > nul")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;

            using var restarted = SingleInstance.Acquire(_folder, restartOf: old.Id);

            Assert.True(old.HasExited);
            Assert.True(restarted.IsFirst);
        }

        [Fact]
        public void A_restart_after_the_old_copy_is_gone_does_not_wait()
        {
            var watch = Stopwatch.StartNew();
            using var restarted = SingleInstance.Acquire(_folder, restartOf: int.MaxValue);
            Assert.True(restarted.IsFirst);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2));
        }

        [Fact]
        public void A_restart_passes_the_arguments_on_with_only_the_newest_restart_marker()
        {
            var arguments = SingleInstance.ArgumentsForRestart(["--game", @"C:\Game", "--restart-of", "123", "--samples"]);

            Assert.Equal(["--game", @"C:\Game", "--samples", "--restart-of", Environment.ProcessId.ToString()], arguments);
        }

        [Theory]
        [InlineData(new[] { "CommandCenter.exe", "--restart-of", "4242" }, 4242)]
        [InlineData(new[] { "CommandCenter.exe", "--RESTART-OF", "7" }, 7)]
        [InlineData(new[] { "CommandCenter.exe", "--restart-of", "not a number" }, null)]
        [InlineData(new[] { "CommandCenter.exe", "--restart-of" }, null)]
        [InlineData(new[] { "CommandCenter.exe" }, null)]
        public void The_copy_to_wait_for_comes_from_the_restart_marker(string[] args, int? expected) =>
            Assert.Equal(expected, SingleInstance.RestartOf(args));

        private static T OnItsOwnThread<T>(Func<T> work)
        {
            T result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try { result = work(); }
                catch (Exception e) { error = e; }
            });
            thread.Start();
            thread.Join();
            if (error != null)
                throw error;
            return result;
        }
    }
}
