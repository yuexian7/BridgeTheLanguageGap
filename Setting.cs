using System;
using System.Collections.Generic;
using Colossal;
using Colossal.IO.AssetDatabase;
using Game;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Widgets;

namespace Cs2AutoTranslator
{
    // [FileLocation] 决定存盘文件名（本模组实际用自管 JSON，见 SettingsStore；此特性仅保留以兼容框架）。
    // 分组顺序（需求 6/7）：总开关 → 翻译范围 → 翻译语言与保存 → API密钥与测试 → 其它与免责。
    [FileLocation("Cs2AutoTranslator")]
    [SettingsUIGroupOrder(kGroupMain, kGroupScope, kGroupTarget, kGroupKeys, kGroupMisc)]
    [SettingsUIShowGroupName(kGroupMain, kGroupScope, kGroupTarget, kGroupKeys, kGroupMisc)]
    public class TranslatorSetting : ModSetting
    {
        public const string kSection = "Main";
        public const string kGroupMain = "MainSwitch";
        public const string kGroupScope = "Scope";
        public const string kGroupTarget = "Target";   // 需求7：目标语言 + 保存按钮（在翻译范围下方）
        public const string kGroupKeys = "Keys";
        public const string kGroupMisc = "Misc";

        public TranslatorSetting(IMod mod) : base(mod) { }

        // ===== 总开关（需求 8/6）=====
        // 首次默认【关闭】：不打翻译，界面全是原文。开启即时开始按需翻译；关闭即时恢复源语言（需求6：不必再点保存）。
        // 用带私有字段的属性：set 时触发 OnEnabledChanged → Mod 端订阅为 SaveNow()（写盘+同步+重刷），做到「即开即翻、即关即回原文」。
        public static event Action OnEnabledChanged;

        private bool _enabled = false;
        [SettingsUISection(kSection, kGroupMain)]
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value) return;   // 同值不触发（框架初始化/SetDefaults 常重复赋值，避免多余保存）
                _enabled = value;
                try { OnEnabledChanged?.Invoke(); } catch { }
            }
        }

        // ===== 翻译范围（需求 2/6）=====
        // 官方 ModSetting 没有原生「复选框组」，用 6 个独立开关实现多选。
        [SettingsUISection(kSection, kGroupScope)]
        public bool ScopeModOptions { get; set; } = true;        // 模组选项及说明

        [SettingsUISection(kSection, kGroupScope)]
        public bool ScopeAssetNames { get; set; } = true;        // 资产名称

        [SettingsUISection(kSection, kGroupScope)]
        public bool ScopeAssetDescriptions { get; set; } = true; // 资产描述（仅标题下方说明，不含参数名）

        [SettingsUISection(kSection, kGroupScope)]
        public bool ScopeGameCore { get; set; } = true;          // 游戏本体（其余全部文字）

        [SettingsUISection(kSection, kGroupScope)]
        public bool ScopeModName { get; set; } = false;          // 模组名称（实验性）

        [SettingsUISection(kSection, kGroupScope)]
        public bool ScopeWorldLabels { get; set; } = false;      // 游戏画面图层（路名/贴地文字，实验性）

        // 玩家自定义内容保护说明（需求 6/11）：去掉 [SettingsUIMultilineText]（该特性不渲染正文，只显示标题），
        // 改为 bare string getter → 框架按 StringField 渲染，文字显示在该行【最右侧】（需求6「置于最右侧」）。
        [SettingsUISection(kSection, kGroupScope)]
        public string ScopeProtect =>
            SelfL10n.T(GameManager.instance?.localizationManager?.activeLocaleId ?? "en-US", "desc.scope.protect");

        // ===== 翻译语言与保存（需求 7：目标语言 + 保存按钮同组，置于翻译范围下方）=====
        // 目标语言下拉（需求 2/3）：列出 LanguageCatalog 中「母语名能被游戏字体正常显示」的约 44 种语言。
        [SettingsUIDropdown(typeof(TranslatorSetting), nameof(GetLocaleItems))]
        [SettingsUISection(kSection, kGroupTarget)]
        public string TargetLocale { get; set; } = "zh-HANS";

        // 保存按钮（需求 6/8）：把上面填的开关/选项/key 立即写盘（自管 JSON），重启不丢；并即时按新设置重刷界面。
        public static event Action OnSaveClicked;

        [SettingsUIButton]
        [SettingsUISection(kSection, kGroupTarget)]
        public bool SaveConfig { set { OnSaveClicked?.Invoke(); } }

        public static string LastSaveResult = "（尚未保存）";   // 仅供 Mod.SaveNow 写日志用，已不在界面单独显示（需求8 改显示日志路径）。

        public DropdownItem<string>[] GetLocaleItems()
        {
            var items = new List<DropdownItem<string>>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var lang in LanguageCatalog.All)
            {
                if (seen.Add(lang.code))
                    items.Add(new DropdownItem<string> { value = lang.code, displayName = lang.name });
            }
            // 当前游戏语言若不在目录里，也补进去，方便玩家「翻成我现在用的语言之外的另一种」。
            string active = GameManager.instance?.localizationManager?.activeLocaleId;
            if (!string.IsNullOrEmpty(active) && seen.Add(active))
                items.Add(new DropdownItem<string> { value = active, displayName = active });
            return items.ToArray();
        }

        // ===== API密钥与测试（需求 2/7：翻译引擎置于本组最上方，方便「选引擎→填 key→测试」）=====
        // 默认微软（=必应官方翻译 API）：准确度高、端点可达、F0 免费额度 200 万字符/月。
        // 谷歌免 key 接口已被封（429），仅作末位备选；DeepL 最准但需 key 且常需 VPN；百度中国最稳但需 APP ID+key。
        public enum Engine { Microsoft, DeepL, Baidu, Google }

        [SettingsUISection(kSection, kGroupKeys)]
        public Engine TranslationEngine { get; set; } = Engine.Microsoft;

        [SettingsUITextInput]
        [SettingsUISection(kSection, kGroupKeys)]
        public string MicrosoftKey { get; set; } = string.Empty;

        // 需求2：部分 Azure 翻译资源（区域/多服务资源）必须带 Ocp-Apim-Subscription-Region 头，否则即使 key 正确也 401。
        // 全球（Global）资源不需要，不确定就留空。
        [SettingsUITextInput]
        [SettingsUISection(kSection, kGroupKeys)]
        public string MicrosoftRegion { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kSection, kGroupKeys)]
        public string DeepLKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kSection, kGroupKeys)]
        public string BaiduAppId { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kSection, kGroupKeys)]
        public string BaiduKey { get; set; } = string.Empty;

        // 测试按钮（需求 7/8）：只测引擎服务连通性，按【目标语言】翻译探针并把结果回显（不再永远显示中文）。
        public static event Action OnTestClicked;

        [SettingsUIButton]
        [SettingsUISection(kSection, kGroupKeys)]
        public bool TestTranslation { set { OnTestClicked?.Invoke(); } }

        public static string LastTestResult = "（尚未测试）";

        [SettingsUISection(kSection, kGroupKeys)]
        public string TestResult => LastTestResult;

        public static TranslatorSetting Instance { get; private set; }

        // ===== 其它与免责（需求 8/9/10）=====
        // 日志路径（需求8）：bare string getter → StringField，显示本模组专属日志文件路径（取代旧「上次保存结果」）。
        [SettingsUISection(kSection, kGroupMisc)]
        public string LogPath => ModLog.FilePath;

        // 打开日志文件夹（需求8）：框架无超链接控件，改用按钮，点击用系统文件管理器打开日志所在目录。
        public static event Action OnOpenLogFolderClicked;

        [SettingsUIButton]
        [SettingsUISection(kSection, kGroupMisc)]
        public bool OpenLogFolder { set { OnOpenLogFolderClicked?.Invoke(); } }

        // 清除缓存（需求10）：清空翻译缓存 + 运行时译文表，全部恢复原文；【不是】重置设置（选项保持不变）。
        public static event Action OnClearCacheClicked;

        [SettingsUIButton]
        [SettingsUISection(kSection, kGroupMisc)]
        public bool ClearCache { set { OnClearCacheClicked?.Invoke(); } }

        // 免责声明（需求9）：[SettingsUIMultilineText] 不渲染正文，故把【全文放进标题】——见 L10n 的 SettingsLocale：
        // 本属性的 label 被映射到 text.disclaimer 全文，于是标题即完整免责说明。getter 仍返回全文以兼容。
        [SettingsUIMultilineText]
        [SettingsUISection(kSection, kGroupMisc)]
        public string Disclaimer =>
            SelfL10n.T(GameManager.instance?.localizationManager?.activeLocaleId ?? "en-US", "text.disclaimer");

        public override void SetDefaults()
        {
            Enabled = false;
            TranslationEngine = Engine.Microsoft;
            TargetLocale = "zh-HANS";
            ScopeModOptions = true;
            ScopeAssetNames = true;
            ScopeAssetDescriptions = true;
            ScopeGameCore = true;
            ScopeModName = false;
            ScopeWorldLabels = false;
            MicrosoftKey = string.Empty;
            MicrosoftRegion = string.Empty;
            DeepLKey = string.Empty;
            BaiduAppId = string.Empty;
            BaiduKey = string.Empty;
        }

        // 取指定引擎对应的 key（百度另需 BaiduAppId；谷歌免 key 返回空）。
        public string GetKeyFor(Engine e)
        {
            switch (e)
            {
                case Engine.Microsoft: return MicrosoftKey;
                case Engine.DeepL: return DeepLKey;
                case Engine.Baidu: return BaiduKey;
                default: return string.Empty;
            }
        }

        // 在主线程调用：注册到游戏选项界面 + 按游戏语言挂多语言标签 + 从我们自己的配置文件载入用户设置。
        // mod 必须是真正的 IMod 实例（Mod.OnLoad 里赋的 Mod.Instance），不能用空壳：
        // ModManager 要求一个程序集里只有一个公开 IMod，多一个就报 IsNotUniqueWarning。
        public static void Register(Mod mod)
        {
            var s = new TranslatorSetting(mod);
            s.RegisterInOptionsUI();

            // 本模组界面文字跟随游戏语言：给每个受支持的 locale 都注册一份对应文字的 SettingsLocale。
            var lm = GameManager.instance.localizationManager;
            foreach (var locale in lm.GetSupportedLocales())
                lm.AddSource(locale, new SettingsLocale(s, locale));

            // 继续用自管 JSON（写在官方的 ModsSettings\Cs2AutoTranslator.json）而不用游戏原生 .coc 资产：
            // 一是玩家现有的设置已经在 JSON 里，换存储会静默丢掉；二是 [FileLocation] 那套要配合
            // AssetDatabase.global.LoadSettings 才落盘，而本模组的设置项带自定义事件回调，自管更可控。
            SettingsStore.Load(s);

            // Instance 放到最后再赋值：后台线程一旦看到 Instance!=null，就能保证配置已加载完，不会读到空 key。
            Instance = s;

            // 若当前游戏语言不在官方 12 种硬编码表内，启动本模组界面文字的机翻兜底（需求 12）。
            // 放在 Instance 赋值之后：SelfL10n 的后台翻译需要读到已就绪的引擎/目标设置。
            SelfL10n.EnsureSelfTranslation(lm.activeLocaleId);
        }
    }
}
