namespace DiskThinner.Rules;

/// <summary>把当前用户的目录显示成 %LOCALAPPDATA%、%APPDATA%、%USERPROFILE%：路径更短，截图里也不暴露用户名。</summary>
public static class PathDisplay
{
    private static readonly (string Path, string Token)[] Prefixes = Build();

    private static (string, string)[] Build()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\');
        var profile = F(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            (F(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (F(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (profile.Length == 0 ? "" : profile + @"\AppData\LocalLow", "%LOCALLOW%"),
            (profile, "%USERPROFILE%"),
        }.Where(p => p.Item1.Length > 3).OrderByDescending(p => p.Item1.Length).ToArray();
    }

    public static string Shorten(string path)
    {
        foreach (var (prefix, token) in Prefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && (path.Length == prefix.Length || path[prefix.Length] == '\\'))
            {
                return token + path[prefix.Length..];
            }
        }

        return path;
    }
}
