using System.Collections;
using System.Globalization;
using System.Resources;
using DiskThinner.Core;

namespace DiskThinner.Rules.Tests;

/// <summary>界面文字（L.T / L.F 和 Strings.resx）。</summary>
public sealed class LocalizationTests
{
    [Fact]
    public void NeutralCultureReturnsTheSourceText()
    {
        Assert.Equal("清理计划", L.T("清理计划"));
        Assert.Equal("扫描 1,234 项，用时 2.5 秒", L.F("扫描 {0:N0} 项，用时 {1:F1} 秒", 1234, 2.5));
        Assert.Equal("{原样}", L.T("{原样}")); // 没有参数时花括号不是格式
    }

    /// <summary>Strings.resx 编进了 DiskThinner.Core.dll：每个键都能取回，值就是原文（带换行的也一样）。</summary>
    [Fact]
    public void EveryKeyIsEmbeddedAndRoundTrips()
    {
        var rm = new ResourceManager("DiskThinner.Core.Strings", typeof(L).Assembly);
        var set = rm.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
        Assert.NotNull(set);
        var entries = set.Cast<DictionaryEntry>().ToList();
        Assert.True(entries.Count > 500, $"只有 {entries.Count} 条");
        Assert.All(entries, e => Assert.Equal(e.Key, e.Value));
        Assert.Contains(entries, e => ((string)e.Key).Contains('\n'));
        Assert.Contains(entries, e => ((string)e.Key).Contains("{0}", StringComparison.Ordinal));
    }

    /// <summary>切到没有译文的语言时显示中文原文；zh-CN 属于中性语言 zh-Hans，不查表。</summary>
    [Fact]
    public void MissingTranslationsFallBackToTheSourceText()
    {
        var before = L.Culture;
        try
        {
            L.Culture = CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("清理计划", L.T("清理计划"));
            Assert.Equal("不在资源里的文字", L.T("不在资源里的文字"));
            Assert.Equal("共 3 项", L.F("共 {0} 项", 3));

            L.Culture = CultureInfo.GetCultureInfo("zh-CN");
            Assert.Equal("清理计划", L.T("清理计划"));
        }
        finally
        {
            L.Culture = before;
        }
    }

    [Fact]
    public void DisplayNamesGoThroughTheLocalizer()
    {
        // 枚举的显示名、保护名单的说明都在 resx 里
        var rm = new ResourceManager("DiskThinner.Core.Strings", typeof(L).Assembly);
        foreach (var text in new[] { Names.Display(Risk.Safe), Names.Display(Kind.Cache), ProtectionList.Display(ProtectionLevel.Hard), ProtectionList.BuiltIn[0].Reason })
        {
            Assert.Equal(text, rm.GetString(text, CultureInfo.InvariantCulture));
        }
    }
}
