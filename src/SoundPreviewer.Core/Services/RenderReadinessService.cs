using SoundPreviewer.Core.Models;

namespace SoundPreviewer.Core.Services;

public sealed record RenderReadiness(SynthKind Synth, string Status, string Detail, bool CanAttemptRender, bool IsVerified = false);

public static class RenderReadinessService
{
    private const string Disclaimer = "此为配置检查，未完成实际加载验证。";

    public static IReadOnlyList<RenderReadiness> Evaluate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Enum.GetValues<SynthKind>().Where(synth => synth != SynthKind.Unknown)
            .Select(synth => Evaluate(settings.Plugins.FirstOrDefault(plugin => plugin.Synth == synth) ??
                new PluginConfiguration { Synth = synth }, settings.DiscoveredPlugins, settings.Template)).ToList();
    }

    public static RenderReadiness Evaluate(PluginConfiguration configuration, IReadOnlyList<DiscoveredPlugin> discovered) =>
        Evaluate(configuration, discovered, null);

    private static RenderReadiness Evaluate(PluginConfiguration configuration, IReadOnlyList<DiscoveredPlugin> discovered, PreviewTemplate? template)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(discovered);
        RenderReadiness Result(string status, string detail, bool canAttempt = false) =>
            new(configuration.Synth, status, detail + " " + Disclaimer, canAttempt);

        if (!Enum.IsDefined(configuration.Synth) || configuration.Synth == SynthKind.Unknown)
            return Result("未识别", "尚不能确认目标音源，不能依据文件名启动渲染。");
        var selected = discovered.FirstOrDefault(plugin =>
            SamePath(plugin.FilePath, configuration.PluginPath) || SamePath(plugin.BinaryPath, configuration.PluginPath));
        var candidates = discovered.Where(plugin => plugin.Synth == configuration.Synth).ToList();
        if (configuration.Backend == RenderBackend.Unconfigured)
        {
            bool selectedX86 = selected?.Synth == configuration.Synth && selected.Architecture == PluginArchitecture.X86;
            bool onlyX86 = candidates.Any(plugin => plugin.Architecture == PluginArchitecture.X86) &&
                !candidates.Any(plugin => plugin.Architecture == PluginArchitecture.X64);
            if (selectedX86 || onlyX86)
                return Result("需要32位宿主", "已识别到 32 位音源。DawDreamer 桥接不能加载它；Sylenth1 VST2 可在包含独立 x86 宿主的版本中重新扫描并关联，其他插件需配置兼容的外部宿主。不要求必须寻找 64 位音源包。");
            return Result("未配置", candidates.Count > 0 || !string.IsNullOrWhiteSpace(configuration.PluginPath)
                ? "已发现或关联音源文件，但尚未配置可运行的渲染宿主；扫描识别不等于能够生成音频。"
                : "尚未发现已支持的音源文件，也未配置渲染宿主；请先检查共享插件目录的扫描结果。");
        }
        if (!Enum.IsDefined(configuration.Backend)) return Result("未配置", "未知的渲染后端，请重新选择。");
        if (configuration.Backend == RenderBackend.VitalCli && configuration.Synth != SynthKind.Vital)
            return Result("格式未适配", "Vital 原生渲染后端只能处理 Vital，不能用来加载其他音源。");
        if (configuration.Backend == RenderBackend.DawDreamer)
        {
            if (configuration.Synth is not (SynthKind.Serum1 or SynthKind.Sylenth1))
                return Result("格式未适配", "当前 DawDreamer 桥接仅适配 Serum 1 / Sylenth1 的 VST2 独立 FXP，不支持此音源及其专有预制格式。");
            if (string.IsNullOrWhiteSpace(configuration.PluginPath)) return Result("插件缺失", "尚未关联 VST2 乐器 DLL，请先扫描并选择插件。");
            if (selected?.Format == PluginBinaryFormat.Vst3 || !Path.GetExtension(configuration.PluginPath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
                return Result("格式未适配", "当前 DawDreamer FXP 桥接需要 VST2 乐器 DLL，尚未适配 VST3 文件或插件包。");
            if (!File.Exists(configuration.PluginPath)) return Result("插件缺失", "已关联的插件 DLL 不存在，请检查安装目录并重新扫描。");
            if (selected is null || selected.Synth != configuration.Synth)
                return Result("插件未确认", "所选文件尚未由扫描元数据确认为目标音源；请重新扫描，不能仅凭路径或文件名确认兼容性。");
            if (selected.Architecture == PluginArchitecture.X86)
                return Result("位数不兼容", "所选插件是 32 位，当前 DawDreamer 桥接仅支持 x64。可配置兼容 32 位插件的独立外部宿主或桥接，不要求更换 64 位音源包。");
            if (selected.Architecture != PluginArchitecture.X64)
                return Result("位数不兼容", "所选插件尚未确认为 Windows x64 架构；不能将其交给当前 x64 DawDreamer 桥接。");
            if (selected.Format != PluginBinaryFormat.Vst2) return Result("格式未适配", "所选插件不是已识别的 VST2 乐器文件。");
        }
        if (string.IsNullOrWhiteSpace(configuration.ExecutablePath) || !File.Exists(configuration.ExecutablePath))
            return Result("程序缺失", configuration.Backend == RenderBackend.DawDreamer
                ? "未找到配置的 Python 程序。可选宿主需要 x64 Python 3.11 / 3.12、DawDreamer 0.8.3 和 NumPy 1.26.4；应用不会自动安装。"
                : "未找到配置的渲染程序，请检查程序位置。");
        if (string.IsNullOrWhiteSpace(configuration.Version)) return Result("版本缺失", "尚未填写或识别实际音源版本，不能建立可靠的渲染记录。");
        if (configuration.TimeoutSeconds is < 5 or > 3600) return Result("未配置", "进程超时必须为 5 到 3600 秒，请修正渲染设置。");
        if (configuration.Backend == RenderBackend.VitalCli)
        {
            if (template is not null && !template.QuickPreview)
            {
                try { template.Validate(); }
                catch (ArgumentException error) { return Result("模板不匹配", error.Message); }
                if (template.HoldSeconds < 1 || Math.Abs(template.HoldSeconds - Math.Round(template.HoldSeconds)) > 0.000001 ||
                    template.Velocity != 89 || Math.Abs(template.TailSeconds - template.HoldSeconds * 0.3) > 0.000001)
                    return Result("模板不匹配", "Vital 原生渲染要求整数秒按键时长、力度 89、尾音为按键时长的 30%；请采用 Vital 原生参数。");
            }
            return Result("可尝试", template?.QuickPreview == true
                ? "快速预览将从真实原生输出截取每音最多半秒，弱音捕获最多延长到四秒；Vital 实际按整数秒演奏，力度固定 89。仍须人工确认音色与资源。"
                : "Vital 独立程序和版本已配置；原生 CLI 不报告目标预制加载成功，输出仍须人工确认音色与资源。", true);
        }
        if (configuration.Backend == RenderBackend.DawDreamer)
            return Result("可尝试", "x64 VST2 插件、Python 程序及版本已配置。尚未检查 x64 Python 3.11 / 3.12、DawDreamer 0.8.3、NumPy 1.26.4 的依赖安装和宿主初始化是否成功；须先完成独立宿主预检及单个预制试渲染，不代表已验证可用。", true);
        return Result("可尝试", selected?.Architecture == PluginArchitecture.X86
            ? "外部渲染器程序已配置，允许交给兼容 32 位插件的独立宿主或桥接；是否支持此音源和预制格式仍须实际验证。"
            : "外部渲染器程序已配置；目标插件位数、宿主能力和预制格式支持由该外部宿主负责，尚未验证其实际可用性。", true);
    }

    private static bool SamePath(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return false; }
    }
}
