using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace DiskThinner.I18n;

/// <summary>
/// 界面文字工具（见 src/DiskThinner.Core/L.cs）：
///   i18n rewrite &lt;源代码目录&gt;              把中文字符串包成 L.T("…")；插值字符串和字符串拼接改成 L.F("… {0} …", 参数)
///   i18n extract &lt;源代码目录&gt; &lt;resx&gt;       从 L.T / L.F 的第一个参数生成 Strings.resx（键和值都是中文原文）
///   i18n check   &lt;源代码目录&gt; &lt;resx&gt;       检查 resx 和源代码是否同步、有没有没包起来的中文（CI 用）
/// 日志（AppLog、SnapshotCommand.Log、Debug.WriteLine）和正则表达式里的中文不处理。
/// case 标签、模式、const、特性参数、参数默认值里必须是常量，不能包；check 会把它们报出来，要改成不依赖文字的写法。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 2 || args[0] is not ("rewrite" or "extract" or "check") || (args[0] != "rewrite" && args.Length < 3))
        {
            Console.Error.WriteLine("用法：i18n rewrite <源代码目录> | i18n extract <源代码目录> <resx> | i18n check <源代码目录> <resx>");
            return 2;
        }

        var root = Path.GetFullPath(args[1]);
        return args[0] switch
        {
            "rewrite" => Rewrite(root),
            "extract" => Scan(root, Path.GetFullPath(args[2]), write: true),
            _ => Scan(root, Path.GetFullPath(args[2]), write: false),
        };
    }

    private static readonly CSharpParseOptions Options = CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Preview);

    internal static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);

    internal static string Relative(string root, string file) => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static int Rewrite(string root)
    {
        var total = 0;
        var skipped = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var bytes = File.ReadAllBytes(file);
            var bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
            var tree = CSharpSyntaxTree.ParseText(text, Options, file);
            var rewriter = new LocalizingRewriter();
            var rewritten = (CompilationUnitSyntax)rewriter.Visit(tree.GetRoot());
            foreach (var (line, reason, value) in rewriter.Skipped)
            {
                skipped.Add($"{Relative(root, file)}:{line}  {reason}  {Shorten(value)}");
            }

            if (rewriter.Count == 0)
            {
                continue;
            }

            rewritten = Usings.Ensure(rewritten, text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
            var output = rewritten.ToFullString();
            var outBytes = new UTF8Encoding(bom).GetPreamble().Concat(new UTF8Encoding(false).GetBytes(output)).ToArray();
            File.WriteAllBytes(file, outBytes);
            total += rewriter.Count;
            Console.WriteLine($"{rewriter.Count,4}  {Relative(root, file)}");
        }

        Console.WriteLine($"共改写 {total} 处。");
        if (skipped.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"没有改写的中文（{skipped.Count} 处）：");
            foreach (var s in skipped)
            {
                Console.WriteLine("  " + s);
            }
        }

        return 0;
    }

    private static int Scan(string root, string resx, bool write)
    {
        var keys = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var errors = new List<string>();
        foreach (var file in SourceFiles(root))
        {
            var rel = Relative(root, file);
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(file), Options, file);
            foreach (var node in tree.GetRoot().DescendantNodes())
            {
                var line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                if (node is InvocationExpressionSyntax inv && Context.IsLocalizer(inv, out _))
                {
                    if (inv.ArgumentList.Arguments.FirstOrDefault()?.Expression is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } lit)
                    {
                        errors.Add($"{rel}:{line}  L.T / L.F 的第一个参数必须是字符串字面量（resx 的键）");
                        continue;
                    }

                    if (!keys.TryGetValue(lit.Token.ValueText, out var files))
                    {
                        keys[lit.Token.ValueText] = files = new SortedSet<string>(StringComparer.Ordinal);
                    }

                    files.Add(rel);
                    continue;
                }

                var value = node switch
                {
                    LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } l => l.Token.ValueText,
                    InterpolatedStringExpressionSyntax s => string.Concat(s.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText)),
                    _ => null,
                };
                if (value is null || !Cjk.Has(value))
                {
                    continue;
                }

                switch (Context.Classify(node, out var reason))
                {
                    case Kind.Ui:
                        errors.Add($"{rel}:{line}  没有包成 L.T / L.F：{Shorten(value)}");
                        break;
                    case Kind.Constant:
                        errors.Add($"{rel}:{line}  {reason}里有中文（翻译后会对不上），改成不依赖文字的写法：{Shorten(value)}");
                        break;
                }
            }
        }

        var existing = File.Exists(resx)
            ? XDocument.Load(resx).Root!.Elements("data").Select(d => (string)d.Attribute("name")!).ToHashSet(StringComparer.Ordinal)
            : [];
        var missing = keys.Keys.Where(k => !existing.Contains(k)).ToList();
        var stale = existing.Where(k => !keys.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();

        if (write)
        {
            Resx.Write(resx, keys);
            Console.WriteLine($"已写入 {Relative(Path.GetDirectoryName(root)!, resx)}：{keys.Count} 条（新增 {missing.Count}，删除 {stale.Count}）。");
        }
        else
        {
            foreach (var k in missing)
            {
                errors.Add($"Strings.resx 里没有：{Shorten(k)}（运行 i18n extract 更新）");
            }

            foreach (var k in stale)
            {
                errors.Add($"Strings.resx 里有、源代码里已经不用了：{Shorten(k)}（运行 i18n extract 更新）");
            }

            Console.WriteLine($"源代码里 {keys.Count} 条界面文字，Strings.resx 里 {existing.Count} 条。");
        }

        foreach (var e in errors)
        {
            Console.WriteLine("错误：" + e);
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            {
                Console.WriteLine("::error title=界面文字::" + e.Replace("%", "%25", StringComparison.Ordinal));
            }
        }

        return errors.Count == 0 ? 0 : 1;
    }

    internal static string Shorten(string s)
    {
        var one = s.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        return one.Length > 60 ? one[..60] + "…" : one;
    }
}

internal static class Cjk
{
    /// <summary>含中日韩文字或全角标点（（）：，。、“”等）。</summary>
    public static bool Has(string s)
    {
        foreach (var c in s)
        {
            if (c is >= '\u3000' and <= '\u303F' or >= '\u4E00' and <= '\u9FFF' or >= '\uFF00' and <= '\uFFEF' or '\u201C' or '\u201D' or '\u2026')
            {
                return true;
            }
        }

        return false;
    }
}

internal enum Kind
{
    /// <summary>界面文字，要包成 L.T / L.F。</summary>
    Ui,

    /// <summary>已经是 L.T / L.F 的第一个参数。</summary>
    Wrapped,

    /// <summary>日志：给开发者看的，不翻译。</summary>
    Log,

    /// <summary>正则表达式：匹配的是数据里的文字。</summary>
    Regex,

    /// <summary>必须是编译期常量的位置（case、模式、const、特性、参数默认值），不能调用 L.T。</summary>
    Constant,
}

internal static class Context
{
    private static readonly HashSet<string> LogCalls = new(StringComparer.Ordinal)
    {
        "AppLog.Write", "AppLog.Append", "AppLog.Crash", "Write", "Append", "Log", "Debug.WriteLine", "Trace.WriteLine",
    };

    private static readonly HashSet<string> RegexCalls = new(StringComparer.Ordinal)
    {
        "Regex.Replace", "Regex.Match", "Regex.Matches", "Regex.IsMatch", "Regex.Split",
        "System.Text.RegularExpressions.Regex.Replace", "System.Text.RegularExpressions.Regex.Match",
        "System.Text.RegularExpressions.Regex.Matches", "System.Text.RegularExpressions.Regex.IsMatch", "System.Text.RegularExpressions.Regex.Split",
    };

    public static bool IsLocalizer(InvocationExpressionSyntax inv, out string method)
    {
        method = "";
        if (inv.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: var m } access && m is "T" or "F"
            && Name(access.Expression) is "L" or "DiskThinner.Core.L" or "Core.L" or "global::DiskThinner.Core.L")
        {
            method = m;
            return true;
        }

        return false;
    }

    /// <summary>调用的方法名在列表里（写全命名空间的也算：DiskThinner.App.Shell.AppLog.Write 对上 AppLog.Write）。</summary>
    private static bool Matches(HashSet<string> names, SyntaxNode expression)
    {
        var name = Name(expression);
        return names.Contains(name) || names.Any(n => n.Contains('.', StringComparison.Ordinal) && name.EndsWith("." + n, StringComparison.Ordinal));
    }

    public static Kind Classify(SyntaxNode node, out string reason)
    {
        reason = "";
        if (node.Parent is ArgumentSyntax { Parent: ArgumentListSyntax { Parent: InvocationExpressionSyntax inv } list } arg && IsLocalizer(inv, out _) && list.Arguments.IndexOf(arg) == 0)
        {
            return Kind.Wrapped;
        }

        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case AttributeSyntax { Name: var attr } when Name(attr) is "GeneratedRegex" or "GeneratedRegexAttribute" or "System.Text.RegularExpressions.GeneratedRegex":
                    return Kind.Regex;
                case AttributeSyntax:
                    reason = "特性参数";
                    return Kind.Constant;
                case CaseSwitchLabelSyntax:
                    reason = "case 标签";
                    return Kind.Constant;
                case PatternSyntax:
                    reason = "模式匹配";
                    return Kind.Constant;
                case ParameterSyntax:
                    reason = "参数默认值";
                    return Kind.Constant;
                case LocalDeclarationStatementSyntax { IsConst: true }:
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.ConstKeyword):
                    reason = "const";
                    return Kind.Constant;
                case InvocationExpressionSyntax inv2 when Matches(LogCalls, inv2.Expression):
                    return Kind.Log;
                case InvocationExpressionSyntax inv3 when Matches(RegexCalls, inv3.Expression):
                    return Kind.Regex;
                case ObjectCreationExpressionSyntax { Type: var t } when Name(t) is "Regex" or "System.Text.RegularExpressions.Regex":
                    return Kind.Regex;
                case MemberDeclarationSyntax:
                    return Kind.Ui;
            }
        }

        return Kind.Ui;
    }

    private static string Name(SyntaxNode expression) => expression.ToString().Replace(" ", "", StringComparison.Ordinal);
}

/// <summary>把中文字符串包成 L.T / L.F（不改变运行结果：L 在中性语言下直接返回原文，F 等同于插值）。</summary>
internal sealed class LocalizingRewriter : CSharpSyntaxRewriter
{
    public int Count { get; private set; }

    public List<(int Line, string Reason, string Value)> Skipped { get; } = [];

    public override SyntaxNode? VisitLiteralExpression(LiteralExpressionSyntax node)
    {
        if (!node.IsKind(SyntaxKind.StringLiteralExpression) || !Cjk.Has(node.Token.ValueText) || !ShouldWrap(node, node.Token.ValueText))
        {
            return node;
        }

        Count++;
        return Call("T", [node.WithoutTrivia()]).WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitInterpolatedStringExpression(InterpolatedStringExpressionSyntax node)
    {
        var text = string.Concat(node.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText));
        if (!Cjk.Has(text) || !ShouldWrap(node, text))
        {
            return base.VisitInterpolatedStringExpression(node);
        }

        var parts = new Parts();
        AddInterpolated(node, parts);
        Count++;
        return parts.Build().WithTriviaFrom(node);
    }

    public override SyntaxNode? VisitBinaryExpression(BinaryExpressionSyntax node)
    {
        // 只在整串 a + b + c 的最外层处理（左结合：内层是最外层的 Left）
        if (!node.IsKind(SyntaxKind.AddExpression) || node.Parent is BinaryExpressionSyntax p && p.IsKind(SyntaxKind.AddExpression) && p.Left == node)
        {
            return base.VisitBinaryExpression(node);
        }

        var operands = new List<ExpressionSyntax>();
        var binaries = new List<BinaryExpressionSyntax>();
        ExpressionSyntax current = node;
        while (current is BinaryExpressionSyntax b && b.IsKind(SyntaxKind.AddExpression))
        {
            binaries.Insert(0, b);
            operands.Insert(0, b.Right);
            current = b.Left;
        }

        operands.Insert(0, current);
        var first = operands.FindIndex(IsString);
        var hasCjk = operands.Any(o => o switch
        {
            LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => Cjk.Has(l.Token.ValueText),
            InterpolatedStringExpressionSyntax s => Cjk.Has(string.Concat(s.Contents.OfType<InterpolatedStringTextSyntax>().Select(t => t.TextToken.ValueText))),
            _ => false,
        });
        if (first < 0 || !hasCjk || !ShouldWrap(node, node.ToString()))
        {
            return base.VisitBinaryExpression(node);
        }

        // 第一个字符串之前的部分保持原来的运算（1 + 2 + "x" 是 "3x"），作为一个参数
        var parts = new Parts();
        if (first > 0)
        {
            var prefix = first == 1 ? operands[0] : binaries[first - 2];
            parts.Hole(VisitExpression(prefix), null, null);
        }

        for (var i = first; i < operands.Count; i++)
        {
            switch (operands[i])
            {
                case LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression):
                    parts.Text(l.Token.ValueText);
                    break;
                case InterpolatedStringExpressionSyntax s:
                    AddInterpolated(s, parts);
                    break;
                default:
                    parts.Hole(VisitExpression(operands[i]), null, null);
                    break;
            }
        }

        Count++;
        return parts.Build().WithTriviaFrom(node);
    }

    private static bool IsString(ExpressionSyntax e) =>
        e is InterpolatedStringExpressionSyntax || e is LiteralExpressionSyntax l && l.IsKind(SyntaxKind.StringLiteralExpression);

    private void AddInterpolated(InterpolatedStringExpressionSyntax node, Parts parts)
    {
        foreach (var content in node.Contents)
        {
            switch (content)
            {
                case InterpolatedStringTextSyntax t:
                    parts.Text(t.TextToken.ValueText);
                    break;
                case InterpolationSyntax h:
                    parts.Hole(VisitExpression(h.Expression), h.AlignmentClause?.Value.ToString(), h.FormatClause?.FormatStringToken.ValueText);
                    break;
            }
        }
    }

    private ExpressionSyntax VisitExpression(ExpressionSyntax e) => ((ExpressionSyntax)Visit(e)!).WithoutTrivia();

    private bool ShouldWrap(SyntaxNode node, string value)
    {
        switch (Context.Classify(node, out var reason))
        {
            case Kind.Ui:
                return true;
            case Kind.Constant:
                Skipped.Add((node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, reason, value));
                return false;
            default:
                return false;
        }
    }

    internal static InvocationExpressionSyntax Call(string method, IReadOnlyList<ExpressionSyntax> args)
    {
        var list = new List<SyntaxNodeOrToken>();
        for (var i = 0; i < args.Count; i++)
        {
            if (i > 0)
            {
                list.Add(Token(SyntaxKind.CommaToken).WithTrailingTrivia(Space));
            }

            list.Add(Argument(args[i]));
        }

        return InvocationExpression(
            MemberAccessExpression(SyntaxKind.SimpleMemberAccessExpression, IdentifierName("L"), IdentifierName(method)),
            ArgumentList(SeparatedList<ArgumentSyntax>(list)));
    }

    /// <summary>拼出复合格式字符串和参数。没有参数时用 L.T，花括号不转义。</summary>
    private sealed class Parts
    {
        private readonly List<(string? Text, int Hole, string? Alignment, string? Format)> _items = [];
        private readonly List<ExpressionSyntax> _args = [];

        public void Text(string text) => _items.Add((text, -1, null, null));

        public void Hole(ExpressionSyntax expression, string? alignment, string? format)
        {
            _items.Add((null, _args.Count, alignment, format));
            _args.Add(expression);
        }

        public InvocationExpressionSyntax Build()
        {
            var sb = new StringBuilder();
            foreach (var (text, hole, alignment, format) in _items)
            {
                if (text is not null)
                {
                    sb.Append(_args.Count == 0 ? text : text.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal));
                    continue;
                }

                sb.Append('{').Append(hole);
                if (alignment is not null)
                {
                    sb.Append(',').Append(alignment);
                }

                if (format is not null)
                {
                    sb.Append(':').Append(format);
                }

                sb.Append('}');
            }

            var literal = LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(sb.ToString()));
            return _args.Count == 0 ? Call("T", [literal]) : Call("F", [literal, .. _args]);
        }
    }
}

/// <summary>需要时补上 using DiskThinner.Core;（L 在这个命名空间里）。</summary>
internal static class Usings
{
    private const string Namespace = "DiskThinner.Core";

    public static CompilationUnitSyntax Ensure(CompilationUnitSyntax unit, string eol)
    {
        var inCore = unit.Members.OfType<BaseNamespaceDeclarationSyntax>().Any(n => n.Name.ToString() == Namespace || n.Name.ToString().StartsWith(Namespace + ".", StringComparison.Ordinal));
        if (inCore || unit.Usings.Any(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.Name?.ToString() == Namespace))
        {
            return unit;
        }

        var directive = UsingDirective(ParseName(Namespace).WithLeadingTrivia(Space)).WithTrailingTrivia(EndOfLine(eol));
        var usings = unit.Usings;
        if (usings.Count == 0)
        {
            // 放在文件最前面，原来开头的注释留在命名空间前面
            return unit.WithUsings(List([directive.WithTrailingTrivia(EndOfLine(eol), EndOfLine(eol))]));
        }

        // System 开头的在前，其余按字母顺序
        var index = usings.Count;
        for (var i = 0; i < usings.Count; i++)
        {
            var name = usings[i].Name?.ToString() ?? "";
            if (usings[i].Alias is null && usings[i].StaticKeyword.IsKind(SyntaxKind.None) && !name.StartsWith("System", StringComparison.Ordinal)
                && string.CompareOrdinal(name, Namespace) > 0)
            {
                index = i;
                break;
            }
        }

        if (index < usings.Count && usings[index].HasLeadingTrivia)
        {
            // 插到带注释的 using 前面时，把注释挪到新的这一行前面
            directive = directive.WithLeadingTrivia(usings[index].GetLeadingTrivia());
            usings = usings.Replace(usings[index], usings[index].WithLeadingTrivia());
        }

        return unit.WithUsings(usings.Insert(index, directive));
    }
}

/// <summary>写 Strings.resx：键和值都是中文原文，注释是用到它的文件。换行写成字符引用，不受 git 换行转换影响。</summary>
internal static class Resx
{
    public static void Write(string path, SortedDictionary<string, SortedSet<string>> keys)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n");
        sb.Append("<!--\r\n");
        // XML 注释里不能出现两个连续的减号，命令写在 tools/i18n/README.md 里
        sb.Append("  界面文字（中性语言 zh-Hans）。由 tools/i18n 从源代码生成，不要手改，用法见 tools/i18n/README.md。\r\n");
        sb.Append("  键是源代码里 L.T / L.F 的中文原文，值也是原文。翻译时复制成 Strings.<语言>.resx，只改 value。\r\n");
        sb.Append("-->\r\n");
        sb.Append("<root>\r\n");
        sb.Append("  <resheader name=\"resmimetype\"><value>text/microsoft-resx</value></resheader>\r\n");
        sb.Append("  <resheader name=\"version\"><value>2.0</value></resheader>\r\n");
        sb.Append("  <resheader name=\"reader\"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\r\n");
        sb.Append("  <resheader name=\"writer\"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>\r\n");
        foreach (var (key, files) in keys)
        {
            sb.Append("  <data name=\"").Append(Escape(key, attribute: true)).Append("\" xml:space=\"preserve\">\r\n");
            sb.Append("    <value>").Append(Escape(key, attribute: false)).Append("</value>\r\n");
            sb.Append("    <comment>").Append(Escape(string.Join(", ", files), attribute: false)).Append("</comment>\r\n");
            sb.Append("  </data>\r\n");
        }

        sb.Append("</root>\r\n");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static string Escape(string s, bool attribute)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            sb.Append(c switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' when attribute => "&quot;",
                '\n' => "&#xA;",
                '\r' => "&#xD;",
                '\t' => "&#x9;",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }
}
