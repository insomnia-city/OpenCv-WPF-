using HalconWorkflow.Abstractions;
using HalconWorkflow.App.Services;
using HalconWorkflow.App.ViewModels;
using Xunit;

namespace HalconWorkflow.App.Tests;

/// <summary>
/// Stage-17 gate: the shell discovers and loads an external plugin from its <c>/plugins</c> folder at
/// startup, exposing the result and logging the load (ADR-007).
/// / 阶段17 闸门：壳层启动时从其 <c>/plugins</c> 目录发现并装载外部插件，暴露结果并记录日志（ADR-007）。
/// </summary>
public sealed class PluginStartupTests
{
    [Fact]
    public async Task Shell_Startup_LoadsExternalPluginContract()
    {
        var shell = new ShellViewModel(new LocalizationService(), new NoopDialogService());
        try
        {
            Assert.NotNull(shell.Plugins);
            var loaded = Assert.Single(shell.Plugins!.Succeeded);
            Assert.Equal("SamplePlugin", loaded.Name);
            Assert.Equal(1, loaded.Contracts);
            Assert.Contains(shell.Log.Entries, e => e.Message.StartsWith("plugin SamplePlugin"));
        }
        finally
        {
            await shell.DisposeAsync();
        }
    }

    private sealed class NoopDialogService : IDialogService
    {
        public string? OpenGraphFile(string filter) => null;
        public string? SaveGraphFile(string defaultName, string filter) => null;
        public string? SaveCsvFile(string defaultName) => null;
        public bool Confirm(string message) => true;
        public void ReportError(string message) { }
    }
}
