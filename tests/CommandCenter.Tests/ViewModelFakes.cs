namespace CommandCenter.Tests
{
    // Stand-ins for what view models get from AppServices, so their states and outcomes can be checked (ticket 08)

    internal sealed class FakeShell : IShell
    {
        public List<(string Message, Action? Undo, bool IsError)> Toasts { get; } = [];
        public List<string> Warnings { get; } = [];
        public int HealthRefreshes { get; private set; }
        public string? FolderToPick { get; set; }
        public UpdateCheck UpdateAnswer { get; set; } = new(null, null);

        public void Toast(string message, Action? undo = null, bool isError = false) => Toasts.Add((message, undo, isError));
        public string? ChooseFolder(string title, string? initial) => FolderToPick;
        public void Warn(string message) => Warnings.Add(message);
        public Task<UpdateCheck> CheckForUpdateAsync() => Task.FromResult(UpdateAnswer);
        public void RefreshHealth() => HealthRefreshes++;
        public int HelpShown { get; private set; }
        public void ShowHelp() => HelpShown++;

        // Yes/no questions asked, and the answer each gets (yes unless set)
        public List<string> Questions { get; } = [];
        public bool Answer { get; set; } = true;
        public bool Confirm(string message)
        {
            Questions.Add(message);
            return Answer;
        }

        public IntPtr WindowHandle => new(42);
        public int SteppedAside { get; private set; }
        public int CameBack { get; private set; }
        public void StepAside() => SteppedAside++;
        public void ComeBack() => CameBack++;

        public List<string> ToolsShown { get; } = [];
        public void ShowTools(string? section = null) => ToolsShown.Add(section ?? "");
    }

    // The Windows side of Play: which programs are there, what was started, and how the wait for the game goes
    internal sealed class FakeLauncher : IGameLauncher
    {
        public bool HasTestClient { get; set; }
        public bool HasOfficialLauncher { get; set; } = true;
        public bool Running { get; set; }
        public bool Startable { get; set; } = true;
        public List<Channel> Started { get; } = [];
        public int OfficialStarted { get; private set; }
        public Exception? StartError { get; set; }

        // What the wait does: by default the game starts, holds the front (settled) and closes again
        public Func<Action, Task<bool>> Wait { get; set; } = settled =>
        {
            settled();
            return Task.FromResult(true);
        };

        public bool IsRunning() => Running;
        public bool CanStart(Channel channel) => Startable;
        public string ExecutableFor(Channel channel) => channel == Channel.Test ? "GeneralsOnlineZH_TestEnvironment.exe" : "GeneralsOnlineZH_60.exe";

        public void Start(Channel channel)
        {
            if (StartError != null)
                throw StartError;
            Started.Add(channel);
        }

        public void StartOfficialLauncher() => OfficialStarted++;
        public Task<bool> WaitAsync(IntPtr window, Action settled) => Wait(settled);
    }

    internal sealed class FakeGameFolder : IGameFolder
    {
        public string Path { get; set; } = @"C:\Games\Zero Hour";
        public bool ZeroHourFound { get; set; } = true;
        public bool GeneralsOnlineFound { get; set; } = true;
        public GameFolderSource Source { get; set; } = GameFolderSource.Other;
        public List<string> Chosen { get; } = [];
        public bool Accept { get; set; } = true;

        public event Action? Changed;

        public bool Choose(string folder)
        {
            if (!Accept)
                return false;
            Chosen.Add(folder);
            Path = folder;
            Changed?.Invoke();
            return true;
        }
    }

    internal sealed class FakeAddons : IAddons
    {
        public (BarKind Kind, string? Resolution) Bar { get; set; } = (BarKind.Original, null);
        public bool Go { get; set; }
        public List<string> Files { get; } = [];
        public Func<BarKind, string?, IProgress<string>, Task<List<BackupEntry>>> Apply { get; set; } =
            (_, _, _) => Task.FromResult(new List<BackupEntry>());
        public List<List<BackupEntry>> Undone { get; } = [];

        public IReadOnlyList<BarPackage> Packages => AddonService.Packages;
        public (BarKind Kind, string? Resolution) Installed() => Bar;
        public bool ManagedByGo() => Go;
        public IReadOnlyList<string> InstalledFiles() => Files;
        public string SuggestedResolution() => "1920x1080";
        public BarPackage? Package(BarKind kind, string? resolution) => AddonService.Package(kind, resolution);
        public Task<List<BackupEntry>> ApplyAsync(BarKind kind, string? resolution, IProgress<string> progress) => Apply(kind, resolution, progress);
        public void Undo(List<BackupEntry> made) => Undone.Add(made);
    }
}
