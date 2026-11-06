namespace CommandCenter.Core.Content
{
    // The control bar add-ons as a view model uses them (ticket 08): what is installed, the packages offered, and the
    // change with Undo. AddonService does the work; tests use a stand-in.
    public interface IAddons
    {
        IReadOnlyList<BarPackage> Packages { get; }
        (BarKind Kind, string? Resolution) Installed();

        // True when the Generals Online launcher installed the bar; Command Center leaves those files alone
        bool ManagedByGo();

        IReadOnlyList<string> InstalledFiles();
        string SuggestedResolution();
        BarPackage? Package(BarKind kind, string? resolution);
        Task<List<BackupEntry>> ApplyAsync(BarKind kind, string? resolution, IProgress<string> progress);
        void Undo(List<BackupEntry> made);
    }

    // The instance form over AddonService, created once in AppServices
    public sealed class Addons : IAddons
    {
        public IReadOnlyList<BarPackage> Packages => AddonService.Packages;
        public (BarKind Kind, string? Resolution) Installed() => AddonService.Installed();
        public bool ManagedByGo() => AddonService.ManagedByGo().Count > 0;
        public IReadOnlyList<string> InstalledFiles() => AddonService.InstalledFiles();
        public string SuggestedResolution() => AddonService.SuggestedResolution();
        public BarPackage? Package(BarKind kind, string? resolution) => AddonService.Package(kind, resolution);

        public Task<List<BackupEntry>> ApplyAsync(BarKind kind, string? resolution, IProgress<string> progress) =>
            AddonService.ApplyAsync(kind, resolution, progress);

        public void Undo(List<BackupEntry> made) => AddonService.Undo(made);
    }
}
