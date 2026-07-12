namespace NexusMonach.Services;

public static class AppPaths
{
    public static string AppRoot { get; private set; } = string.Empty;
    public static string UserData { get; private set; } = string.Empty;
    public static string SettingsFile => Path.Combine(AppRoot, "settings.json");
    public static string BookmarksFile => Path.Combine(AppRoot, "bookmarks.json");
    public static string HistoryFile => Path.Combine(AppRoot, "history.json");
    public static string SessionFile => Path.Combine(AppRoot, "session.json");
    public static string SiteRulesFile => Path.Combine(AppRoot, "site-rules.json");
    public static string ExtensionRegistryFile => Path.Combine(AppRoot, "extensions.json");
    public static string KnowledgeGraphFile => Path.Combine(AppRoot, "knowledge-graph.json");
    public static string Extensions => Path.Combine(AppRoot, "Extensions");
    public static string WebAssets => Path.Combine(AppContext.BaseDirectory, "Assets", "Web");
    public static string BuiltInDevToolsExtension => Path.Combine(AppContext.BaseDirectory, "Assets", "NexusDevTools");

    public static void Initialize(IEnumerable<string> args)
    {
        var portable = args.Any(x => x.Equals("--portable", StringComparison.OrdinalIgnoreCase)) ||
                       File.Exists(Path.Combine(AppContext.BaseDirectory, "portable.flag"));

        AppRoot = portable
            ? Path.Combine(AppContext.BaseDirectory, "Data")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusMonach");

        UserData = Path.Combine(AppRoot, "WebView2");
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(UserData);
        Directory.CreateDirectory(Extensions);
    }
}
