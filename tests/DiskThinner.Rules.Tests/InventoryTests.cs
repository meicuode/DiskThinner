using DiskThinner.Inventory;

namespace DiskThinner.Rules.Tests;

/// <summary>已装软件的收集和名称整理、程序版本信息和签名。</summary>
public class InventoryTests
{
    [Theory]
    [InlineData("Postman x64 11.2.0", "Postman")]
    [InlineData("Python 3.12.1 (64-bit)", "Python")]
    [InlineData("7-Zip 23.01 (x64)", "7-Zip")]
    [InlineData("Microsoft OneNote - zh-cn", "Microsoft OneNote")]
    [InlineData("Node.js", "Node.js")]
    [InlineData("JetBrains Rider 2024.1.4", "JetBrains Rider")]
    [InlineData("Microsoft SQL Server Management Studio - 20.1", "Microsoft SQL Server Management Studio")]
    [InlineData("Notepad++ (64-bit x64)", "Notepad++")]
    [InlineData("微信", "微信")]
    [InlineData("Microsoft® Windows® Operating System", "Windows")]
    [InlineData("Microsoft® .NET", "Microsoft .NET")]
    public void CleansDisplayNames(string raw, string expected) => Assert.Equal(expected, AppNames.CleanDisplayName(raw));

    [Theory]
    [InlineData("JetBrains s.r.o.", "JetBrains")]
    [InlineData("Microsoft Corporation", "Microsoft")]
    [InlineData("腾讯科技(深圳)有限公司", "腾讯科技")]
    [InlineData("Google LLC", "Google")]
    [InlineData("Anthropic, PBC", "Anthropic")]
    [InlineData("Docker Inc.", "Docker")]
    [InlineData("Datag", "Datag")]
    public void CleansPublishers(string raw, string expected) => Assert.Equal(expected, AppNames.CleanPublisher(raw));

    [Fact]
    public void TokensSplitLettersAndDigits()
    {
        Assert.Equal(["rider", "2024", "1"], AppNames.Tokens("Rider2024.1"));
        Assert.Equal(AppNames.Key("Microsoft Edge"), AppNames.Key("microsoft-edge"));
        Assert.True(AppNames.StartsWithTokens(AppNames.Tokens("Google Chrome"), AppNames.Tokens("Google")));
        Assert.False(AppNames.StartsWithTokens(AppNames.Tokens("Google"), AppNames.Tokens("Google Chrome")));
    }

    [Theory]
    [InlineData("Microsoft.WindowsTerminal_8wekyb3d8bbwe", "Windows Terminal")]
    [InlineData("B9ECED6F.ASUSPCAssistant_qmba6cd70vzyy", "ASUSPC Assistant")]
    [InlineData("Clipchamp.Clipchamp_yxz26nhyzhsrt", "Clipchamp")]
    public void NamesStorePackagesFromFamily(string family, string expected) => Assert.Equal(expected, AppNames.FromPackageFamily(family));

    [Fact]
    public void ParsesUninstallEntries()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.Equal($@"{programFiles}\Foo\foo.exe", InventoryCollector.ExeFromCommand($"\"{programFiles}\\Foo\\foo.exe\",0"));
        Assert.Equal($@"{programFiles}\Foo\unins000.exe", InventoryCollector.ExeFromCommand($@"{programFiles}\Foo\unins000.exe /SILENT"));
        Assert.Null(InventoryCollector.ExeFromCommand("MsiExec.exe /X{12345678-1234-1234-1234-123456789012}"));
        Assert.Null(InventoryCollector.ExeFromCommand($@"{Environment.GetFolderPath(Environment.SpecialFolder.Windows)}\System32\rundll32.exe shell32.dll"));

        Assert.Equal("Microsoft.WindowsTerminal_8wekyb3d8bbwe", InventoryCollector.FamilyName("Microsoft.WindowsTerminal_1.23.0.0_x64__8wekyb3d8bbwe"));
        Assert.Null(InventoryCollector.FamilyName("Microsoft.WindowsTerminal_1.23.0.0_neutral_split.scale-125_8wekyb3d8bbwe"));

        Assert.Null(InventoryCollector.NormalizeDir(programFiles + @"\"));
        Assert.Equal($@"{programFiles}\Git", InventoryCollector.NormalizeDir($"\"{programFiles}\\Git\\\""));
        Assert.Null(InventoryCollector.UsableInstallDir(@"C:\ProgramData\Package Cache\{1234}"));
    }

    [Fact]
    public void CollectsInstalledAppsAndRunningProcesses()
    {
        var snapshot = InventoryCollector.Collect();
        Assert.True(snapshot.Count(AppSource.Registry) > 0, string.Join("；", snapshot.Errors));

        // 当前进程（测试宿主）不在 Windows 目录里，一定能列出来
        var self = Environment.ProcessPath!;
        Assert.Contains(snapshot.Apps, a => a.Source == AppSource.Process && string.Equals(a.ExePath, self, StringComparison.OrdinalIgnoreCase));
        Assert.All(snapshot.Apps, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }

    [Fact]
    public void ReadsVersionInfoOfPrograms()
    {
        var id = ExeIdentityReader.Read(Environment.ProcessPath!);
        Assert.NotNull(id);
        Assert.Equal("Microsoft", id.Company);
        Assert.Null(ExeIdentityReader.Read(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
    }

    [Fact]
    public void ChecksAuthenticodeSigner()
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(dotnet))
        {
            var sig = ExeIdentityReader.ReadSignature(dotnet);
            Assert.Contains("Microsoft", sig.Signer, StringComparison.Ordinal);
            Assert.True(sig.Valid, $"0x{sig.Status:X8}");
        }

        var unsigned = Path.Combine(Path.GetTempPath(), $"dt-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllText(unsigned, "not a program");
        try
        {
            var none = ExeIdentityReader.ReadSignature(unsigned);
            Assert.Null(none.Signer);
            Assert.False(none.Valid);
        }
        finally
        {
            File.Delete(unsigned);
        }
    }
}
