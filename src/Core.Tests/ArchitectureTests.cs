using System.IO;
using System.Linq;
using System.Xml.Linq;
using HalconWorkflow.Core.Execution;

namespace HalconWorkflow.Core.Tests;

public sealed class ArchitectureMeritTests
{
    /// <summary>
    /// Asserts Core carries no external package/framework references (zero-dependency rule, ADR-002). 
    /// 断言 Core 无外部包/框架引用(零依赖铁律, ADR-002)
    /// </summary>
    [Fact]
    public void Core_HasZeroExternalDependencies()
    {
        var csproj = LocateCoreCsproj();
        var xml = XDocument.Load(csproj);
        XNamespace ns = "http://schemas.microsoft.com/developer/msbuild/2003";

        var packageRefs = xml.Descendants(ns + "PackageReference").ToList();
        Assert.Empty(packageRefs);

        // No WPF/Halcon/comm framework references allowed. · 禁止 WPF/Halcon/通讯框架引用
        var frameworkRefs = xml.Descendants(ns + "FrameworkReference")
            .Select(r => (string?)r.Attribute("Include"))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();
        Assert.DoesNotContain(frameworkRefs, r => r.Contains("WPF", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(frameworkRefs, r => r.Contains("Halcon", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asserts Core references no WPF assembly in code (no using of System.Windows). 
    /// 断言 Core 代码不引用 WPF 程序集
    /// </summary>
    [Fact]
    public void Core_Code_DoesNotReferenceWpf()
    {
        var dir = LocateCoreDir();
        var files = Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            Assert.DoesNotContain("System.Windows", text);
            Assert.DoesNotContain("PresentationFramework", text);
        }
    }

    /// <summary>
    /// Asserts the runtime headless path is UI-free: scheduler can execute a graph without any UI thread. 
    /// 断言无头执行路径不依赖 UI 线程
    /// </summary>
    [Fact]
    public async Task HeadlessRun_NoUiThread_Works()
    {
        // Runs on the test pool thread with no dispatcher; must complete. 
        // 直接在测试线程池执行,无 Dispatcher,必须完成
        var graph = SchedulerTests.MakeHeadlessGraph();
        await using var scheduler = new Execution.GraphScheduler();
        scheduler.Load(graph);
        var result = await scheduler.RunOnceAsync(CancellationToken.None);
        Assert.True(result.Success);
    }

    private static string LocateCoreDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Core");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate src/Core directory.");
    }

    private static string LocateCoreCsproj() =>
        Path.Combine(LocateCoreDir(), "HalconWorkflow.Core.csproj");
}