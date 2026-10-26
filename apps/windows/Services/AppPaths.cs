namespace Ovi.Screenshot.Services;

internal sealed class AppPaths
{
    internal AppPaths(string? root = null) => Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OviScreenshot"));
    internal string Root { get; }
    internal string Settings => Path.Combine(Root, "settings.json");
    internal string Log => Path.Combine(Root, "app.log");
    internal string Recordings => Path.Combine(Root, "Recordings");
}
