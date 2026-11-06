namespace CommandCenter.Core.Game
{
    public enum GameFolderSource { NotFound, ChosenByPlayer, Steam, Other }

    // The game folder as the pages see it (ticket 08): where Zero Hour is, whether Generals Online is in it, where the
    // folder came from, and the player's choice of another one. Whatever depends on the game listens to Changed
    // (AppServices wires the checks, Home's picture and the match guard to it).
    public interface IGameFolder
    {
        // Empty when no Zero Hour folder was found
        string Path { get; }
        bool ZeroHourFound { get; }
        bool GeneralsOnlineFound { get; }
        GameFolderSource Source { get; }

        // False when the folder does not hold Zero Hour, and nothing changes; otherwise it is used and remembered
        bool Choose(string folder);

        event Action? Changed;
    }

    // The instance form over GamePaths, created once in AppServices
    public sealed class GameFolder : IGameFolder
    {
        public string Path => GamePaths.ZeroHourFound ? GamePaths.Game : "";
        public bool ZeroHourFound => GamePaths.ZeroHourFound;
        public bool GeneralsOnlineFound => GamePaths.GameFound;

        public GameFolderSource Source =>
            !GamePaths.ZeroHourFound ? GameFolderSource.NotFound
            : GamePaths.ChosenByPlayer ? GameFolderSource.ChosenByPlayer
            : GamePaths.IsSteamCopy(GamePaths.Game) ? GameFolderSource.Steam
            : GameFolderSource.Other;

        public event Action? Changed;

        public bool Choose(string folder)
        {
            if (!GamePaths.UseGameFolder(folder))
                return false;
            Changed?.Invoke();
            return true;
        }
    }
}
