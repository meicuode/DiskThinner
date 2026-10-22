using System.Security.Cryptography;
using DiskThinner.Core;
using DiskThinner.Rules;

namespace DiskThinner.App.Rules;

/// <summary>
/// 内置规则库：构建时由 dtrule 编译并嵌入 exe。首次运行时释放到 %LOCALAPPDATA%\DiskThinner\rules\rules-&lt;哈希&gt;.db，
/// 再以只读加固方式打开（RuleDb.Read）。文件名带内容哈希，内容不对就换一个名字重新释放，不会读到被改过的文件。
/// </summary>
internal static class RuleStore
{
    private const string ResourceName = "DiskThinner.rules.db";

    public static string Directory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DiskThinner", "rules");

    public static RuleSet LoadBuiltIn()
    {
        using var stream = typeof(RuleStore).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(L.T("程序里没有内置规则库"));
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));

        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"rules-{hash[..16]}.db");
        if (!File.Exists(path) || !SHA256.HashData(File.ReadAllBytes(path)).AsSpan().SequenceEqual(Convert.FromHexString(hash)))
        {
            var temp = path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        var rules = RuleDb.Read(path);
        RemoveOldVersions(path);
        return rules;
    }

    /// <summary>只删除本程序自己释放的旧版规则库文件（rules-*.db），失败就算了。</summary>
    private static void RemoveOldVersions(string current)
    {
        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "rules-*.db"))
            {
                if (!string.Equals(file, current, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
