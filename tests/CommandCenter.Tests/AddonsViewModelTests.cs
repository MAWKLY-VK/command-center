using CommandCenter.Pages;
using CommandCenter.ViewModels;

namespace CommandCenter.Tests
{
    // Add-ons in Settings (ticket 08): the control bar choice moved out of the section into a view model. These tests pin
    // what the section did before the move: its texts, choices, Apply with Undo, Discard, and the cases it refuses.
    public sealed class AddonsViewModelTests
    {
        private readonly FakeAddons _addons = new();
        private readonly FakeGameFolder _game = new();
        private readonly FakeShell _shell = new();

        private AddonsViewModel Model() => new(_addons, _game, _shell);

        [Fact]
        public void The_installed_bar_is_chosen_and_nothing_is_pending()
        {
            var model = Model();
            Assert.True(model.OriginalChosen);
            Assert.True(model.OriginalInstalled);
            Assert.Equal("Installed now: Original bar", model.ChangeText);
            Assert.Equal("Choose another bar to change it. The new bar shows the next time the game starts.", model.ChangeDetail);
            Assert.False(model.CanApply);
            Assert.False(model.CanDiscard);
            Assert.False(model.ShowCompare);
            Assert.Equal("Choose Control Bar Pro to compare it with the original.", model.CompareNote);
        }

        [Fact]
        public void Choosing_Control_Bar_Pro_makes_a_change_to_apply_with_its_download()
        {
            var model = Model();
            model.ProChosen = true;

            Assert.True(model.HasPendingChanges);
            Assert.True(model.CanApply);
            Assert.True(model.CanDiscard);
            Assert.Equal("1920x1080", model.ChosenResolution);
            Assert.Equal("Original bar  →  Control Bar Pro 1920 × 1080", model.ChangeText);
            Assert.Equal($"Downloads {Views.Size(1221633)} from GitHub, checks it, then adds the .big files to the game folder.", model.ChangeDetail);
            Assert.True(model.ShowCompare);
            Assert.Equal("Drag across the picture to compare. Pick the size that matches your game resolution; a wrong size looks stretched.",
                model.CompareNote);
        }

        [Fact]
        public void Picking_a_size_chooses_Control_Bar_Pro()
        {
            var model = Model();
            model.ChosenResolution = "2560x1440";
            Assert.True(model.ProChosen);
            Assert.Equal("Original bar  →  Control Bar Pro 2560 × 1440", model.ChangeText);
        }

        [Fact]
        public void Going_back_to_the_original_bar_moves_the_add_on_out()
        {
            _addons.Bar = (BarKind.Pro, "1920x1080");
            var model = Model();
            model.OriginalChosen = true;
            Assert.Equal("Control Bar Pro 1920 × 1080  →  Original bar", model.ChangeText);
            Assert.Equal("Moves the add-on archives out of the game folder; Undo puts them back.", model.ChangeDetail);
        }

        [Fact]
        public void Discard_puts_the_installed_choice_back()
        {
            var model = Model();
            model.ProChosen = true;
            model.DiscardCommand.Execute(null);
            Assert.True(model.OriginalChosen);
            Assert.False(model.HasPendingChanges);
        }

        [Fact]
        public async Task Apply_changes_the_bar_and_offers_Undo()
        {
            var made = new List<BackupEntry> { new() { Id = "1" } };
            _addons.Apply = (kind, resolution, progress) =>
            {
                _addons.Bar = (kind, resolution);
                return Task.FromResult(made);
            };
            var model = Model();
            model.ProChosen = true;
            await model.ApplyCommand.ExecuteAsync(null);

            var toast = Assert.Single(_shell.Toasts);
            Assert.Equal("Control bar changed. It shows the next time the game starts.", toast.Message);
            Assert.Equal("Installed now: Control Bar Pro 1920 × 1080", model.ChangeText);
            Assert.Equal(1, _shell.HealthRefreshes);

            toast.Undo!();
            Assert.Same(made, Assert.Single(_addons.Undone));
            Assert.Equal("Previous control bar put back", _shell.Toasts[^1].Message);
            Assert.Equal(2, _shell.HealthRefreshes);
        }

        [Fact]
        public async Task A_failed_apply_says_what_went_wrong()
        {
            _addons.Apply = (_, _, _) => throw new InvalidOperationException("GitHub did not answer.");
            var model = Model();
            model.ProChosen = true;
            await model.ApplyCommand.ExecuteAsync(null);
            Assert.Equal(("GitHub did not answer.", true), (_shell.Toasts[^1].Message, _shell.Toasts[^1].IsError));
        }

        [Fact]
        public void A_bar_the_Generals_Online_launcher_installed_is_left_to_it()
        {
            _addons.Go = true;
            var model = Model();
            Assert.False(model.CanChoose);
            Assert.True(model.ShowOtherNotice);
            Assert.Equal("Installed by the Generals Online launcher", model.OtherTitle);
            Assert.Equal("Change or remove it in the Generals Online launcher. Command Center does not touch files that launcher manages, so the two never undo each other's changes.",
                model.OtherDetail);
            Assert.Equal("Managed by the Generals Online launcher.", model.ChangeDetail);
            Assert.False(model.HasPendingChanges);
        }

        [Fact]
        public void Another_bar_is_named_and_neither_choice_is_taken()
        {
            _addons.Bar = (BarKind.Other, null);
            _addons.Files.Add(@"C:\Games\Zero Hour\ControlBarHD.big");
            var model = Model();
            Assert.Equal("Another control bar is installed", model.OtherTitle);
            Assert.Equal("ControlBarHD.big in the game folder. Choose the original bar or Control Bar Pro to replace it; Undo puts it back.", model.OtherDetail);
            Assert.False(model.OriginalChosen);
            Assert.False(model.ProChosen);
        }

        [Fact]
        public void Without_the_game_nothing_can_be_applied()
        {
            _game.ZeroHourFound = false;
            var model = Model();
            Assert.Equal("Folder not found", model.ChangeText);
            Assert.Equal("Zero Hour was not found. Press Play on the home page to choose the game folder.", model.ChangeDetail);
            Assert.False(model.CanApply);
            Assert.False(model.CanDiscard);
        }
    }
}
