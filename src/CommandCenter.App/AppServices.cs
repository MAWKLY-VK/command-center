namespace CommandCenter
{
    // The hand-written composition root (ADR 0004): the one place where the services get what they need from the App at
    // startup, which is the run options. The services stay static until their pages move to MVVM in phases 5 and 6;
    // from then on, the ViewModels get them from here.
    internal static class AppServices
    {
        // First thing at startup, before anything reads a service. args are the command line as the program got it.
        public static void Configure(IReadOnlyList<string> args)
        {
            GoProfile.FileOverride = Value(args, "--go-profile");
            MapLibraryClient.SourceOverride = Value(args, "--map-library");
            MapLibraryClient.LibraryFolder = Value(args, "--library-folder");
            AppState.Samples = Has(args, "--samples");
        }

        // The value after an option, as App.Arg reads it
        private static string? Value(IReadOnlyList<string> args, string name)
        {
            int index = Index(args, name);
            return index >= 0 && index + 1 < args.Count ? args[index + 1] : null;
        }

        private static bool Has(IReadOnlyList<string> args, string name) => Index(args, name) >= 0;

        private static int Index(IReadOnlyList<string> args, string name)
        {
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                    return i;
            }
            return -1;
        }
    }
}
