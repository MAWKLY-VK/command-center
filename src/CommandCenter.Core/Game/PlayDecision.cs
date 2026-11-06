namespace CommandCenter.Core.Game
{
    public enum PlayAction { LocateGame, Update, Fix, Play }

    // PLAY's inputs (P5-D2): whether Zero Hour was found, Health's results with the client version check among them, and
    // the server stats, which stay null until they arrive or when the servers do not answer (ServerAsked tells which)
    public sealed record PlayInputs(bool GameFound, IReadOnlyList<HealthResult> Health, ServerStats? Server, bool ServerAsked);

    // What the button says and does, and the plain line under it. Problem is the Health result that Fix acts on: its fix
    // with Undo, or else its guide.
    public sealed record PlayState(PlayAction Action, string Label, string Status, HealthResult? Problem = null)
    {
        // The small "Play anyway" link under Fix
        public bool PlayAnyway => Action == PlayAction.Fix;
    }

    // The one button whose name follows the situation (P5-D2): Locate game, Update, Fix and Play, the highest that applies
    public static class PlayDecision
    {
        public static PlayState Decide(PlayInputs inputs)
        {
            if (!inputs.GameFound)
                return new PlayState(PlayAction.LocateGame, "Locate game", "Zero Hour was not found. Choose its folder.");

            if (inputs.Health.Any(r => r.Id == HealthService.GoVersionId && r.Status != HealthStatus.Passed))
                return new PlayState(PlayAction.Update, "Update", "Generals Online has a new version. The official launcher installs it.");

            var blocking = Blocking(inputs.Health);
            if (blocking.Count > 0)
            {
                var first = blocking[0];
                string status = blocking.Count == 1
                    ? first.Title + "."
                    : English.Plural(blocking.Count - 1, "{1}, and {0} more problem.", "{1}, and {0} more problems.", first.Title);
                return new PlayState(PlayAction.Fix, "Fix", status, first);
            }

            return new PlayState(PlayAction.Play, "Play", inputs switch
            {
                { Server: { } stats } => English.Plural(stats.Players, "Ready. {0:N0} player online.", "Ready. {0:N0} players online."),
                { ServerAsked: true } => "Ready. The Generals Online servers do not answer right now.",
                _ => "Ready.",
            });
        }

        // The quick check before PLAY: failing results that stop the game or cause a mismatch, those that stop the game
        // first, then problems before warnings, otherwise in Health's order
        public static List<HealthResult> Blocking(IEnumerable<HealthResult> health) =>
            health.Where(r => r.Status != HealthStatus.Passed && r.Impact != HealthImpact.None)
                .OrderBy(r => r.Impact == HealthImpact.StopsGame ? 0 : 1)
                .ThenBy(r => r.Status == HealthStatus.Problem ? 0 : 1)
                .ToList();
    }
}
