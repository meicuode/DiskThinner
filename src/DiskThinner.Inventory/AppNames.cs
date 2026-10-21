using System.Text;
using System.Text.RegularExpressions;

namespace DiskThinner.Inventory;

/// <summary>软件名和厂商名的整理与比较：去掉版本号、架构、公司后缀，拆成小写的词。</summary>
public static partial class AppNames
{
    [GeneratedRegex(@"\s*[\(（\[][^\)）\]]*[\)）\]]")]
    private static partial Regex Parenthetical();

    [GeneratedRegex(@"(?<=\s|^)v?\d+(\.\d+)+[\w\-.]*", RegexOptions.IgnoreCase)]
    private static partial Regex DottedVersion();

    [GeneratedRegex(@"(?<=\s|^)(x64|x86|amd64|arm64|64-bit|32-bit|64 位|32 位)(?=\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex Architecture();

    // 英文后缀前面必须有空格或逗号（避免把 "Datag" 截成 "Dat"）；中文后缀直接连在名字后面
    [GeneratedRegex(@"(?:[,\s]+(?:co\.,?\s*ltd\.?|ltd\.?|limited|inc\.?|incorporated|corporation|corp\.?|llc|l\.l\.c\.|gmbh|s\.r\.o\.|ag|b\.v\.|pbc|pte\.?|s\.a\.|oy|ab)|有限责任公司|股份有限公司|有限公司)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    // Office 这类按语言安装的条目："Microsoft OneNote - zh-cn"
    [GeneratedRegex(@"\s+-\s+[a-z]{2}(-[a-z]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LocaleSuffix();

    /// <summary>"Postman x64 11.2.0" → "Postman"，"Python 3.12.1 (64-bit)" → "Python"，"7-Zip 23.01 (x64)" → "7-Zip"。</summary>
    public static string CleanDisplayName(string name)
    {
        var s = name.Replace("®", "").Replace("™", "").Replace("©", "");

        // 系统自带程序的版本信息里产品名都是这一句
        if (string.Equals(Spaces().Replace(s, " ").Trim(), "Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows";
        }

        s = Parenthetical().Replace(s, " ");
        s = LocaleSuffix().Replace(s.TrimEnd(), "");
        s = DottedVersion().Replace(s, " ");
        s = Architecture().Replace(s, " ");
        s = Spaces().Replace(s, " ").Trim(' ', '-', '–', '_', ',', ':');
        return s.Length == 0 ? name.Trim() : s;
    }

    /// <summary>"JetBrains s.r.o." → "JetBrains"，"Microsoft Corporation" → "Microsoft"，"腾讯科技(深圳)有限公司" → "腾讯科技"。</summary>
    public static string? CleanPublisher(string? publisher)
    {
        if (string.IsNullOrWhiteSpace(publisher))
        {
            return null;
        }

        var s = Parenthetical().Replace(publisher, "").Trim();
        for (var i = 0; i < 3; i++)
        {
            var next = CompanySuffix().Replace(s, "").Trim(' ', ',', '.');
            if (next == s || next.Length == 0)
            {
                break;
            }

            s = next;
        }

        return s.Length == 0 ? publisher.Trim() : s;
    }

    /// <summary>
    /// 拆成小写的词：字母连续的一段、数字连续的一段各算一个词（"Rider2024.1" → rider、2024、1）；汉字整段算一个词。
    /// 词连起来就是 <see cref="Key"/>，所以拆法不影响整体比较。
    /// </summary>
    public static List<string> Tokens(ReadOnlySpan<char> text)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var lastDigit = false;
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                var digit = char.IsDigit(ch);
                if (sb.Length > 0 && digit != lastDigit)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                }

                sb.Append(char.ToLowerInvariant(ch));
                lastDigit = digit;
            }
            else if (sb.Length > 0)
            {
                tokens.Add(sb.ToString());
                sb.Clear();
            }
        }

        if (sb.Length > 0)
        {
            tokens.Add(sb.ToString());
        }

        return tokens;
    }

    /// <summary>比较用的键：所有词连起来（"Microsoft Edge" 和 "microsoft-edge" 的键都是 "microsoftedge"）。</summary>
    public static string Key(ReadOnlySpan<char> text) => string.Concat(Tokens(text));

    /// <summary><paramref name="prefix"/> 的词是不是 <paramref name="tokens"/> 开头的几个词。</summary>
    public static bool StartsWithTokens(IReadOnlyList<string> tokens, IReadOnlyList<string> prefix)
    {
        if (prefix.Count == 0 || prefix.Count > tokens.Count)
        {
            return false;
        }

        for (var i = 0; i < prefix.Count; i++)
        {
            if (!string.Equals(tokens[i], prefix[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>"Microsoft.WindowsTerminal_8wekyb3d8bbwe" → "Windows Terminal"（商店应用的名称解析不出来时用）。</summary>
    public static string FromPackageFamily(string family)
    {
        var name = family.Split('_')[0];
        var dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1)
        {
            name = name[(dot + 1)..];
        }

        // 按大小写拆词："WindowsTerminal" → "Windows Terminal"，"ASUSPCAssistant" → "ASUSPC Assistant"
        var sb = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            var upper = char.IsUpper(name[i]);
            if (i > 0 && upper && (char.IsLower(name[i - 1]) || char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1])))
            {
                sb.Append(' ');
            }

            sb.Append(name[i]);
        }

        return sb.ToString();
    }
}
