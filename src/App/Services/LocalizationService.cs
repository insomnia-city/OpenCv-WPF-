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
        ["palette.read"] = "Read Tag",
        ["palette.write"] = "Write Tag",
        ["palette.wait"] = "Wait Tag",
        ["palette.home"] = "Home Axis",
        ["palette.moveAbs"] = "Move Absolute",
        ["palette.moveRel"] = "Move Relative",
        ["palette.line"] = "Move Line",
        ["palette.waitInPos"] = "Wait In Position",
        ["palette.dout"] = "Digital Out",
        ["palette.dataWrite"] = "Write Record",
        ["palette.dataQuery"] = "Query Data",
        ["node.input"] = "Inputs",
        ["node.output"] = "Outputs",
        ["property.title"] = "Properties",
        ["property.empty"] = "Select a node to edit its parameters",
        ["status.ready"] = "Ready",
        ["status.noGraph"] = "No graph loaded",
        ["tab.editor"] = "Editor",
        ["tab.dashboard"] = "Dashboard",
        ["dash.title"] = "Trace Board",
        ["dash.refresh"] = "Refresh",
        ["dash.clear"] = "Clear",
        ["dash.export"] = "Export CSV",
        ["dash.dimension"] = "Slice by",
        ["dash.cycles"] = "Cycles",
        ["dash.ok"] = "OK",
        ["dash.ng"] = "NG",
        ["dash.yield"] = "Yield",
        ["dash.records"] = "Recent Cycles",
        ["dash.slices"] = "Yield Slices",
        ["dash.preview"] = "Result Preview",
        ["dash.nopreview"] = "No preview frame",
        ["dash.col.seq"] = "Seq",
        ["dash.col.trigger"] = "Trigger",
        ["dash.col.batch"] = "Batch",
        ["dash.col.node"] = "Node",
        ["dash.col.kind"] = "Kind",
        ["dash.col.ts"] = "Time",
        ["dash.col.key"] = "Key",
        ["dash.col.cycles"] = "Cycles",
        ["dash.col.ok"] = "OK",
        ["dash.col.ng"] = "NG",
        ["dash.col.yield"] = "Yield",
        ["dim.line"] = "Line",
        ["dim.machine"] = "Machine",
        ["dim.shift"] = "Shift",
        ["dim.model"] = "Model",
        ["dim.recipe"] = "Recipe",
        ["dim.date"] = "Date",
        ["tab.audit"] = "Audit",
        ["menu.undoToSave"] = "Undo to Saved",
        ["role.label"] = "Role",
        ["role.readonly"] = "Read-only",
        ["role.operator"] = "Operator",
        ["role.engineer"] = "Engineer",
        ["role.admin"] = "Administrator",
        ["status.denied"] = "Permission denied",
        ["audit.title"] = "Operation Audit",
        ["audit.refresh"] = "Refresh",
        ["audit.clear"] = "Clear",
        ["audit.export"] = "Export CSV",
        ["audit.user"] = "User",
        ["audit.action"] = "Action",
        ["audit.target"] = "Object",
        ["audit.empty"] = "No audit entries",
        ["audit.col.at"] = "Time",
        ["audit.col.user"] = "User",
        ["audit.col.action"] = "Action",
        ["audit.col.target"] = "Object"
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
        ["palette.read"] = "读 Tag",
        ["palette.write"] = "写 Tag",
        ["palette.wait"] = "等待 Tag",
        ["palette.home"] = "回零",
        ["palette.moveAbs"] = "绝对定位",
        ["palette.moveRel"] = "相对移动",
        ["palette.line"] = "直线插补",
        ["palette.waitInPos"] = "等待到位",
        ["palette.dout"] = "数字输出",
        ["palette.dataWrite"] = "写记录",
        ["palette.dataQuery"] = "查询数据",
        ["node.input"] = "输入",
        ["node.output"] = "输出",
        ["property.title"] = "属性",
        ["property.empty"] = "选择节点以编辑其参数",
        ["status.ready"] = "就绪",
        ["status.noGraph"] = "未载入图",
        ["tab.editor"] = "编辑",
        ["tab.dashboard"] = "看板",
        ["dash.title"] = "追溯看板",
        ["dash.refresh"] = "刷新",
        ["dash.clear"] = "清空",
        ["dash.export"] = "导出 CSV",
        ["dash.dimension"] = "切片维度",
        ["dash.cycles"] = "产量",
        ["dash.ok"] = "良品",
        ["dash.ng"] = "不良",
        ["dash.yield"] = "良率",
        ["dash.records"] = "最近周期",
        ["dash.slices"] = "良率切片",
        ["dash.preview"] = "结果预览",
        ["dash.nopreview"] = "暂无预览帧",
        ["dash.col.seq"] = "序号",
        ["dash.col.trigger"] = "触发",
        ["dash.col.batch"] = "批次",
        ["dash.col.node"] = "节点",
        ["dash.col.kind"] = "类别",
        ["dash.col.ts"] = "时间",
        ["dash.col.key"] = "分组",
        ["dash.col.cycles"] = "产量",
        ["dash.col.ok"] = "良品",
        ["dash.col.ng"] = "不良",
        ["dash.col.yield"] = "良率",
        ["dim.line"] = "线别",
        ["dim.machine"] = "机台",
        ["dim.shift"] = "班次",
        ["dim.model"] = "机型",
        ["dim.recipe"] = "Recipe",
        ["dim.date"] = "日期",
        ["tab.audit"] = "审计",
        ["menu.undoToSave"] = "撤回到保存点",
        ["role.label"] = "角色",
        ["role.readonly"] = "只读",
        ["role.operator"] = "操作员",
        ["role.engineer"] = "工程师",
        ["role.admin"] = "管理员",
        ["status.denied"] = "权限不足",
        ["audit.title"] = "操作审计",
        ["audit.refresh"] = "刷新",
        ["audit.clear"] = "清空",
        ["audit.export"] = "导出 CSV",
        ["audit.user"] = "用户",
        ["audit.action"] = "动作",
        ["audit.target"] = "对象",
        ["audit.empty"] = "暂无审计记录",
        ["audit.col.at"] = "时间",
        ["audit.col.user"] = "用户",
        ["audit.col.action"] = "动作",
        ["audit.col.target"] = "对象"
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
        ["palette.read"] = "태그 읽기",
        ["palette.write"] = "태그 쓰기",
        ["palette.wait"] = "태그 대기",
        ["palette.home"] = "원점 복귀",
        ["palette.moveAbs"] = "절대 이동",
        ["palette.moveRel"] = "상대 이동",
        ["palette.line"] = "직선 보간",
        ["palette.waitInPos"] = "위치 대기",
        ["palette.dout"] = "디지털 출력",
        ["palette.dataWrite"] = "레코드 쓰기",
        ["palette.dataQuery"] = "데이터 조회",
        ["node.input"] = "입력",
        ["node.output"] = "출력",
        ["property.title"] = "속성",
        ["property.empty"] = "노드를 선택하여 매개변수를 편집하세요",
        ["status.ready"] = "준비됨",
        ["status.noGraph"] = "그래프가 없습니다",
        ["tab.editor"] = "편집",
        ["tab.dashboard"] = "대시보드",
        ["dash.title"] = "추적 대시보드",
        ["dash.refresh"] = "새로고침",
        ["dash.clear"] = "지우기",
        ["dash.export"] = "CSV 내보내기",
        ["dash.dimension"] = "슬라이스 기준",
        ["dash.cycles"] = "생산량",
        ["dash.ok"] = "양품",
        ["dash.ng"] = "불량",
        ["dash.yield"] = "수율",
        ["dash.records"] = "최근 사이클",
        ["dash.slices"] = "수율 슬라이스",
        ["dash.preview"] = "결과 미리보기",
        ["dash.nopreview"] = "미리보기 프레임 없음",
        ["dash.col.seq"] = "순번",
        ["dash.col.trigger"] = "트리거",
        ["dash.col.batch"] = "배치",
        ["dash.col.node"] = "노드",
        ["dash.col.kind"] = "종류",
        ["dash.col.ts"] = "시간",
        ["dash.col.key"] = "그룹",
        ["dash.col.cycles"] = "생산량",
        ["dash.col.ok"] = "양품",
        ["dash.col.ng"] = "불량",
        ["dash.col.yield"] = "수율",
        ["dim.line"] = "라인",
        ["dim.machine"] = "설비",
        ["dim.shift"] = "교대",
        ["dim.model"] = "모델",
        ["dim.recipe"] = "레시피",
        ["dim.date"] = "날짜",
        ["tab.audit"] = "감사",
        ["menu.undoToSave"] = "저장 지점으로 실행 취소",
        ["role.label"] = "역할",
        ["role.readonly"] = "읽기 전용",
        ["role.operator"] = "작업자",
        ["role.engineer"] = "엔지니어",
        ["role.admin"] = "관리자",
        ["status.denied"] = "권한이 없습니다",
        ["audit.title"] = "작업 감사",
        ["audit.refresh"] = "새로고침",
        ["audit.clear"] = "지우기",
        ["audit.export"] = "CSV 내보내기",
        ["audit.user"] = "사용자",
        ["audit.action"] = "동작",
        ["audit.target"] = "대상",
        ["audit.empty"] = "감사 기록 없음",
        ["audit.col.at"] = "시간",
        ["audit.col.user"] = "사용자",
        ["audit.col.action"] = "동작",
        ["audit.col.target"] = "대상"
    };

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _tables = new(StringComparer.Ordinal)
    {
        ["en"] = En,
        ["zh-Hans"] = ZhHans,
        ["zh"] = ZhHans,   // two-letter alias: zh-CN / zh-Hant still resolve Chinese · 双字母别名：zh-CN / zh-Hant 仍解析中文
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