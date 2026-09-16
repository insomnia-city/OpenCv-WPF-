using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace HalconWorkflow.App.Services;

/// <summary>
/// Culture keys supported for hot language switching (ADR-010). 
/// 支持运行时热切换的语言(Culture 键)
/// </summary>
public static class SupportedCultures
{
    public static readonly IReadOnlyList<CultureInfo> All =
    [
        new("zh-Hans"),
        new("en"),
        new("ko")
    ];
}

/// <summary>
/// Dict-backed localization for the MVP; key resolution returns English on missing keys.
/// Layout note: swap to .resx ResourceManager when the full string inventory lands (§4.8).
/// MVP 用字典驱动本地化;缺键回退英文;完整词条落地后换 .resx ResourceManager(§4.8)
/// </summary>
public sealed class LocalizationService : INotifyPropertyChanged
{
    private static readonly IReadOnlyDictionary<string, string> En = new Dictionary<string, string>
    {
        ["app.title"] = "Halcon Workflow",
        ["menu.load"] = "Load Graph...",
        ["menu.save"] = "Save Graph",
        ["menu.new"] = "New Graph",
        ["menu.run"] = "Run",
        ["menu.stop"] = "Stop",
        ["menu.undo"] = "Undo",
        ["menu.redo"] = "Redo",
        ["menu.language"] = "Language",
        ["log.title"] = "Log",
        ["log.entry"] = "{0}",
        ["palette.title"] = "Node Library",
        ["palette.start"] = "Start",
        ["palette.grabber"] = "Grabber",
        ["palette.threshold"] = "Threshold",
        ["palette.decision"] = "Decision",
        ["palette.result"] = "Result",
        ["palette.branch"] = "Branch",
        ["palette.join"] = "Join",
        ["palette.script"] = "Script",
        ["palette.counter"] = "Counter",
        ["palette.delay"] = "Delay",
        ["palette.measure"] = "Measure",
        ["palette.hdev"] = "HDev Script",
        ["palette.tomat"] = "To Mat",
        ["palette.tohobject"] = "To HObject",
        ["node.input"] = "Inputs",
        ["node.output"] = "Outputs",
        ["property.title"] = "Properties",
        ["property.empty"] = "Select a node to edit its parameters",
        ["status.ready"] = "Ready",
        ["status.noGraph"] = "No graph loaded"
    };

    private static readonly IReadOnlyDictionary<string, string> ZhHans = new Dictionary<string, string>
    {
        ["app.title"] = "Halcon 可视化工作流",
        ["menu.load"] = "载入图...",
        ["menu.save"] = "保存图",
        ["menu.new"] = "新建图",
        ["menu.run"] = "运行",
        ["menu.stop"] = "停止",
        ["menu.undo"] = "撤销",
        ["menu.redo"] = "重做",
        ["menu.language"] = "语言",
        ["log.title"] = "日志",
        ["log.entry"] = "{0}",
        ["palette.title"] = "节点库",
        ["palette.start"] = "起点",
        ["palette.grabber"] = "采集",
        ["palette.threshold"] = "阈值分割",
        ["palette.decision"] = "判定",
        ["palette.result"] = "结果输出",
        ["palette.branch"] = "分支",
        ["palette.join"] = "汇聚",
        ["palette.script"] = "脚本",
        ["palette.counter"] = "计数器",
        ["palette.delay"] = "延时",
        ["palette.measure"] = "测量",
        ["palette.hdev"] = "HDev 脚本",
        ["palette.tomat"] = "转 Mat",
        ["palette.tohobject"] = "转 HObject",
        ["node.input"] = "输入",
        ["node.output"] = "输出",
        ["property.title"] = "属性",
        ["property.empty"] = "选择节点以编辑其参数",
        ["status.ready"] = "就绪",
        ["status.noGraph"] = "未载入图"
    };

    private static readonly IReadOnlyDictionary<string, string> Ko = new Dictionary<string, string>
    {
        ["app.title"] = "할콘 워크플로우",
        ["menu.load"] = "그래프 불러오기...",
        ["menu.save"] = "그래프 저장",
        ["menu.new"] = "새 그래프",
        ["menu.run"] = "실행",
        ["menu.stop"] = "정지",
        ["menu.undo"] = "실행 취소",
        ["menu.redo"] = "다시 실행",
        ["menu.language"] = "언어",
        ["log.title"] = "로그",
        ["log.entry"] = "{0}",
        ["palette.title"] = "노드 라이브러리",
        ["palette.start"] = "시작",
        ["palette.grabber"] = "캡처",
        ["palette.threshold"] = "임계값",
        ["palette.decision"] = "판정",
        ["palette.result"] = "결과 출력",
        ["palette.branch"] = "분기",
        ["palette.join"] = "조인",
        ["palette.script"] = "스크립트",
        ["palette.counter"] = "카운터",
        ["palette.delay"] = "지연",
        ["palette.measure"] = "측정",
        ["palette.hdev"] = "HDev 스크립트",
        ["palette.tomat"] = "Mat 변환",
        ["palette.tohobject"] = "HObject 변환",
        ["node.input"] = "입력",
        ["node.output"] = "출력",
        ["property.title"] = "속성",
        ["property.empty"] = "노드를 선택하여 매개변수를 편집하세요",
        ["status.ready"] = "준비됨",
        ["status.noGraph"] = "그래프가 없습니다"
    };

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tables = new(StringComparer.Ordinal)
    {
        ["en"] = En,
        ["zh-Hans"] = ZhHans,
        ["ko"] = Ko
    };

    private CultureInfo _culture = new("zh-Hans");

    /// <summary>
    /// Current UI culture. Setting it broadcasts to bound strings and refreshes CultureInfo. 
    /// 当前 UI 语言;设置时广播绑定字符串并刷新 CultureInfo
    /// </summary>
    public CultureInfo Culture
    {
        get => _culture;
        set
        {
            if (ReferenceEquals(_culture, value)) return;
            _culture = value ?? throw new ArgumentNullException(nameof(value));
            CultureInfo.CurrentUICulture = value;
            CultureInfo.CurrentCulture = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item"));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Culture"));
        }
    }

    /// <summary>
    /// Localized text for a key; English backstop when a key is missing (ADR-010 fallback chain). 
    /// 键对应的本地化文案;缺键回退英文(ADR-010 回退链)
    /// </summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// Resolves a key with optional format arguments. · 解析键并支持格式化参数
    /// </summary>
    public string Get(string key, params object[] args)
    {
        var root = _culture.Name;
        var lang = _culture.TwoLetterISOLanguageName;

        if (_tables.TryGetValue(root, out var t1) && t1.TryGetValue(key, out var v1)) return Format(v1, args);
        if (_tables.TryGetValue(lang, out var t2) && t2.TryGetValue(key, out var v2)) return Format(v2, args);
        if (En.TryGetValue(key, out var vEn)) return Format(vEn, args);
        return key;
    }

    private static string Format(string template, object[] args) => args.Length == 0 ? template : string.Format(template, args);

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Emits localized column titles used by VM properties. · 供 VM 属性使用的本地化列标题
/// </summary>
public sealed class LocalizedTitle : INotifyPropertyChanged
{
    private readonly LocalizationService _loc;
    private readonly string _key;

    public LocalizedTitle(LocalizationService loc, string key)
    {
        _loc = loc;
        _key = key;
        if (loc is not null)
            ((INotifyPropertyChanged)loc).PropertyChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
    }

    public string Value => _loc.Get(_key);

    public event PropertyChangedEventHandler? PropertyChanged;
}