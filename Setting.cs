using System;
using System.Collections.Generic;
using Colossal;
using Game;
using Game.Input;
using Game.Modding;
using Game.SceneFlow;
using Game.Settings;
using Game.UI.Widgets;

namespace Cs2AutoTranslator
{
    // 分组顺序（需求 6/7）：总开关 → 翻译范围 → 翻译语言与保存 → API密钥与测试 → 其它与免责。
    // 不加 [FileLocation]：本模组唯一的存储是 ModsSettings\Cs2AutoTranslator.json（见 SettingsStore）。
    // ⚠ 实测更正（2026-09-06，v0.30 那局）：去掉这个特性**并没有**让 <UserData>\Cs2AutoTranslator.coc 消失 ——
    // 该文件仍在，且退出时被框架重写（mtime 00:34:13，同目录全部 41 个 .coc 都是这一批），内容停在早期 5 个字段的
    // 旧快照（MicrosoftKey 还是当年的输入法垃圾值），与 JSON 完全不同步。也就是说它是框架设置库里的一条
    // 历史记录，退出时原样回写；本模组既不读它也不再往里写。
    // 未验证的两问（删掉文件跑一局即可判定）：① 手工删了会不会再生；② 全新安装的玩家会不会一开始就没有它。
    // 若答案是「不会再生 / 新玩家没有」，那 v0.30 线上更新日志那句「不再生成 .coc」只是措辞不严；若会再生就是错的。
    [SettingsUITabOrder(kTabTranslate, kTabEngine, kTabMisc)]
    [SettingsUIGroupOrder(kGroupMain, kGroupTarget, kGroupScope,
                          kGroupEngine, kGroupTest,
                          kGroupHotkeys, kGroupLog, kGroupDisclaimer)]
    [SettingsUIShowGroupName(kGroupMain, kGroupTarget, kGroupScope,
                             kGroupEngine, kGroupTest,
                             kGroupHotkeys, kGroupLog, kGroupDisclaimer)]
    public class TranslatorSetting : ModSetting
    {
        // 需求1：SettingsUISection(tab, group) 的【第一个参数就是标签页 id】（特性构造函数的形参名就叫 tab），
        // 第二个才是页内板块。v0.31 之前所有选项都挂在 "Main" 一页，而 EffectiveModel / DefaultModel / Instance
        // 这几个纯代码用的公开属性没带任何 SettingsUI 特性，被框架归进了 id 为空串的默认标签页 ——
        // 玩家看到的就是「主设置旁边一个空白标签页」。现在拆成三页，并给那几个属性补上 [SettingsUIHidden]。
        public const string kTabTranslate = "Translate";   // 翻译设置
        public const string kTabEngine = "Engine";         // 翻译引擎
        public const string kTabMisc = "Misc";             // 快捷键与其它

        public const string kGroupMain = "MainSwitch";     // 总开关
        public const string kGroupTarget = "Target";       // 目标语言（含保存按钮）
        public const string kGroupScope = "Scope";         // 翻译范围
        public const string kGroupEngine = "Engine";       // 选择引擎（含说明、注册按钮、各家 key、模型）
        public const string kGroupTest = "Test";           // 测试
        public const string kGroupHotkeys = "Hotkeys";     // 快捷键设置（需求6）
        public const string kGroupLog = "Log";             // 日志
        public const string kGroupDisclaimer = "Disclaimer"; // 声明

        public TranslatorSetting(IMod mod) : base(mod) { }

        // ===== 总开关（需求 8/6）=====
        // 首次默认【关闭】：不打翻译，界面全是原文。开启即时开始按需翻译；关闭即时恢复源语言（需求6：不必再点保存）。
        // 用带私有字段的属性：set 时触发 OnEnabledChanged → Mod 端订阅为 SaveNow()（写盘+同步+重刷），做到「即开即翻、即关即回原文」。
        public static event Action OnEnabledChanged;

        private bool _enabled = false;
        [SettingsUISection(kTabTranslate, kGroupMain)]
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

        // ===== 翻译范围（需求 2/6，v0.31 需求3 重排）=====
        // 官方 ModSetting 没有原生「复选框组」，用 6 个独立开关实现多选。界面顺序 = 这里的声明顺序。
        // 反馈2：默认【一个都不勾】。范围直接决定要不要联网、要不要花额度，替玩家默认勾上等于替他做了这个决定；
        // 玩家勾了什么由自管 JSON 存着（Mod.cs 的 SettingsStore），重启照旧。
        // 反馈1：改成带私有字段的属性 —— 勾/取消都要【即时】生效，不再必须点「保存设置」（保存只负责落盘，
        // 而 Mod 端在通知里顺手也落了盘，所以玩家忘了点保存也不会丢设置）。
        [SettingsUISection(kTabTranslate, kGroupScope)]
        public bool ScopeModName { get => _scopeModName; set { if (Set(ref _scopeModName, value)) NotifyLive(); } }

        [SettingsUISection(kTabTranslate, kGroupScope)]
        public bool ScopeModOptions { get => _scopeModOptions; set { if (Set(ref _scopeModOptions, value)) NotifyLive(); } }

        [SettingsUISection(kTabTranslate, kGroupScope)]
        public bool ScopeAssetNames { get => _scopeAssetNames; set { if (Set(ref _scopeAssetNames, value)) NotifyLive(); } }

        [SettingsUISection(kTabTranslate, kGroupScope)]
        public bool ScopeAssetDescriptions { get => _scopeAssetDescriptions; set { if (Set(ref _scopeAssetDescriptions, value)) NotifyLive(); } }

        [SettingsUISection(kTabTranslate, kGroupScope)]
        public bool ScopeGameCore { get => _scopeGameCore; set { if (Set(ref _scopeGameCore, value)) NotifyLive(); } }

        [SettingsUISection(kTabTranslate, kGroupScope)]
        public bool ScopeWorldLabels { get => _scopeWorldLabels; set { if (Set(ref _scopeWorldLabels, value)) NotifyLive(); } }

        private bool _scopeModName, _scopeModOptions, _scopeAssetNames, _scopeAssetDescriptions, _scopeGameCore, _scopeWorldLabels;

        // 反馈1：范围开关、引擎切换都走这一条通知。Mod 端订阅成「按当前设置立即重刷一遍」，
        // 所以玩家勾上模组名称就马上看到译文、取消马上回到原文，不必再点保存。
        public static event Action OnLiveSettingChanged;

        // 反馈1（本轮更正）：换引擎/换模型【只要求实时存下来】，下一句翻译自然用新设置。
        // 范围开关那条走 OnLiveSettingChanged（勾选即刻翻、取消即刻回原文），两者不能共用一个事件。
        public static event Action OnEngineChanged;

        // 只在设置真正变了之后发；载入玩家存过的配置（SettingsStore.Load）与 SetDefaults 阶段
        // Instance 还没赋值（Register 把它放在最后一步），那时发通知会让 Mod 端读到半套配置去刷界面。
        private void NotifyLive()
        {
            if (Instance == null) return;
            try { OnLiveSettingChanged?.Invoke(); } catch { }
        }

        private void NotifyEngine()
        {
            if (Instance == null) return;
            try { OnEngineChanged?.Invoke(); } catch { }
        }

        // 同值不触发（框架初始化与 SetDefaults 常重复赋值），变了才返回 true。
        private static bool Set(ref bool field, bool value)
        {
            if (field == value) return false;
            field = value;
            return true;
        }

        // 反馈4：世界文字的「刷新方式」下拉已删除。三档里前两档玩家实测没区别、第三档每次强刷都卡顿，
        // 现在只剩一种行为：进存档后统一翻一遍 + 每有新名字按 10 秒节流清一次图层缓存（实现见 Mod.cs 的 WorldLabels），
        // 全部说明并进 desc.scope.worldLabels 那一行，不再单独占一个设置项。
        // 反馈5：「玩家自定义内容保护」说明行（ScopeProtect）已删除 —— 那句「必须保存设置后生效！」在界面上重复了三遍，
        // 后半句也已经写进保存按钮的说明文本，这一行没有信息量了。

        // ===== 翻译语言与保存（需求 7：目标语言 + 保存按钮同组，置于翻译范围下方）=====
        // 目标语言下拉（需求 2/3）：列出 LanguageCatalog 中「母语名能被游戏字体正常显示」的约 44 种语言。
        [SettingsUIDropdown(typeof(TranslatorSetting), nameof(GetLocaleItems))]
        [SettingsUISection(kTabTranslate, kGroupTarget)]
        public string TargetLocale { get; set; } = "zh-HANS";

        // 保存按钮（需求 6/8）：把上面填的开关/选项/key 立即写盘（自管 JSON），重启不丢；并即时按新设置重刷界面。
        public static event Action OnSaveClicked;

        [SettingsUIButton]
        [SettingsUISection(kTabTranslate, kGroupTarget)]
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

        // ===== 翻译引擎与凭据（需求 1/2）=====
        // 下拉框顺序 = 这里的枚举声明顺序：完全免注册 → 需注册有免费额度 → 自定义 AI 收尾（需求2）。
        // 枚举按【名字】存盘（见 Mod.cs 的 SettingsStore），所以重排不会让老玩家的配置失效。
        // 默认从微软改成 Google：免 key 开箱可用（2026-09-08：gtx 那个 client 已被 429 拦，改走 dict-chrome-ex，见 FreeApi）。
        // Yandex 原本在免注册那一段，但它的免 key 网页通道已经死了，现在必须填 IAM token，所以归到需注册段。
        public enum Engine
        {
            Google, DuckDuckGo, MyMemory,
            Yandex, Microsoft, DeepL, Baidu, Gemini, Groq, OpenRouter, SiliconFlow, Cloudflare,
            Custom,
        }

        // 自定义 AI 的内置接口格式。枚举名【必须】与 EngineKit.CustomAi.Vendors 里的字符串逐字对应
        // （直接 ToString() 当 vendor 传下去）；改一边就得改另一边，T3 的 Q 段钉着这张表。
        // 反馈8：末尾加第 9 项 custom —— 8 家之外的任意接口（自架网关、校内代理、其它厂商），
        // 地址/模型名全部由玩家自己填，请求体形状再由下面的 CustomProtocol 指定。
        public enum AiVendor { gpt, claude, kimi, qwen, deepseek, gemini, grok, glm, custom }

        // 接口格式（只在厂商选「自定义」时出现）。三种覆盖了绝大多数兼容网关：
        // openai = POST {base}/chat/completions + Bearer；anthropic = POST {base}/messages + x-api-key + 版本头；
        // gemini = POST {base}/models/{model}:generateContent?key=…（模型名在 URL 里而不在请求体里）。
        public enum AiProtocol { openai, anthropic, gemini }

        // 思考强度：第九轮按各家真实支持的档位集合铺，一种档位集合一个枚举类型（集合相同的行共用一个类型）。
        // 为什么要拆成七套：下拉框能选哪几档 = 这个属性的【枚举类型】里有哪些成员，而框架在建页时就把选项抓走了，
        // 拿不到「玩家当前选的是哪家」，所以只能给每一行配一套写死的子集（与 UrlX/KeyX 那批同一性质）。
        // ⚠ 成员名必须与 EngineKit.CustomAi.ThinkingLevels 里那一串 ~ 分隔的词逐字相同：两边靠【名字】互转
        // （存盘词 = lv.ToString()，读回 = CustomAi.ParseLevel），数值一律不参与。
        // ⚠ 成员从 0 连续编号，不给数值留空洞。
        public enum AiThinkOpenAi { Off, Minimal, Low, Medium, High, Xhigh, Max }    // 全七档：OpenAI / GLM / Groq / OpenRouter
        public enum AiThinkAnthropic { Off, Low, Medium, High, Xhigh, Max }         // Claude：effort 这一族没有 minimal
        public enum AiThinkGemini { Off, Minimal, Low, Medium, High }               // Gemini 2.5 与 3.x 的并集，认不出的代际由 ThinkingField 挤回
        public enum AiThinkGrok { Off, Low, Medium, High, Xhigh }                   // Grok：无 minimal、无 max
        public enum AiThinkKimi { Off, Low, High, Max }                             // Kimi / DeepSeek：官方都没有 medium
        public enum AiThinkPlain { Off, Low, Medium, High }                        // 「自定义」端点：对面是谁只有玩家知道，只给最通用那四档
        public enum AiThinkSilicon { Off, High, Max }                              // 硅基流动：平台的 effort 只收 high / max

        private Engine _engine = Engine.Google;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        public Engine TranslationEngine
        {
            get => _engine;
            set
            {
                if (_engine == value) return;
                _engine = value;
                // 换引擎后上一条测试结果不再代表当前引擎，清掉免得误导（拿着微软的「成功」去用没填 key 的 Groq）。
                LastTestResult = NotTested;
                // 上一家引擎的模型名与已拉取的模型列表对新引擎都无意义 —— 留着的话下拉框会挂着别家的模型名，
                // 并且真的被发出去（必然 404）。在 setter 里清是安全的：自管 JSON 的载入顺序是
                // 先 TranslationEngine、后 AiModel（Mod.cs SettingsStore.Load），清掉的空值随后就被真实值覆盖。
                _aiModel = string.Empty;
                SetFetchedModels(null);
                // 反馈1（本轮更正）：换引擎只【实时存盘】，下一句翻译才用新引擎。
                // 以前这里跟着范围开关走重刷一遍的路径，玩家只是换个引擎就被迫重译、等一次卡顿。
                NotifyEngine();
            }
        }

        public const string NotTested = "（尚未测试）";

        // 需求5：常驻的「说明：…」小字。鼠标移开设置项 tooltip 就没了，而 tooltip 框没有滚动条，
        // 长文案放不下 —— 所以额度/限流/补充说明这类信息不再塞进 desc.engine，改成常显在屏幕上。
        // 文字放在【标题】位置（getter 返回空串）：bare string 的正文位置在最右侧且很窄，标题才在左侧。
        //
        // 反馈6 修复：以前这里只有【一个】EngineNotes 属性，正文由 L10n.SettingsLocale 按当前选中引擎现算 ——
        // 但 ReadEntries 只在语言字典构建时跑一次，换引擎不会重建字典，于是这行永远显示第一次算出来的那句
        // （点「清除缓存」会触发 ReloadActiveLocale 重建字典，说明才「恢复正常」，与用户报的现象完全一致）。
        // 改成 13 个引擎各占一行、标签在字典里写死、用 hide 条件切换显隐：hide 条件在每次 RefreshPage 时
        // 重新求值（各家 key 输入框就是这么做的，已实测有效），静态标签则不可能过时。
        private bool HideNotes(Engine e) => TranslationEngine != e;

        private bool HideNotesGoogle() => HideNotes(Engine.Google);
        private bool HideNotesDuckDuckGo() => HideNotes(Engine.DuckDuckGo);
        private bool HideNotesMyMemory() => HideNotes(Engine.MyMemory);
        private bool HideNotesYandex() => HideNotes(Engine.Yandex);
        private bool HideNotesMicrosoft() => HideNotes(Engine.Microsoft);
        private bool HideNotesDeepL() => HideNotes(Engine.DeepL);
        private bool HideNotesBaidu() => HideNotes(Engine.Baidu);
        private bool HideNotesGemini() => HideNotes(Engine.Gemini);
        private bool HideNotesGroq() => HideNotes(Engine.Groq);
        private bool HideNotesOpenRouter() => HideNotes(Engine.OpenRouter);
        private bool HideNotesSiliconFlow() => HideNotes(Engine.SiliconFlow);
        private bool HideNotesCloudflare() => HideNotes(Engine.Cloudflare);
        private bool HideNotesCustom() => HideNotes(Engine.Custom);

        // 13 行说明的声明顺序 = 引擎下拉框顺序，所以界面上这行永远紧跟在下拉框下面那一片里。
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesGoogle))]
        public string NotesGoogle => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesDuckDuckGo))]
        public string NotesDuckDuckGo => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesMyMemory))]
        public string NotesMyMemory => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesYandex))]
        public string NotesYandex => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesMicrosoft))]
        public string NotesMicrosoft => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesDeepL))]
        public string NotesDeepL => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesBaidu))]
        public string NotesBaidu => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesGemini))]
        public string NotesGemini => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesGroq))]
        public string NotesGroq => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesOpenRouter))]
        public string NotesOpenRouter => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesSiliconFlow))]
        public string NotesSiliconFlow => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesCloudflare))]
        public string NotesCloudflare => string.Empty;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideNotesCustom))]
        public string NotesCustom => string.Empty;

        // 「跳转注册页面」（需求2/5）：紧跟说明行，用系统默认浏览器打开该引擎的注册/申请 key 页面。
        public static event Action OnOpenRegisterPageClicked;

        [SettingsUIButton]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideOpenRegisterPage))]
        public bool OpenRegisterPage { set { OnOpenRegisterPageClicked?.Invoke(); } }

        // ---- 条件显隐（需求2：选哪个引擎才显示哪个引擎的输入框）----
        // SettingsUIHideByCondition 的谓词【返回 true = 隐藏】。真值表在 EngineKit.ShowField / ShowRegister 里，
        // 这里只负责「当前选中的是不是这个引擎」——规则集中在纯函数层，离线壳能断言，改一处全局生效。
        private bool Hide(Engine e, string field) => TranslationEngine != e || !EngineKit.ShowField(e.ToString(), field);

        private bool HideMyMemoryEmail() => Hide(Engine.MyMemory, "email");
        private bool HideYandexKey() => Hide(Engine.Yandex, "key");
        private bool HideMicrosoftKey() => Hide(Engine.Microsoft, "key");
        private bool HideMicrosoftRegion() => Hide(Engine.Microsoft, "key");
        private bool HideDeepLKey() => Hide(Engine.DeepL, "key");
        private bool HideBaiduAppId() => Hide(Engine.Baidu, "second");
        private bool HideBaiduKey() => Hide(Engine.Baidu, "key");
        private bool HideGeminiKey() => Hide(Engine.Gemini, "key");
        private bool HideGroqKey() => Hide(Engine.Groq, "key");
        private bool HideOpenRouterKey() => Hide(Engine.OpenRouter, "key");
        private bool HideSiliconFlowKey() => Hide(Engine.SiliconFlow, "key");
        private bool HideCloudflareAccount() => Hide(Engine.Cloudflare, "account");
        private bool HideCloudflareToken() => Hide(Engine.Cloudflare, "key");
        private bool HideCustomVendor() => Hide(Engine.Custom, "vendor");

        // 反馈3：AI 厂商的接口地址 / key / token 用量每家一行，只有【当前选中的那一家】可见。
        // 下标顺序同 CustomAi.Vendors（与 AiVendor 枚举同序）。
        private bool HideVendorSlot(int i) => TranslationEngine != Engine.Custom || VendorSlot != i;
        private bool HideVendorGpt() => HideVendorSlot(0);
        private bool HideVendorClaude() => HideVendorSlot(1);
        private bool HideVendorKimi() => HideVendorSlot(2);
        private bool HideVendorQwen() => HideVendorSlot(3);
        private bool HideVendorDeepseek() => HideVendorSlot(4);
        private bool HideVendorGemini() => HideVendorSlot(5);
        private bool HideVendorGrok() => HideVendorSlot(6);
        private bool HideVendorGlm() => HideVendorSlot(7);
        private bool HideVendorCustom() => HideVendorSlot(8);

        // 反馈8：厂商选「自定义」时多出三行（接口格式 / 模型名称 / 思考强度），同时模型【下拉框】要让位给文本框 ——
        // 自填接口的模型名只有玩家自己知道，而下拉框只能从「获取模型列表」的结果里挑，自定义地址未必提供 /models，
        // 给一个点不动的下拉框不如给一个能打的输入框（下拉框点不动这个坑在需求9 已经踩过一次）。
        private bool IsFreeformCustom => TranslationEngine == Engine.Custom && CustomVendor == AiVendor.custom;

        private bool HideCustomProtocol() => !IsFreeformCustom;
        private bool HideCustomModelName() => !IsFreeformCustom;
        // 反馈4：思考强度不再只属于「自定义」那一家 —— 大模型引擎都有这个概念，走 ShowField 那张表统一判。
        // 第八轮：思考强度拆成每档引擎、每家厂商各一行。自定义 AI 那九行沿用上面的 HideVendorSlot 谓词
        // （它已经含了「引擎必须是 Custom」），这里只补四档直连大模型引擎的。
        private bool HideThinkingGemini() => Hide(Engine.Gemini, "thinking");
        private bool HideThinkingGroq() => Hide(Engine.Groq, "thinking");
        private bool HideThinkingOpenRouter() => Hide(Engine.OpenRouter, "thinking");
        private bool HideThinkingSiliconFlow() => Hide(Engine.SiliconFlow, "thinking");

        // 模型下拉框 + 「获取模型列表」按钮是四个 AI 引擎共用的（自定义 AI / Groq / OpenRouter / SiliconFlow），
        // 所以谓词不再绑单个引擎，直接问 EngineKit「当前选中的引擎要不要 model 字段」。
        private bool HideAiModel() => IsFreeformCustom || !EngineKit.ShowField(TranslationEngine.ToString(), "model");
        private bool HideFetchModels() => HideAiModel();

        // 「跳转注册页面」按钮的显隐与目标网址都由 EngineKit 给：免注册的四家与自定义 AI 都不出这个按钮。
        private bool HideOpenRegisterPage() => !EngineKit.ShowRegister(TranslationEngine.ToString());

        // ---- 各引擎凭据（按下拉框顺序声明，界面顺序即声明顺序）----
        // MyMemory 的邮箱是【选填】：不填 5000 字符/天，填了 50000 字符/天。
        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideMyMemoryEmail))]
        public string MyMemoryEmail { get; set; } = string.Empty;

        // Yandex 免 key 走网页通道即可用；填了 IAM token 改走官方 API（更稳，不受网页会话失效影响）。同样选填。
        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideYandexKey))]
        public string YandexKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideMicrosoftKey))]
        public string MicrosoftKey { get; set; } = string.Empty;

        // 部分 Azure 翻译资源（区域/多服务资源）必须带 Ocp-Apim-Subscription-Region 头，否则 key 正确也 401。
        // 全球（Global）资源不需要，不确定就留空 —— 所以它是选填，标签不带 *。
        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideMicrosoftRegion))]
        public string MicrosoftRegion { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideDeepLKey))]
        public string DeepLKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideBaiduAppId))]
        public string BaiduAppId { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideBaiduKey))]
        public string BaiduKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideGeminiKey))]
        public string GeminiKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideGroqKey))]
        public string GroqKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideOpenRouterKey))]
        public string OpenRouterKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideSiliconFlowKey))]
        public string SiliconFlowKey { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideCloudflareAccount))]
        public string CloudflareAccountId { get; set; } = string.Empty;

        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideCloudflareToken))]
        public string CloudflareToken { get; set; } = string.Empty;

        // ---- 自定义 AI 模型（需求2：厂商 / 中转地址 / 获取模型列表 / 模型下拉框）----
        private AiVendor _vendor = AiVendor.gpt;

        // 反馈3：换厂商只影响【之后】发往 AI 接口的请求，不该触发「清空译文重翻」。所以单开一条轻量事件：
        // Mod 端收到后只做「静默存盘 + 重刷当前选项页」，让新厂商那三行（接口地址/key/已用 token）当场换出来。
        public static event Action OnVendorChanged;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideCustomVendor))]
        public AiVendor CustomVendor
        {
            get => _vendor;
            set
            {
                if (_vendor == value) return;
                _vendor = value;
                SetFetchedModels(null);   // 旧厂商拉来的模型列表对新厂商无意义，清掉免得下拉框里全是错的名字
                _aiModel = string.Empty;  // 默认模型名也跟着厂商变，旧选择同样作废
                if (Instance != null) { try { OnVendorChanged?.Invoke(); } catch { } }
            }
        }

        // 反馈8：接口格式。只在厂商选「自定义」时出现，决定请求体形状与鉴权头（真值表在 EngineKit.CustomAi）。
        private AiProtocol _protocol = AiProtocol.openai;

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideCustomProtocol))]
        public AiProtocol CustomProtocol
        {
            get => _protocol;
            set => _protocol = value;
        }

        // 8 家内置厂商的接口地址：【选填】，走代理或第三方中转站时改成自己的地址，留空用该厂商官方地址。
        // 厂商选「自定义」：【必填】—— 没有官方地址可兜底，留空就等于没告诉模组该往哪发（EngineKit.MissingField 会拦下）。
        //
        // 反馈3：这两格以前是【9 个厂商共用一个输入框】，切到第二家看到的还是第一家的内容，
        // 发出去就是拿 A 家的 key 去请求 B 家的端点（实测日志里出现过「用第三方 sk- 打 Google 接口拿 401」）。
        //
        // 为什么写成 9 行，而不是「一行读写当前格」：输入框里的文字是【建页时】从属性取一次就交给控件的，
        // RefreshPage() 只重跑显隐/标题/警告，不会回头重取控件的值（模型下拉框的 items 同理，见 Mod.PushModelItems）。
        // 一行式写法在切厂商后框子里仍显示上一家的内容，玩家随手一改就把 A 家的字符串写进 B 家的格子 ——
        // 存得对、看得见错，比原来更难解释。每家一行之后，每行的控件值天然等于自己那一格，串不了。
        // （与 13 条引擎说明行同一个套路：行都在，靠显隐只留一行。）下标顺序同 CustomAi.Vendors。
        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGpt))]
        public string UrlGpt { get => Slot(VendorBaseUrl, 0); set => SetSlot(VendorBaseUrl, 0, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGpt))]
        public string KeyGpt { get => Slot(VendorKey, 0); set => SetKeySlot(0, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorClaude))]
        public string UrlClaude { get => Slot(VendorBaseUrl, 1); set => SetSlot(VendorBaseUrl, 1, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorClaude))]
        public string KeyClaude { get => Slot(VendorKey, 1); set => SetKeySlot(1, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorKimi))]
        public string UrlKimi { get => Slot(VendorBaseUrl, 2); set => SetSlot(VendorBaseUrl, 2, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorKimi))]
        public string KeyKimi { get => Slot(VendorKey, 2); set => SetKeySlot(2, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorQwen))]
        public string UrlQwen { get => Slot(VendorBaseUrl, 3); set => SetSlot(VendorBaseUrl, 3, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorQwen))]
        public string KeyQwen { get => Slot(VendorKey, 3); set => SetKeySlot(3, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorDeepseek))]
        public string UrlDeepseek { get => Slot(VendorBaseUrl, 4); set => SetSlot(VendorBaseUrl, 4, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorDeepseek))]
        public string KeyDeepseek { get => Slot(VendorKey, 4); set => SetKeySlot(4, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGemini))]
        public string UrlGemini { get => Slot(VendorBaseUrl, 5); set => SetSlot(VendorBaseUrl, 5, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGemini))]
        public string KeyGemini { get => Slot(VendorKey, 5); set => SetKeySlot(5, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGrok))]
        public string UrlGrok { get => Slot(VendorBaseUrl, 6); set => SetSlot(VendorBaseUrl, 6, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGrok))]
        public string KeyGrok { get => Slot(VendorKey, 6); set => SetKeySlot(6, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGlm))]
        public string UrlGlm { get => Slot(VendorBaseUrl, 7); set => SetSlot(VendorBaseUrl, 7, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGlm))]
        public string KeyGlm { get => Slot(VendorKey, 7); set => SetKeySlot(7, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorCustom))]
        public string UrlCustom { get => Slot(VendorBaseUrl, 8); set => SetSlot(VendorBaseUrl, 8, value); }

        [SettingsUITextInput, SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorCustom))]
        public string KeyCustom { get => Slot(VendorKey, 8); set => SetKeySlot(8, value); }

        private void SetKeySlot(int i, string value)
        {
            string old = Slot(VendorKey, i);
            string v = value ?? string.Empty;
            // 反馈3：换 key 就把这一家的已消耗 token 清零 —— 旧数字是新 key 花出来的，混在一起统计没有意义。
            if (old.Length > 0 && !string.Equals(old, v, StringComparison.Ordinal)) VendorTokens[i] = 0;
            SetSlot(VendorKey, i, v);
        }

        // 引擎与校验代码用的「当前选中厂商那一格」，纯代码入口，不进界面。
        [SettingsUIHidden]
        public string CustomBaseUrl { get => Slot(VendorBaseUrl, VendorSlot); set => SetSlot(VendorBaseUrl, VendorSlot, value); }

        [SettingsUIHidden]
        public string CustomKey { get => Slot(VendorKey, VendorSlot); set => SetKeySlot(VendorSlot, value); }

        // ===== 反馈3：按厂商分格的凭证与用量（下标同 CustomAi.Vendors）=====
        // private：界面只经上面那些属性读写，落盘只经 Get/PutVendor*；框架扫描设置项时也不会把它们当选项。
        private readonly string[] VendorBaseUrl = new string[CustomAi.Vendors.Length];
        private readonly string[] VendorKey = new string[CustomAi.Vendors.Length];
        private readonly long[] VendorTokens = new long[CustomAi.Vendors.Length];

        // 当前选中厂商在下表里的位置；认不出来（游戏版本改了枚举）时回 0，最坏是存进第一格，不会越界。
        private int VendorSlot
        {
            get
            {
                string name = CustomVendor.ToString().ToLowerInvariant();
                for (int i = 0; i < CustomAi.Vendors.Length; i++) if (CustomAi.Vendors[i] == name) return i;
                return 0;
            }
        }

        private static string Slot(string[] arr, int i) => (arr != null && i >= 0 && i < arr.Length && arr[i] != null) ? arr[i] : string.Empty;

        private static void SetSlot(string[] arr, int i, string v)
        {
            if (arr != null && i >= 0 && i < arr.Length) arr[i] = v ?? string.Empty;
        }

        // 读盘用：把某厂商那一格写进去（不经过属性，免得触发「换 key 就清零 token」的副作用）。
        public void PutVendorBaseUrl(string vendor, string v) { int i = IndexOf(vendor); if (i >= 0) VendorBaseUrl[i] = v ?? string.Empty; }
        public void PutVendorKey(string vendor, string v) { int i = IndexOf(vendor); if (i >= 0) VendorKey[i] = v ?? string.Empty; }
        public void PutVendorTokens(string vendor, long n) { int i = IndexOf(vendor); if (i >= 0) VendorTokens[i] = n < 0 ? 0 : n; }
        public string GetVendorBaseUrl(string vendor) { int i = IndexOf(vendor); return i < 0 ? string.Empty : Slot(VendorBaseUrl, i); }
        public string GetVendorKey(string vendor) { int i = IndexOf(vendor); return i < 0 ? string.Empty : Slot(VendorKey, i); }
        public long GetVendorTokens(string vendor) { int i = IndexOf(vendor); return i < 0 ? 0 : VendorTokens[i]; }

        private static int IndexOf(string vendor)
        {
            string name = CustomAi.Normalize(vendor);
            for (int i = 0; i < CustomAi.Vendors.Length; i++) if (CustomAi.Vendors[i] == name) return i;
            return -1;
        }

        public void ClearVendorCredentials()
        {
            for (int i = 0; i < VendorTokens.Length; i++)
            {
                VendorBaseUrl[i] = string.Empty;
                VendorKey[i] = string.Empty;
                VendorTokens[i] = 0;
            }
        }

        // 存盘前清洗必须遍历 9 格：属性 CustomKey 只够得着界面当前那一格，而其余格子玩家一切厂商就会直接发出去。
        // key 里混进输入法带来的不可见字符时肉眼看不出差别，但校验永远失败。
        public void CleanVendorCredentials()
        {
            for (int i = 0; i < VendorKey.Length; i++)
            {
                VendorBaseUrl[i] = Mod.CleanKey(Slot(VendorBaseUrl, i));
                VendorKey[i] = Mod.CleanKey(Slot(VendorKey, i));
            }
        }

        // 反馈8：模型名称。厂商选「自定义」时用它代替模型下拉框 —— 自填接口的模型名只有玩家知道，
        // 而自定义地址未必提供 /models，下拉框会变成点不动的死控件。8 家内置厂商仍走 AiModel 下拉框。
        [SettingsUITextInput]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideCustomModelName))]
        public string CustomModelName { get; set; } = string.Empty;

        // 反馈8 / 反馈4：思考强度这一行已挪到共用「模型」下拉框的正下方（声明顺序 = 界面顺序），
        // 现在它对自定义 + Gemini + 三家托管 AI 都可见，字段 _thinking 还留在这里。

        // 「获取模型列表」按钮。需求2 要求它在下拉框【左边】，但框架一行只放一个控件，
        // 所以退而求其次放在紧邻的上一行（声明顺序 = 界面顺序）。点击后由 Mod 端后台线程拉列表，
        // 拉完刷新选项界面，结果进 FetchedModels 供下面的下拉框读取。
        public static event Action OnFetchModelsClicked;

        [SettingsUIButton]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideFetchModels))]
        public bool FetchModels { set { OnFetchModelsClicked?.Invoke(); } }

        public static string FetchModelsResult = string.Empty;   // 后台线程写，界面读（拉取进度/失败原因）

        // 拉取结果单独一行显示（bare string）：按钮点下去是后台跑的，玩家需要看到「取到 N 个」或失败原因，
        // 否则点了没反应跟坏了没区别。显隐跟着按钮走。
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideFetchModels))]
        public string FetchModelsStatus => FetchModelsResult;

        // 后台线程写、UI 线程读，所以要锁。清空的时机：换厂商（列表不再适用）。
        private static readonly List<string> FetchedModels = new List<string>();

        public static void SetFetchedModels(IEnumerable<string> models)
        {
            lock (FetchedModels)
            {
                FetchedModels.Clear();
                if (models != null) foreach (string m in models) if (!string.IsNullOrEmpty(m)) FetchedModels.Add(m);
            }
        }

        // 反馈2/3：硬编码的默认模型名会过期。实测 OpenRouter 的 meta-llama/llama-3.3-70b-instruct:free
        // 已经 404、硅基流动的 Qwen/Qwen2.5-7B-Instruct 回 402 Payment Required —— 两家都表现为
        // 「列表能拉到、翻译全失败」。列表刚到货是纠正的唯一时机：当前值不在列表里（包括从没选过、
        // getter 回落到默认名的情况）就改用列表第一项，列表已按免费优先排过，所以第一项就是最稳的那一个。
        // 返回被采用的模型名；不需要改时返回空串。
        public string AdoptFetchedModelIfStale()
        {
            if (HideAiModel()) return string.Empty;                      // 这一档引擎不显示模型行（Gemini 固定用别名）
            string current = AiModel;
            string first;
            bool known;
            lock (FetchedModels)
            {
                first = FetchedModels.Count > 0 ? FetchedModels[0] : null;
                known = FetchedModels.Contains(current);
            }
            if (string.IsNullOrEmpty(first) || known) return string.Empty;
            _aiModel = first;
            return first;
        }

        // 模型下拉框：四个 AI 引擎共用（自定义 AI + Groq / OpenRouter / SiliconFlow）。
        // 需求1 里 OpenRouter / SiliconFlow 写的是「可选永久免费模型」—— 平台的免费模型名会变，
        // 所以给玩家一个能改的下拉框，比写死一个模型名靠谱。Gemini 不共用：它固定吃 gemini-flash-latest 别名。
        //
        // 需求9 修复：这个值以前是自动属性、默认空串，而空串【从来不在】GetModelItems() 的返回值里 ——
        // 框架拿当前值去列表里找下标，找不到就把整个下拉框渲染成灰色不可点。玩家存盘里的 AiModel 实测就是 ""，
        // 所以 OpenRouter 明明取回了 428 个模型，下拉框照样点不动（硅基流动、自定义 AI 同理）。
        // 现在 getter 在「没选过」时直接回当前引擎的默认模型名，保证当前值一定在列表里。
        // 反馈7 复查：那只是一半原因。另一半是【候选项在建页那一刻就被框架抓走】存进 DropdownField<string>.items，
        // 而 RefreshPage() 只重跑显隐/标题/警告，从不重取 items —— 所以点完「获取模型列表」界面纹丝不动。
        // 那一半由 Mod.Patches.PushModelItems() 用反射把新列表塞回控件并抬高它的版本号来治，两边都做了才真的能选。
        private string _aiModel = string.Empty;

        [SettingsUIDropdown(typeof(TranslatorSetting), nameof(GetModelItems))]
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideAiModel))]
        public string AiModel
        {
            get => !string.IsNullOrEmpty(_aiModel) ? _aiModel : DefaultModel;
            set => _aiModel = value ?? string.Empty;
        }

        // 反馈4：这一行从「只有自定义厂商可见」放开给所有大模型引擎（Gemini / Groq / OpenRouter / 硅基流动 / 自定义）。
        // 第八轮：改成【每档引擎、每家厂商各一格】。以前九家加四档引擎共用一格，给 GPT 选了「高」，切到 Claude
        // 界面还停在「高」、发往 Claude 的请求也带着「高」—— 与反馈3 那次「9 家共用一个 key 框」同一类毛病。
        // 为什么是 12 行而不是「一行读写当前格」：见上面 UrlX/KeyX 那段 —— 控件的值是【建页时】抓走的，
        // RefreshPage() 只重跑显隐，不会回头重取，一行式写法切完厂商显示的还是上一家的档位。
        // 格位编号的唯一真值在 EngineKit.ThinkingSlot（t3 的 Q 段把它钉死了）；存盘键带引擎名/厂商名。
        // 第九轮：Off 的含义从「一个字段都不发」改成「按这一家的口径显式关掉」——逐家核实过，不发字段在 13 格里
        // 只有 2 格真等于不思考，其余是「按那一家的默认档继续思考继续扣费」。该发什么由 CustomAi.ThinkingField 决定。
        private readonly CustomAi.ThinkLevel[] Thinking = new CustomAi.ThinkLevel[EngineKit.ThinkingSlots];

        private CustomAi.ThinkLevel ThinkingAt(int slot) => (slot >= 0 && slot < Thinking.Length) ? Thinking[slot] : CustomAi.ThinkLevel.Off;

        private void SetThinkingAt(int slot, CustomAi.ThinkLevel value)
        {
            if (slot >= 0 && slot < Thinking.Length) Thinking[slot] = value;
        }

        // 某档引擎（vendor 只对「自定义 AI」有意义）自己存的那一档。没有这一行的引擎 → 格位 -1 → 回 Off。
        // internal 而不是 public：CustomAi 是 internal 类，公有成员拿它当返回值/参数会撞 CS0050；调用方全在本 assembly 内。
        internal CustomAi.ThinkLevel GetThinking(string engineName, string vendor) => ThinkingAt(EngineKit.ThinkingSlot(engineName, vendor));

        // 读盘用：绕开界面属性直接写进指定格（界面那一行只能写当前显示的那一格，读盘要一次灌满 22 格）。
        internal void PutThinking(string engineName, string vendor, CustomAi.ThinkLevel value)
        {
            SetThinkingAt(EngineKit.ThinkingSlot(engineName, vendor), value);
        }

        // 规范档位 ↔ 界面上那一行的枚举。两边成员名逐字相同，所以只按名字转，数值不参与
        // （AiThinkKimi.Low 是 1、CustomAi.ThinkLevel.Low 是 2，按数值硬转会错位）。
        // 名字对不上只可能是「这一行没有这一档」→ 回 Off，与读盘认不出脏值的处理一致。
        private static T ThinkRow<T>(CustomAi.ThinkLevel lv) where T : struct
        {
            T v;
            return Enum.TryParse(lv.ToString(), out v) ? v : default(T);
        }

        private static CustomAi.ThinkLevel ThinkLevelOf(object row) => CustomAi.ParseLevel(Convert.ToString(row));

        // 「恢复默认」用：一次清全部格位，与 ClearVendorCredentials 同理（只清当前格会让切到别家时仍看到旧档位）。
        public void ClearThinking()
        {
            for (int i = 0; i < Thinking.Length; i++) Thinking[i] = CustomAi.ThinkLevel.Off;
        }

        // ---- 四档直连大模型引擎各一行（自定义 AI 那八行用上面现成的 HideVendor* 谓词）----
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideThinkingGemini))]
        public AiThinkGemini ThinkEngineGemini { get => ThinkRow<AiThinkGemini>(GetThinking(nameof(Engine.Gemini), null)); set => PutThinking(nameof(Engine.Gemini), null, ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideThinkingGroq))]
        public AiThinkOpenAi ThinkEngineGroq { get => ThinkRow<AiThinkOpenAi>(GetThinking(nameof(Engine.Groq), null)); set => PutThinking(nameof(Engine.Groq), null, ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideThinkingOpenRouter))]
        public AiThinkOpenAi ThinkEngineOpenRouter { get => ThinkRow<AiThinkOpenAi>(GetThinking(nameof(Engine.OpenRouter), null)); set => PutThinking(nameof(Engine.OpenRouter), null, ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideThinkingSiliconFlow))]
        public AiThinkSilicon ThinkEngineSiliconFlow { get => ThinkRow<AiThinkSilicon>(GetThinking(nameof(Engine.SiliconFlow), null)); set => PutThinking(nameof(Engine.SiliconFlow), null, ThinkLevelOf(value)); }

        // ---- 自定义 AI：八家厂商各一行。厂商下拉框切到哪一家，露出来的就是哪一行的那一格。----
        // 千问没有这一行：阿里云原文要求非流式调用必须 enable_thinking=false，这一格没有任何可调档位，
        // 所以格位也不留（EngineKit.ThinkingSlot 对空档位集合回 -1），发请求时由 CustomAi.QwenField 恒定关思考。
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGpt))]
        public AiThinkOpenAi ThinkGpt { get => ThinkRow<AiThinkOpenAi>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.gpt))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.gpt), ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorClaude))]
        public AiThinkAnthropic ThinkClaude { get => ThinkRow<AiThinkAnthropic>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.claude))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.claude), ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorKimi))]
        public AiThinkKimi ThinkKimi { get => ThinkRow<AiThinkKimi>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.kimi))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.kimi), ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorDeepseek))]
        public AiThinkKimi ThinkDeepseek { get => ThinkRow<AiThinkKimi>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.deepseek))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.deepseek), ThinkLevelOf(value)); }

        // 厂商 gemini 与上面引擎 Gemini【不是同一格】：一个走玩家自备的接口地址，一个走官方端点。
        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGemini))]
        public AiThinkGemini ThinkVendorGemini { get => ThinkRow<AiThinkGemini>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.gemini))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.gemini), ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGrok))]
        public AiThinkGrok ThinkGrok { get => ThinkRow<AiThinkGrok>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.grok))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.grok), ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGlm))]
        public AiThinkOpenAi ThinkGlm { get => ThinkRow<AiThinkOpenAi>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.glm))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.glm), ThinkLevelOf(value)); }

        [SettingsUISection(kTabEngine, kGroupEngine)]
        [SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorCustom))]
        public AiThinkPlain ThinkVendorCustom { get => ThinkRow<AiThinkPlain>(GetThinking(nameof(Engine.Custom), nameof(AiVendor.custom))); set => PutThinking(nameof(Engine.Custom), nameof(AiVendor.custom), ThinkLevelOf(value)); }

        // 当前引擎/厂商的默认模型名。三处共用（下拉框首项、AiModel 兜底、EffectiveModel），
        // 以前是三份一样的三元表达式抄在一起，改一处漏两处。
        [SettingsUIHidden]
        public string DefaultModel =>
            TranslationEngine == Engine.Custom ? CustomAi.DefaultModel(CustomVendor.ToString())
            : EngineKit.HostedModel(TranslationEngine.ToString()) ?? string.Empty;

        // 下拉项 = 当前引擎的默认模型 + 已拉取的列表 + 玩家当前选中的值。
        // 第三项不能省：框架只会把「在列表里的值」显示出来，没拉列表时玩家已有的选择会凭空消失。
        // 显示名给免费档加「（免费）」后缀（需求9：玩家看不出哪个模型免费），value 仍是原始模型名，发请求时用 value。
        public DropdownItem<string>[] GetModelItems()
        {
            var items = new List<DropdownItem<string>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string engine = TranslationEngine.ToString();
            string def = DefaultModel;
            if (!string.IsNullOrEmpty(def) && seen.Add(def)) items.Add(Item(engine, def));
            lock (FetchedModels)
            {
                foreach (string m in FetchedModels)
                    if (!string.IsNullOrEmpty(m) && seen.Add(m)) items.Add(Item(engine, m));
            }
            string cur = AiModel;
            if (!string.IsNullOrEmpty(cur) && seen.Add(cur)) items.Add(Item(engine, cur));
            return items.ToArray();
        }

        private static DropdownItem<string> Item(string engine, string model)
        {
            return new DropdownItem<string>
            {
                value = model,
                displayName = EngineKit.IsFreeModel(engine, model) ? model + "（免费）" : model,
            };
        }

        // 引擎实际用的模型名。AiModel 的 getter 已经兜过默认值，这里直接读。
        // 反馈8：厂商选「自定义」时模型名来自手填的文本框（AiModel 那一行是隐藏的），走 CustomModelName。
        // [SettingsUIHidden]：纯代码用的属性，不加它框架会当选项渲染进默认标签页（v0.31 那个空白页的来源）。
        [SettingsUIHidden]
        public string EffectiveModel => IsFreeformCustom ? (CustomModelName ?? string.Empty).Trim() : AiModel;

        // ---- 测试板块（需求1：测试按钮与结果从「选择引擎」拆出来，单独一个板块）----
        // 测试按钮（需求 7/8）：只测引擎服务连通性，按【目标语言】翻译探针并把结果回显（不再永远显示中文）。
        public static event Action OnTestClicked;

        [SettingsUIButton]
        [SettingsUISection(kTabEngine, kGroupTest)]
        public bool TestTranslation { set { OnTestClicked?.Invoke(); } }

        public static string LastTestResult = NotTested;

        // 反馈4：值【不能想多长就多长】—— 一长框架就把左边这列标题挤成一列一字，整行难看。
        // 框架没有「给值两行」的口子（SettingsUITextInputAttribute 是无参数属性），所以在这里截断，
        // 完整结果由 Mod.RunTest 写进日志（日志路径就在同一个标签页下方）。上限留成常量，实机觉得宽/窄再调。
        private const int kTestResultMaxChars = 64;

        [SettingsUISection(kTabEngine, kGroupTest)]
        public string TestResult => Display.OneLine(LastTestResult, kTestResultMaxChars);

        // 已消耗 token 数（需求2）：平台报了 usage 就用真值，没报就本地估算，所以这个数只是量级参考。
        // 反馈3（本轮）：标题里那段「与实际消耗量可能会有出入」的括号解释按玩家要求删了，标题只剩 label.tokens 一句；
        // 数字挂在行的【值】上（见 VendorTokensText），标题保持静态。
        // 反馈3：以前 9 家厂商共用一个计数器，切到第二家看到的还是第一家的用量。现在每家一格（VendorTokens），
        // 界面显示【当前选中厂商】那一格；换 key 会把那一格清零（见 CustomKey 的 setter）。
        public static long CurrentVendorTokens
        {
            get
            {
                TranslatorSetting s = Instance;
                return s == null ? 0 : s.VendorTokens[s.VendorSlot];
            }
        }

        // 后台翻译线程累加（可能并发多条），所以走 Interlocked 而不是 ++。
        // 记在【实际发出这次请求的那一家】头上，不是界面上当前选中的那一家 —— 请求在飞的几百毫秒里玩家完全
        // 可能把厂商下拉框切走，把 A 家的花销记到 B 家账上比少记几个 token 更难查。
        public static void AddTokens(string vendor, long n)
        {
            if (n <= 0) return;
            TranslatorSetting s = Instance;
            if (s == null) return;
            int i = IndexOf(vendor);
            if (i < 0) i = s.VendorSlot;      // 认不出这个厂商名（老包或枚举改过）→ 兜到当前格，别把用量丢掉
            System.Threading.Interlocked.Add(ref s.VendorTokens[i], n);
        }

        // 反馈6：9 家合计的用量快照，只给 Mod 端那条「用量变了就静默落盘」的对账用，不进界面。
        // 为什么非得有这么一个东西：token 是后台线程悄悄往上加的，玩家不会为它再点一次「保存设置并翻译」，
        // 而存盘只有【点按钮 / 开关键 / 改实时项】三个触发口 —— 于是这一局后面烧掉的量全丢在内存里，
        // 重启后数字掉回上一次落盘那一刻，看起来就像「越用越少」。
        // 用合计而不是单格：换 key 会把某一格清零，合计可能【变小】，所以比对条件是「不等于」而非「大于」。
        public long TrackedTokens
        {
            get
            {
                long sum = 0;
                for (int i = 0; i < VendorTokens.Length; i++)
                    sum += System.Threading.Interlocked.Read(ref VendorTokens[i]);
                return sum;
            }
        }

        // 每家厂商一行用量，只有当前厂商那行可见（跟接口地址/key 同一个套路）。
        // 反馈5：数字【必须挂在这一行的值上】。以前值恒为空串、数字由 SettingsLocale 字典写进行标题，
        // 而那张字典一整局只建一次（同一个坑见 L10n.cs 里反馈6 那段注释），于是玩家永远看到 0。
        // 行的值是每次界面刷新重新读 getter 的，所以数字会跟着涨；标题保持一句静态文案（label.tokens）。
        private string VendorTokensText(int slot)
        {
            long n = slot >= 0 && slot < VendorTokens.Length ? VendorTokens[slot] : 0;
            return n.ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
        }

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGpt))]
        public string TokGpt => VendorTokensText(0);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorClaude))]
        public string TokClaude => VendorTokensText(1);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorKimi))]
        public string TokKimi => VendorTokensText(2);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorQwen))]
        public string TokQwen => VendorTokensText(3);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorDeepseek))]
        public string TokDeepseek => VendorTokensText(4);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGemini))]
        public string TokGemini => VendorTokensText(5);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGrok))]
        public string TokGrok => VendorTokensText(6);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorGlm))]
        public string TokGlm => VendorTokensText(7);

        [SettingsUISection(kTabEngine, kGroupEngine), SettingsUIHideByCondition(typeof(TranslatorSetting), nameof(HideVendorCustom))]
        public string TokCustom => VendorTokensText(8);

        // [SettingsUIHidden]：静态单例句柄，纯代码用，不加它同样会被框架当选项塞进默认标签页。
        [SettingsUIHidden]
        public static TranslatorSetting Instance { get; private set; }

        // ===== 快捷键设置（需求6，反馈5 定稿，反馈1 修订）=====
        // 用游戏【内置】的按键绑定控件：玩家点中这一行、直接按下要用的键就设好了，不让他们输入字母。
        // ⚠ 上一轮的判断是错的：当时测到 RegisterKeyBindings 成功、GetAction 也拿到了 ProxyAction，
        // 但 WasPressedThisFrame() 永远 false，就断定这条链是死路。真相是 ModSetting 只管【注册】，
        // 注册出来的 action 并不会自动启用 —— ProxyAction.shouldBeEnabled 是 public 可写的，
        // 游戏给自己的输入上下文管这件事，模组的那一格没人管，必须模组自己置 true。补上这一步控件就正常工作。
        // 反馈1：默认【不给键】（这个只有一个参数的构造里 defaultKey 就是 BindingKeyboard.None，枚举 0 值）。
        // F9/F10 是上一版替玩家挑的，玩家的要求很明确：不需要的默认值就别塞，空着让他自己定。
        // 反馈1：绑定值必须由模组自己存进自管 JSON（下面两个字段）。框架那份按键存在 Game.Settings.KeybindingSettings
        // 里，而模组每次启动都会按 attribute 重新注册一遍 action —— 重注册就是把绑定打回默认，
        // 于是玩家看到的是「重启游戏快捷键直接没了」。所以现在：存 → Mod.CaptureBindings，用 → Mod.Arm 写回。
        internal const string kActionToggle = "BLGToggleTranslation";
        internal const string kActionRetranslate = "BLGRetranslate";

        // 上一局存下来的按键路径（"keyboard/f9" 这种）。空串 = 玩家没绑或主动清掉了。
        internal string SavedToggleKey = "";
        internal string SavedRetranslateKey = "";

        [SettingsUIKeyboardBinding(kActionToggle)]
        [SettingsUISection(kTabMisc, kGroupHotkeys)]
        public ProxyBinding ToggleTranslationBinding { get; set; }

        [SettingsUIKeyboardBinding(kActionRetranslate)]
        [SettingsUISection(kTabMisc, kGroupHotkeys)]
        public ProxyBinding RetranslateBinding { get; set; }

        // ===== 日志（需求 8/10）=====
        // 日志路径（需求8）：bare string getter → StringField，显示本模组专属日志文件路径（取代旧「上次保存结果」）。
        [SettingsUISection(kTabMisc, kGroupLog)]
        public string LogPath => ModLog.FilePath;

        // 打开日志文件夹（需求8）：框架无超链接控件，改用按钮，点击用系统文件管理器打开日志所在目录。
        public static event Action OnOpenLogFolderClicked;

        [SettingsUIButton]
        [SettingsUISection(kTabMisc, kGroupLog)]
        public bool OpenLogFolder { set { OnOpenLogFolderClicked?.Invoke(); } }

        // 清除缓存（需求10）：清空翻译缓存 + 运行时译文表，全部恢复原文；【不是】重置设置（选项保持不变）。
        public static event Action OnClearCacheClicked;

        [SettingsUIButton]
        [SettingsUISection(kTabMisc, kGroupLog)]
        public bool ClearCache { set { OnClearCacheClicked?.Invoke(); } }

        // ===== 声明（需求 9/11）=====
        // [SettingsUIMultilineText] 不渲染正文，故把【全文放进标题】——见 L10n 的 SettingsLocale：
        // 本属性的 label 被映射到 text.disclaimer 全文，于是标题即完整声明。getter 仍返回全文以兼容。
        [SettingsUIMultilineText]
        [SettingsUISection(kTabMisc, kGroupDisclaimer)]
        public string Disclaimer =>
            SelfL10n.T(GameManager.instance?.localizationManager?.activeLocaleId ?? "en-US", "text.disclaimer");

        public override void SetDefaults()
        {
            Enabled = false;
            // 默认引擎从微软改成 Google：免注册免 key 开箱可用，且 2026-09-08 用户实测 gtx 网页接口返回正常。
            TranslationEngine = Engine.Google;
            TargetLocale = "zh-HANS";
            // 反馈2：范围默认一个都不勾（声明处的初始值也是 false，这里再写一遍是给「恢复默认设置」用的）。
            ScopeModName = false;
            ScopeModOptions = false;
            ScopeAssetNames = false;
            ScopeAssetDescriptions = false;
            ScopeGameCore = false;
            ScopeWorldLabels = false;
            MyMemoryEmail = string.Empty;
            YandexKey = string.Empty;
            MicrosoftKey = string.Empty;
            MicrosoftRegion = string.Empty;
            DeepLKey = string.Empty;
            BaiduAppId = string.Empty;
            BaiduKey = string.Empty;
            GeminiKey = string.Empty;
            GroqKey = string.Empty;
            OpenRouterKey = string.Empty;
            SiliconFlowKey = string.Empty;
            CloudflareAccountId = string.Empty;
            CloudflareToken = string.Empty;
            CustomVendor = AiVendor.gpt;
            CustomProtocol = AiProtocol.openai;
            // 一次清全部格位：思考强度已经按引擎/厂商分格，只清当前格会让切到别家时仍看到旧档位。
            ClearThinking();
            // 一次清 9 家：CustomBaseUrl/CustomKey 是分格属性的「当前格」，只赋空串会让切到别家时仍看到旧凭据。
            ClearVendorCredentials();
            CustomModelName = string.Empty;
            AiModel = string.Empty;
        }

        // 取指定引擎的【主】凭证。免注册通道返回空串，由调用方按 IsConfigured 判定；
        // 需要第二样凭证的（百度 APP ID、Cloudflare Account ID）见 GetSecondFor。
        public string GetKeyFor(Engine e)
        {
            switch (e)
            {
                case Engine.MyMemory: return MyMemoryEmail;   // MyMemory 没有 key，邮箱只用来提额度
                case Engine.Yandex: return YandexKey;
                case Engine.Microsoft: return MicrosoftKey;
                case Engine.DeepL: return DeepLKey;
                case Engine.Baidu: return BaiduKey;
                case Engine.Gemini: return GeminiKey;
                case Engine.Groq: return GroqKey;
                case Engine.OpenRouter: return OpenRouterKey;
                case Engine.SiliconFlow: return SiliconFlowKey;
                case Engine.Cloudflare: return CloudflareToken;
                case Engine.Custom: return CustomKey;
                default: return string.Empty;                 // Google / DuckDuckGo 完全免凭证
            }
        }

        // 第二样凭证：只有百度和 Cloudflare 有。
        public string GetSecondFor(Engine e)
        {
            switch (e)
            {
                case Engine.Baidu: return BaiduAppId;
                case Engine.Cloudflare: return CloudflareAccountId;
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

            // 唯一存储：自管 JSON（写在官方约定的 ModsSettings\Cs2AutoTranslator.json）。
            // 不用框架的 .coc 通道，是因为玩家现有的设置都在这份 JSON 里，换存储会静默丢配置；
            // 且本模组的设置项带自定义事件回调（开关即时生效、key 需要 CleanKey 归一），自管更可控。
            // 类上去掉 [FileLocation] 后，本模组不再读写任何 .coc；但框架退出时仍会把它库里那条历史记录
            // 原样回写成 <UserData>\Cs2AutoTranslator.coc（2026-09-06 实测，内容停在旧快照）—— 见类头注释。
            SettingsStore.Load(s);

            // Instance 放到最后再赋值：后台线程一旦看到 Instance!=null，就能保证配置已加载完，不会读到空 key。
            Instance = s;

            // 若当前游戏语言不在官方 12 种硬编码表内，启动本模组界面文字的机翻兜底（需求 12）。
            // 放在 Instance 赋值之后：SelfL10n 的后台翻译需要读到已就绪的引擎/目标设置。
            SelfL10n.EnsureSelfTranslation(lm.activeLocaleId);
        }
    }
}
