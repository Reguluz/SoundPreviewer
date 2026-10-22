using System.Text.Json;
using Avalonia.Controls;
using Avalonia.VisualTree;
using SoundPreviewer.Core.Models;
using SoundPreviewer.Core.Services;

namespace SoundPreviewer.App;

public partial class MainWindow
{
    private async Task<bool> CheckRendererSettingsIsolationAsync()
    {
        // Only mutate the same empty/synthetic libraries used by the editing smoke test.
        if (_settings.Plugins.Any(plugin => plugin.Backend != RenderBackend.Unconfigured) ||
            Presets.Any(row => row.Record.SourceId != "test-only-ui-fixture")) return false;
        var savedSettings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_settings, LibraryJson.Options), LibraryJson.Options)!;
        var savedRecords = _store.LoadPresets();
        try
        {
            Pages.SelectedItem = SettingsTab;
            SettingsPages.SelectedItem = RenderSettingsTab;
            await SettleLayoutAsync();
            if (!PrepareRendererButton.IsEffectivelyVisible || !PrepareRendererButton.IsEnabled ||
                !RendererSetupLocation.Contains(Path.Combine(App.DataDirectory, "renderers"), StringComparison.Ordinal))
                throw new InvalidOperationException("一键准备入口未显示或未指向当前 Preview。");
            var setupResult = Path.Combine(App.DataDirectory, "diagnostics", "TEST ONLY 中文 setup.json");
            var setupCommand = CreateRendererSetupCommand(AppContext.BaseDirectory, App.DataDirectory, setupResult,
                PluginSelectionService.CurrentPlatform, PluginSelectionService.CurrentArchitecture);
            if (!File.Exists(setupCommand.Script) || setupCommand.Arguments.Contains("-PythonPath") ||
                setupCommand.Arguments.Contains("--python") || !setupCommand.Arguments.Contains(setupResult) ||
                !setupCommand.Arguments.Contains(App.DataDirectory) ||
                !setupCommand.Arguments.Any(value => value is "-IncludeSerum2" or "--include-serum2"))
                throw new InvalidOperationException("一键准备未使用随包脚本、私有结果路径或仍要求系统 Python。");
            var combo = PluginGrid.GetVisualDescendants().OfType<ComboBox>().First(control =>
                control.IsEffectivelyVisible && control.DataContext is PluginConfiguration { Synth: SynthKind.Serum1 });
            combo.SelectedIndex = (int)RenderBackend.DawDreamer;
            await SettleLayoutAsync();
            CheckIsolatedBackend();
            RefreshGrid(PluginGrid);
            SettingsPages.SelectedItem = StorageSettingsTab;
            await SettleLayoutAsync();
            SettingsPages.SelectedItem = RenderSettingsTab;
            await SettleLayoutAsync();
            CheckIsolatedBackend();
            foreach (var control in PluginGrid.GetVisualDescendants().OfType<ComboBox>().Where(control => control.IsEffectivelyVisible))
                if (control.DataContext is PluginConfiguration model && control.SelectedItem is RenderBackend selected && selected != model.Backend)
                    throw new InvalidOperationException("切换页面后渲染方式与对应音源不一致。");

            combo = PluginGrid.GetVisualDescendants().OfType<ComboBox>().First(control =>
                control.IsEffectivelyVisible && control.DataContext is PluginConfiguration { Synth: SynthKind.Serum1 });
            combo.SelectedIndex = (int)RenderBackend.Sushi;
            await SettleLayoutAsync();
            CheckIsolatedBackend(RenderBackend.Sushi);
            await SaveSettingsCoreAsync(CancellationToken.None);
            if (_store.LoadSettings().Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).Backend != RenderBackend.Sushi)
                throw new InvalidOperationException("自行配置的 Sushi 方式未保存，或被自动音源配置覆盖。");
            combo.SelectedIndex = (int)RenderBackend.DawDreamer;
            await SettleLayoutAsync();
            CheckIsolatedBackend();

            string offline = Path.Combine(App.DataDirectory, "TEST-ONLY-offline-python", "python.exe");
            Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).ExecutablePath = offline;
            Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).ExecutableHash = "TEST-ONLY-last-known-executable";
            Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).PluginHash = "TEST-ONLY-last-known-plugin";
            Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2).Backend = RenderBackend.DawDreamer;
            Plugins.Single(plugin => plugin.Synth == SynthKind.Spire).Backend = RenderBackend.DawDreamer;
            RefreshGrid(PluginGrid);
            // Run the actual save handler without swallowing exceptions: missing executable
            // and version fields in different rows must not block this valid settings save.
            await SaveSettingsCoreAsync(CancellationToken.None);
            var stored = _store.LoadSettings();
            var runtime = CreateExecutionSettings(stored);
            if (runtime.Plugins.Any(plugin => plugin.Backend != RenderBackend.Unconfigured) ||
                stored.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).Backend != RenderBackend.DawDreamer)
                throw new InvalidOperationException("未配置完整的宿主进入执行，或执行前检查覆盖了保存的渲染方式。");
            if (stored.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).ExecutablePath != offline ||
                stored.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).ExecutableHash != "TEST-ONLY-last-known-executable" ||
                stored.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum1).PluginHash != "TEST-ONLY-last-known-plugin" ||
                stored.Plugins.Single(plugin => plugin.Synth == SynthKind.Serum2).Backend != RenderBackend.DawDreamer ||
                stored.Plugins.Single(plugin => plugin.Synth == SynthKind.Spire).Backend != RenderBackend.DawDreamer ||
                RenderReadinessService.Evaluate(stored).Any(state => state.CanAttemptRender))
                throw new InvalidOperationException("未配置完整的音源阻止保存、路径丢失或错误显示为可生成。");
            return true;
        }
        finally
        {
            _store.SaveState(savedRecords, savedSettings);
            _settings = savedSettings;
            PopulateSettings();
            ClearFilterSelections();
            SetPresets(savedRecords);
        }

        void CheckIsolatedBackend(RenderBackend chosen = RenderBackend.DawDreamer)
        {
            if (Plugins.Any(plugin => plugin.Backend != (plugin.Synth == SynthKind.Serum1 ? chosen : RenderBackend.Unconfigured)))
                throw new InvalidOperationException("更改 Serum 1 的渲染方式修改了其他音源。");
        }
    }
}
