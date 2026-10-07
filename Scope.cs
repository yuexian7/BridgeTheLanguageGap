using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;

namespace Cs2AutoTranslator
{
    // 翻译「范围」分类 + 本地预筛（需求 1/2/4/11）。
    //
    // 设计要点：
    //  - 分类只看本地化 key 的前缀（Group.SUBKEY[Identifier] 形式；v1.1.3 起也认模组自带的
    //    Group.SUBKEY:Identifier 冒号形式），纯字符串比较、零联网、极廉价。
    //    Prefix 那条热路径走 ClassifyMemo（key→类目 的小缓存），Classify 本体留给离线表与其它调用方。
    //  - ShouldSkipLocal 在入队前跑，命中即「无需翻译」：不联网、不显示「…」标记，直接放行原文。
    //    它覆盖九类：① 剥掉 {VALUE} 这类占位符后不含任何字母/表意字符（纯数字、纯符号、纯占位符）；
    //    ② 键位串（Ctrl+Enter、Mouse0，以及 W A S D 这种空格分隔的组合）；③ 书写系统已经是目标语言；
    //    ④ 「2x / 1080p / 3d」这类数字+极少字母的倍率度量串（翻了只会丢数字，v1.0.5 实机凭据）；
    //    ⑤ 控制路径（<Keyboard>/x）与文件路径（C:\…\.log、C:\…\Cs2AutoTranslator.json）——
    //       v1.0.5 需求2 放开"我们自己的文案在小语种目标下也送翻"之后，这类字符串第一次进入翻译链。
    //    ⑥ 整串就是一个专有名词（Discord / Ko-fi / Skyve / Move It 这类模组标题与平台名）——
    //       日志实证的错译：'Discord' -> '不和谐'、'Skyve' -> '滑块'、'Move It' -> '快点'。
    //    ⑦ 整串就是一个语言名（Français / 한국어 / Hrvatski / 简体中文）——「选择界面语言」那一栏，
    //       玩家点名不要翻（v1.1.2）。语言名一旦翻成本地写法，正在挑语言的人反而认不出母语那一行。
    //       实机凭据：id=Français → 上屏=英语（目标中文时整栏被改成中文国名，五六个选项挤成一样的词）。
    //    ⑧ 「数字+单位」的档位/时间值（5 min、300dpi、16 x 9、1080p、#FF8800、60 fps）——
    //       玩家点名"每个选项就几个字母+数字之类的也不需要"（v1.1.2）；这类串翻完只会把单位写坏。
    //    ⑨ 带连接号的语言码（en-US / zh-HANS / pt-BR）——那是给程序读的标识，不是给人读的短语。
    //       ⑦⑧⑨ 都只在「整串全是这一类」时生效，判据细节与反例（Use French layout / 5 minutes walk /
    //       3 players / OK 仍然要翻）见各自的实现处与 A8/A9 两张 fixture 表。
    //  - 玩家自定义/输入的文字（重命名道路·资产、模组参数文本框、任何输入框）天然没有 key、
    //    不进本地化字典，所以 Prefix 的 dict.TryGetValue 永远拿不到它们 → 结构性地永不被翻译（需求 11）。
    internal static class Scope
    {
        internal enum Category { ModName, ModOptions, AssetName, AssetDesc, WorldLabel, GameCore }

        // ===== key -> 类目 =====
        public static Category Classify(string key)
        {
            if (string.IsNullOrEmpty(key)) return Category.GameCore;

            // ⓪ 本模组自己的键 —— 归「模组选项及说明」，不留在「游戏本体」里。
            // 2026-09-29 实机凭据：玩家勾着「模组选项及说明」、没勾「游戏本体」，把目标语言切成土耳其语后
            // 反馈"其它模组都翻了，只有你自己没翻"。原因就在下一段那条 IsModIdentifier 判据上：
            // 已注册模组 id 表故意把自己过滤掉（否则本模组会翻自己），于是我们的键一路落到默认的 GameCore，
            // 而「游戏本体」的语义是"游戏自己的界面" —— 让我们自己的设置页被那个开关绑架，玩家不可能猜到。
            // 走 ModOptions 之后：想翻我们的设置页就勾「模组选项及说明」（他本来就勾着），关掉它我们的文字回原文。
            if (DropdownKit.IsSelfKey(key)) return Category.ModOptions;

            // 世界内标签（路名/高速/桥/坝/巷/区域/城市名）：归「游戏画面图层」实验性类目。
            // 这些在游戏世界里其实走 NameSystem 渲染（绕过 Translate），但 UI 面板里也可能经 Translate 请求到，
            // 故在此一并归类，只有勾选「游戏画面图层」才翻。
            if (StartsWithAny(key,
                    "Assets.STREET_NAME[", "Assets.HIGHWAY_NAME[", "Assets.BRIDGE_NAME[",
                    "Assets.DAM_NAME[", "Assets.ALLEY_NAME[", "Assets.DISTRICT_NAME[", "Assets.CITY_NAME["))
                return Category.WorldLabel;

            // 资产参数/属性名（Properties.*）：明确排除出「资产描述」，归游戏本体（需求 2）。
            if (key.StartsWith("Properties.", StringComparison.Ordinal)) return Category.GameCore;

            // 资产名称
            if (StartsWithAny(key, "Assets.NAME[", "Assets.UPGRADE_NAME[", "Services.NAME[", "SubServices.NAME["))
                return Category.AssetName;

            // 资产描述（标题下方的说明文字）
            if (StartsWithAny(key, "Assets.DESCRIPTION[", "Assets.UPGRADE_DESCRIPTION[",
                    "Assets.SUB_SERVICE_DESCRIPTION[", "Services.DESCRIPTION[", "SubServices.DESCRIPTION["))
                return Category.AssetDesc;

            // 模组在选项菜单里的分区标题 = 模组名称（实验性：模组列表里的真名走 Paradox 元数据，覆盖不到）。
            if (key.StartsWith("Options.SECTION[", StringComparison.Ordinal))
                return IsModIdentifier(key) ? Category.ModName : Category.GameCore;

            // 【冒号形式】的 Options.* key ⇒ 一定是模组自带的自定义文案（v1.1.3，玩家点名 Plop the Growables
            // 与 Real Weather Sync 的"设置项说明整页不翻"）。判据是现算的，不是印象：
            // 官方 en-US 语言包 22,163 条 key 里 Options.* 共 1,595 条，其中【含冒号的 0 条】
            // （脚本见交接文档 §12 的 state\official-locale\parsed\en-US.json），官方一律是 `Family[identifier]`。
            // 而 PlopTheGrowables 的 dll 里嵌的就是 `Options.OPTION:AllBuildingsHistorical` /
            // `Options.OPTION_DESCRIPTION:RemoveAllAbandonment` 这种冒号形状（13 条，含 GROUP/SECTION）。
            // 为什么不能只靠下面那条 IsModIdentifier：冒号形式没有方括号，ExtractIdentifier 取不出标识符，
            // 于是这些 key 从前一路掉到默认的 GameCore ⇒ 玩家没勾「游戏本体」时静默不翻，
            // 与他这次报的现象完全对上（同一页里【游戏自己建的】`Options.OPTION[<模组id>.分区.选项]` 反而翻了，
            // 因为那种 key 的标识符首段就是已注册模组 id）。
            if (key.StartsWith("Options.", StringComparison.Ordinal) && ColonBeforeAnyBracket(key))
                return Category.ModOptions;

            // 模组选项及说明（Options.* / Common.ACTION[...]，且 identifier 属于已注册模组）。
            if (key.StartsWith("Options.", StringComparison.Ordinal) || key.StartsWith("Common.ACTION[", StringComparison.Ordinal))
                return IsModIdentifier(key) ? Category.ModOptions : Category.GameCore;

            // 首字符是小写字母的 key ⇒ 【第三方模组自带的自定义文案】，归「模组选项及说明」。
            // 一手凭据（2026-09-29 现算，不靠印象）：官方 en-US 语言包 22,163 条 key 里
            // 「以小写字母开头」的有 0 条、「mode.access」这种纯小写点分的也有 0 条（脚本见交接文档 §12 的
            // state\official-locale\parsed\en-US.json），官方 key 一律是 Assets.* / Options.* / Menu.* 这类大写前缀。
            // 而 AccessAnarchy 这种自带 LocaleSource 的模组用的正是 enabled.label / mode.access / group.main
            // （它的 dll 里 30 个 key 全是这个形状）。从前这些 key 走默认分支归 GameCore ⇒ 玩家没勾「游戏本体」
            // 时它们静默不翻，实机表现为"这个模组的下拉框和标题死活不翻，隔壁模组却正常"。
            if (key[0] >= 'a' && key[0] <= 'z') return Category.ModOptions;

            // 其余全部：游戏本体。
            return Category.GameCore;
        }

        // ===== 下拉框候选项的类目：key 形状判不出来时【由页归属性决定】（v1.1.1）=====
        // 为什么 Classify 不够用：候选项这一格的 key 常常不是 key。模组把显示文字直接塞进
        // LocalizedString.id（LocalizedString.cs:124 的隐式转换），于是 Classify("Access zones only")
        // 既不含 Options.、首字母又不是小写 ⇒ 掉进默认的 GameCore，而玩家【没勾「游戏本体」】是常态
        // （2026-09-30 本机实测：ScopeGameCore=false、ScopeModOptions=true）。
        // 结果是 v1.1.1 放开"裸文字 id 也送翻"之后，这些项照样被范围闸拦成 范围外。
        // 判据用页：一页 = 一个选项页/标签页，官方页的 key 是裸词（General / Graphics / Interface，
        // 实机 55 个页里官方就这三个出现过；官方 Options.TAB 的六个标识符 Advanced/Gamepad/Keyboard/
        // Misc/Mouse/Simple 也全都不含点，现算于 state\official-locale\parsed\en-US.json），
        // 模组页的 key 是「模组名.分区.…」点分串（实机凭据：ToolModeMemory.ToolModeMemory.ToolModeMemoryMod、
        // Cs2AutoTranslator.Cs2AutoTranslator.Mod、MapExt2.MapExtPDX.Mod.EconomyEX）。⇒ 含点 = 模组的页。
        // 只把 GameCore 往上抬到 ModOptions：资产名/描述/画面图层/模组名这些类目语义不变，
        // 玩家关「模组选项及说明」时这一页照样得退回原文，行为与标题说明那条链一致。
        public static Category ClassifyItem(string key, bool onModPage)
        {
            // 空 key 怎么办（v1.1.3 改过判，理由写在这里）：
            // · 【候选项】那条链（DropdownKit.StepOnItem）里空 key 在 ResolveSource 的 NoId 出口就已经返回 null，
            //   永远走不到这道范围闸 ⇒ 这里给什么都无所谓，仍旧返回 GameCore（保守）。
            // · 【选项行标题/说明】这条新链（HandleRowText）就不一样了：模组经常直接给一句
            //   `LocalizedString.Value("Sync now")` —— 没有 key、只有裸文字，而这正是玩家 10-02 报的
            //   "这一页整页英文"的形状。那种行如果也算 GameCore，就被【没勾「游戏本体」】那道闸永久挡死。
            //   ⇒ 由页归属兜底：模组的页 ⇒ 模组选项及说明；官方的页 ⇒ 仍旧算游戏本体（不越玩家的开关）。
            //   还原语义不变：关掉「模组选项及说明」时这一行照样退回模组原文。
            Category c = string.IsNullOrEmpty(key) ? Category.GameCore : ClassifyMemo(key);
            return (onModPage && c == Category.GameCore) ? Category.ModOptions : c;
        }

        private static bool StartsWithAny(string s, params string[] prefixes)
        {
            for (int i = 0; i < prefixes.Length; i++)
                if (s.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // ===== 模组 identifier 判别 =====
        // 已注册模组 id 由宿主注入（Mod.cs 的 RegisteredModIds 负责反射 ModSetting.instances + 5 秒节流）。
        // 为什么做成委托而不是在这里自己反射：本文件与 TextKit.cs 要能被【零游戏 DLL 依赖】的离线测试壳
        // （tests\t3）单独编译，而 UnityEngine.Time.realtimeSinceStartup 那句在 try 之外，离线必抛 TypeLoadException。
        // 未注入（离线壳）→ 空数组 → Options.* 一律保守归 GameCore，与旧的「反射失败」退化行为完全一致。
        private static Func<string[]> _modIdsProvider;

        internal static void SetModIdProvider(Func<string[]> provider) { _modIdsProvider = provider; }

        // identifier 是否属于某个已注册模组（其首段命中 ModSetting.instances 的 key 前缀）。
        private static bool IsModIdentifier(string key)
        {
            string[] ids = GetModIds();
            if (ids.Length == 0) return false;
            string ident = ExtractIdentifier(key);
            if (string.IsNullOrEmpty(ident)) return false;
            for (int i = 0; i < ids.Length; i++)
                if (ident.StartsWith(ids[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // 取 key 里第一个 '[' 到最后一个 ']' 之间的 identifier。
        private static string ExtractIdentifier(string key)
        {
            int a = key.IndexOf('[');
            int b = key.LastIndexOf(']');
            if (a >= 0 && b > a) return key.Substring(a + 1, b - a - 1);
            // v1.1.3：冒号形式的模组 key（Options.OPTION:PlopTheGrowables.Foo）也取一次标识符，
            // 好让 IsModIdentifier 那套"首段是不是已注册模组 id"的判据对它同样生效。
            // 只在【冒号出现在任何方括号之前】时才算数，否则 `Options.OPTION[Foo:Bar]` 这种
            // 官方形状会被误当成冒号形式（官方 key 内部确实有含冒号的值，如带时间的格式串）。
            int c = key.IndexOf(':', 8);
            if (c > 0 && (a < 0 || c < a)) return key.Substring(c + 1);
            return null;
        }

        // 冒号形式判据（与 ExtractIdentifier 同一条口径，避免两处漂移）。
        private static bool ColonBeforeAnyBracket(string key)
        {
            int c = key.IndexOf(':', 8);
            if (c <= 0) return false;
            int a = key.IndexOf('[', 8);
            return a < 0 || c < a;
        }

        private static string[] GetModIds()
        {
            Func<string[]> provider = _modIdsProvider;
            if (provider == null) return Array.Empty<string>();
            return provider() ?? Array.Empty<string>();
        }

        // ===== key → 类目 的小缓存（v1.1.3 性能，对照 AccessAnarchy 那类"高频路径做与变化无关的重活"）=====
        // 为什么要有它：Prefix 是【每一次界面取词】都会跑的钩子（进一次选项页就是成百上千次），而 Classify
        // 里 IsModIdentifier 要遍历几十条模组 id、ExtractIdentifier 每条都 Substring 新建一个串。
        // 类目对同一个 key 是恒定的（只有模组 id 表变才可能变），所以这正是"最该缓存的那种重复计算"。
        // 失效：模组 id 表换了一份（新模组注册 / 反射刷新）⇒ 整表作废，否则"当时还没认出的模组"
        // 会被永久钉成游戏本体，玩家勾了开关也不翻 —— 那是比慢更糟的错。
        // 上限：key 空间 = 字典规模（官方 22,163 条 + 各模组自带），到 kMaxCachedKeys 就停止【写入】
        //（照常计算，只是不进表），不让它无界增长。
        private const int kMaxCachedKeys = 4096;
        private static readonly ConcurrentDictionary<string, Category> s_categoryCache =
            new ConcurrentDictionary<string, Category>();
        private static string[] s_categoryCacheIds;      // 这张缓存是用哪一份 id 表建起来的（按引用比）

        public static Category ClassifyMemo(string key)
        {
            if (string.IsNullOrEmpty(key)) return Category.GameCore;

            string[] ids = GetModIds();
            if (!ReferenceEquals(ids, s_categoryCacheIds))
            {
                s_categoryCache.Clear();
                s_categoryCacheIds = ids;
            }

            Category c;
            if (s_categoryCache.TryGetValue(key, out c)) return c;
            c = Classify(key);
            if (s_categoryCache.Count < kMaxCachedKeys) s_categoryCache.TryAdd(key, c);
            return c;
        }

        // 测试与"改设置后强制重判"用：清空类目缓存。
        public static void ForgetCategories() { s_categoryCache.Clear(); s_categoryCacheIds = null; }

        // ===== 本地预筛：是否「无需翻译」（需求 1 + 4）=====
        // 返回 true = 跳过翻译（ResolveAsSkip、不显示「…」、不联网）。极廉价，热路径可逐条调用。
        public static bool ShouldSkipLocal(string text, string targetLocale, string activeLocale)
        {
            if (string.IsNullOrEmpty(text)) return true;

            // 规则①：剥掉 {...} 占位符后不含任何字母/表意字符 → 跳过。
            // 例：15、+、100%、——、{VALUE}、{VALUE}{COUNT} 这些翻不出新信息（v0.21 事故串、以及纯占位符都走这里）。
            if (!HasTranslatableText(text)) return true;

            // 规则②：键位串（Ctrl+Enter / Shift + A / Mouse0 / F12 / Q+E / 单字母 / 空格分隔的 W A S D）→ 跳过。
            if (LooksLikeKeyBinding(text)) return true;

            // 规则③：文本「书写系统」与目标语言一致 → 已是目标语言，本地跳过、不联网（所有语言通用）。
            //   - 中日韩：汉字/假名/谚文彼此字形正交，纯按字符安全判定（下详），绝不误伤别种语言。
            //   - 拉丁 / 西里尔：同族内部（英↔法、俄↔乌）字形完全相同，无法只凭字符区分语言，
            //     故仅当「当前游戏语言 == 目标语言」时才按同文字跳过——这样英→英秒跳，而英文游戏翻成
            //     法语(目标≠游戏语言)时英文正文仍照翻，不会被误判成"已达标"而漏翻。
            //   - 跨族（拉丁↔西里尔↔中日韩）字形正交，外语文字永不被同族规则误跳，仍照常翻译。
            if (IsAlreadyTargetScript(text, targetLocale, activeLocale)) return true;

            // 规则④：「1~4 位数字 + 1~2 个拉丁字母」的倍率/度量短串（2x、4x、8x、3d、4k、1080p、24h）→ 跳过。
            // 2026-09-27 实机凭据：候选项纳入翻译后，中文界面里游戏仍写作 "2x"/"4x" 的那档超采样倍率
            // 被谷歌翻成 'X' 和 'ch' —— 数字丢了，比不翻更坏（日志 [懒翻译] '2x' -> 'X'）。
            // 这类串国际通用、翻不出新信息。判据故意收得紧：整串必须只有数字开头加至多两个字母，
            // 带空格/标点/更长后缀的（"5th Generation"）一律不在此列，照送引擎。
            // ⚠ v1.1.2 另开规则⑧（LooksLikeUnitValue）把"数字 + 白名单单位词"这一大类也收走（5 min、60 fps、
            //    300dpi、16 x 9）——④ 自己的口径没动，别把它当"所有带字母的短串都不翻"，那会开始吞正常句子。
            if (LooksLikeMeasurement(text)) return true;

            // 规则⑤：输入/输出路径与控制路径（v1.0.5 需求3 点名"文件路径、快捷键设置不要翻"）。
            // 为什么这一格必须单独拦：需求2 放开"目标语言超出官方 12 语时连我们自己的文案也送翻"之后，
            // 设置页里显示的日志目录、模组目录这类字符串就第一次进入了翻译链 —— 一条路径被翻成
            // "C:\游戏\我的 设置.json" 是会让玩家照着找不到文件的事故形状，不是外观问题。
            // 判据故意收紧（宁可漏拦也不误拦整句中文说明）：
            //   · 控制路径：Unity 的格式一定带尖括号，如 <Keyboard>/x、<Mouse2D>/left；
            //   · 文件路径：以盘符开头（C:\），或带反斜杠且以已知扩展名结尾，或直接就是 .log/.json/.dll/.coc/.cache 这类文件名。
            if (LooksLikePathOrControl(text)) return true;

            // 规则⑥：整串就是一个专有名词（模组标题 / 赞助与下载平台名）→ 跳过。
            // 2026-09-29 从日志里数出来的（见 §10 第 70 条）：'Discord' -> '不和谐'（4 次）、'Ko-fi' -> '咖啡'、
            // 'Skyve' -> '滑块'/'滑动'/'スライドnono'、'Move It' -> '快点'/'移动它' —— 这些是名字，不是词，
            // 机翻只会把它变成另一个中文词，玩家照着界面反而找不到那个按钮/那个模组。
            // 判据【只认整串相等】：句子里出现 "Discord" 不算（"Join our Discord" 仍照翻），
            // 因为一旦按词匹配就会开始吞正常句子 —— 那是另一种"没翻干净"，方向正好相反。
            if (IsProtectedName(text)) return true;

            // 规则⑦（v1.1.2，玩家点名「设置语言那类下拉框不需要翻译」）：整串就是一个语言名 → 跳过。
            // 实机凭据（2026-10-01 15:09:07）：`id=Français 源=Français → 上屏=英语` —— 法语被翻成"英语"，
            // 而玩家在这个框里找的就是「Français」这三个字母本来的样子。表在 LanguageCatalog（44 种母语写法
            // + 常见英文称呼），同样【只认整串相等】："Use French layout" 这种句子照翻。
            if (LanguageCatalog.IsLanguageName(text)) return true;

            // 规则⑧（v1.1.2，玩家点名「时间、分钟这种每个选项就几个字母+数字的也不需要」）：
            // 数字 + 单位/技术记号（5 min、30 sec、1 h、60 fps、1080p、1920x1080、2 x、10 MB、v1.2.3、#FF8800）
            // → 跳过。这是规则④（2x/4x 那种"数字+≤2 字母"）放宽到"带单位词"的一版：放宽的边界由那张单位白名单
            // 钉死 —— 任何一个含字母的片段只要不在表里，整串就照常送去翻译，所以 "5 minutes walk"、"3 players"
            // 这类真句子不会被吞。**为什么值得单独一条**：档位类候选项几乎都是这种形状，一旦被翻成
            // "5 分钟"/"分钟 5" 语序乱掉，比留原文难读得多（'2x' -> 'X' 那次事故是同一类的极端形）。
            if (LooksLikeUnitValue(text)) return true;

            // 规则⑨（v1.1.2）：语言码（en-US、zh-HANS、pt-BR）→ 跳过。这种串出现在"选语言"栏、引擎参数栏里，
            // 它是给机器看的标识符；翻成"英语-美国"只是噪音。判据要【带连字符】才算，
            // 否则两个字母的正常词（"Hi"、"OK"）会被误吞。
            if (LooksLikeLocaleCode(text)) return true;

            return false;
        }

        // 整串只有数字/符号（没有任何字母）⇒ true。与 ShouldSkipLocal 的规则①同一个判据，
        // 单独开个名字是给 v1.1.2 的"整栏判定"（DropdownKit.ClassifyColumn）复用：档位框里
        // "1" / "2" / "10" 这种纯数字候选项也该算进"这是一栏数值"的证据里。
        public static bool IsNumericOnly(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            bool digit = false;
            for (int i = 0; i < text.Length; i++) if (char.IsDigit(text[i])) { digit = true; break; }
            return digit && !HasTranslatableText(text);
        }

        // ===== v1.1.2 规则⑧：数字 + 单位/技术记号 =====
        // 白名单只收"当单位讲"的记号。刻意不收：普通名词、颜色名、材质名、以及任何可能是句子里一个词的串。
        static readonly HashSet<string> s_UnitWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 时间
            "s", "sec", "secs", "second", "seconds", "ms", "us", "ns", "min", "mins", "minute", "minutes",
            "h", "hr", "hrs", "hour", "hours", "d", "day", "days", "w", "wk", "wks", "week", "weeks",
            "mo", "month", "months", "y", "yr", "yrs", "year", "years",
            // 倍率 / 档位 / 维度
            "x", "xx", "p", "d", "2d", "3d", "4k", "8k", "u", "u2",
            // 频率与速率
            "fps", "hz", "khz", "mhz", "ghz", "rpm", "kph", "mph", "bps", "kbps", "mbps", "gbps",
            // 尺寸与体积
            "px", "dpi", "ppi", "ppm", "m", "cm", "mm", "km", "kg", "mg", "g", "t", "l", "ml", "cl", "lb", "oz",
            // 容量
            "k", "kb", "mb", "gb", "tb", "bit", "bits", "byte", "bytes", "b",
            // 色彩空间与图形记号（只在带数字时才生效，见主判据）
            "rgb", "rgba", "hsl", "hsv", "cmyk", "hex", "yuv",
            // 其它单位前缀尾
            "v", "kv", "mv", "w", "kw", "ma", "fa",
        };

        // 整串是"数字 + 单位"的技术值 ⇒ true。要求：长度 ≤24、纯 ASCII、至少一个数字、【且】至少一个 token
        // 真的带一个白名单单位；任何含字母又不认识的片段 ⇒ 直接 false（宁可漏拦也不吞正常句子）。
        public static bool LooksLikeUnitValue(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.Length < 2 || t.Length > 24) return false;

            bool hasDigit = false, hasUnit = false;
            int start = 0;
            for (int i = 0; i <= t.Length; i++)
            {
                // 夹在两个数字之间的 x/X 是"乘"（1920x1080、8x8）：它既是分隔符、【本身也是这条串是技术值的证据】
                // —— 少了后半句，"1920x1080" 会被切成两段纯数字而判不出这是分辨率（纯数字段不设 hasUnit）。
                if (i < t.Length && (t[i] == 'x' || t[i] == 'X')
                    && i > 0 && i + 1 < t.Length && char.IsDigit(t[i - 1]) && char.IsDigit(t[i + 1]))
                {
                    if (i > start) { if (!UnitTokenOk(t, start, i - start, ref hasUnit)) return false; }
                    hasUnit = true; start = i + 1; continue;
                }
                bool sep = i == t.Length || IsUnitSeparator(t, i);
                if (!sep)
                {
                    char c = t[i];
                    if (c >= '0' && c <= '9') hasDigit = true;
                    else if (c > 127) return false;              // 非 ASCII（"30 秒"、"1 分钟"）交给别的规则判，翻成目标语言反而是对的
                    continue;
                }
                if (i > start)
                {
                    if (!UnitTokenOk(t, start, i - start, ref hasUnit)) return false;
                    start = i + 1;
                }
                else start = i + 1;
            }
            return hasDigit && hasUnit;
        }

        // 分隔符：空白与常见连接标点（"5-min"、"10 MB"、"16:9"、"rgb(255,0,0)"）。
        // ⚠ 小数点【不算】分隔符 —— "1.5x" 里 1.5 必须是一个 token，切开就没有单位可判了。
        // ⚠ 夹在两个数字之间的 x/X 当分隔符（那是"乘"：1920x1080、8x8），而跟在数字后面的单个 x 是【单位】
        //    （2x、16x 倍率），不能当分隔符，否则 "2x" 会退化成"纯数字"而判不出这是倍率。
        private static bool IsUnitSeparator(string t, int i)
        {
            char c = t[i];
            if (c == 'x' || c == 'X')
                return i > 0 && i + 1 < t.Length && char.IsDigit(t[i - 1]) && char.IsDigit(t[i + 1]);
            return c == ' ' || c == '\t' || c == ',' || c == ':' || c == '/' || c == '(' || c == ')'
                || c == '+' || c == '_' || c == '*' || c == '~' || c == '|' || c == '-'
                || c == '×' || c == '·';
        }

        // 一个 token：纯数字 ⇒ ok；`#RRGGBB`（含数字）⇒ ok；`v1` / `1.5x` 这种"数字+单位"或"前缀+数字"⇒ 记 hasUnit；
        // 纯字母但确实是个单位词（"60 fps" 里的 fps）⇒ ok；其它 ⇒ 不是技术值。
        private static bool UnitTokenOk(string t, int start, int len, ref bool hasUnit)
        {
            bool allDigits = true, hasLetter = false;
            for (int i = start; i < start + len; i++)
            {
                char c = t[i];
                if (c >= '0' && c <= '9') continue;
                allDigits = false;
                if (char.IsLetter(c)) hasLetter = true;
                else if (c != '#' && c != '.') return false;
            }
            if (allDigits) return true;

            // #FF8800 / FF8800：十六进制色值（含数字，长度 3~8）
            int s = start, e = start + len;
            if (t[s] == '#') { s++; }
            int hexLen = e - s;
            if (hexLen >= 3 && hexLen <= 8 && IsHexRun(t, s, e)) { hasUnit = true; return true; }

            // 前缀 v（版本号 v1.2.3）：剥掉一个前导字母 v/V 再看剩下的
            if (hasLetter && (t[start] == 'v' || t[start] == 'V'))
            {
                string rest = t.Substring(start + 1, len - 1);
                if (rest.Length > 0 && rest[0] >= '0' && rest[0] <= '9') { hasUnit = true; return true; }
            }

            // 数字在前、字母在后（5min / 1080p / 2x / 60fps）；或字母在前但整段就是个单位词（fps、MB）
            int firstLetter = -1;
            for (int i = start; i < e; i++) if (char.IsLetter(t[i])) { firstLetter = i; break; }
            if (firstLetter < 0) return false;
            if (firstLetter == start)
            {
                // 纯单位词（"60 fps" 的 fps、"5 min" 的 min）⇒ 也算这条串是技术值的证据。
                // 旧写法这里只 return 不设 hasUnit，于是 "60 fps" 两段（60 纯数字 + fps 纯单位）谁都没举手，
                // 整串反而判 false —— 帧率档就这么漏了。
                if (!s_UnitWords.Contains(t.Substring(start, len))) return false;
                hasUnit = true;
                return true;
            }
            string prefix = t.Substring(start, firstLetter - start);
            foreach (char c in prefix) if (!(c >= '0' && c <= '9' || c == '.')) return false;
            string unit = t.Substring(firstLetter, e - firstLetter);
            if (!s_UnitWords.Contains(unit)) return false;
            hasUnit = true;
            return true;
        }

        private static bool IsHexRun(string t, int s, int e)
        {
            for (int i = s; i < e; i++)
            {
                char c = t[i];
                bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!ok) return false;
            }
            return true;
        }

        // ===== v1.1.2 规则⑨：语言码 =====
        // `xx` / `xx-YY` / `xx-YYYY`（en、zh-HANS、pt-BR、sr-Latn-RS…）这种【带连字符】的形状才算。
        // 光要求"两个字母"会吞掉 "Hi"、"OK"、"ID" 这类真文字，所以宁可漏掉裸码（en）也不误吞 ——
        // 裸码出现在语言下拉框里时，由规则⑦/整栏语言判据兜住。
        public static bool LooksLikeLocaleCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.Length < 5 || t.Length > 22) return false;
            int dash = t.IndexOf('-');
            if (dash < 2 || dash > 3) return false;                       // 前段只能是国家/语言码长度
            for (int i = 0; i < t.Length; i++)
            {
                if (i == dash) continue;
                char c = t[i];
                if (!(c >= 'a' && c <= 'z' || c >= 'A' && c <= 'Z')) return false;
            }
            return true;
        }

        // 专有名词表：只收「模组标题」和「平台名」这两类在都市天际线2 模组界面里会【单独成行】出现的名字
        // （按钮文字、分区标题、下拉框候选项）。刻意不收 steam / anarchy 这类"同时是普通名词"的串 ——
        // 整串相等就跳过意味着游戏本体某条真叫 "Steam" 的文案会永远留在英文，代价比收益大。
        // 新增名字的唯一凭据应该是日志里一条真实的 `[懒翻译] 'X' -> '错的东西'`，不是"我觉得这是品牌"。
        static readonly HashSet<string> s_ProtectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "discord", "ko-fi", "kofi", "patreon", "paypal", "gumroad",
            "github", "gitlab", "youtube", "twitch",
            "turbosquid", "sketchfab", "nexus mods", "nexusmods",
            "skyve", "move it"
        };

        // 整串（去首尾空白）命中上表 → true。零分配：不 ToLower 整个串，交给 OrdinalIgnoreCase 的 HashSet。
        public static bool IsProtectedName(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int a = 0, b = text.Length - 1;
            while (a <= b && char.IsWhiteSpace(text[a])) a++;
            while (b >= a && char.IsWhiteSpace(text[b])) b--;
            if (b - a < 2) return false;                       // 太短的不可能是这些名字
            if (b - a > 24) return false;                      // 表里最长那条 19 字符，再长就是句子
            return s_ProtectedNames.Contains(text.Substring(a, b - a + 1));
        }

        // 控制路径 / 文件路径。纯字符判断、零分配（热路径逐条调用，见 §10 第 9 条）。
        public static bool LooksLikePathOrControl(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;

            // ① Unity 控制路径：'<...>/...'，或整串就是 <...>。
            if (text[0] == '<' && text.IndexOf('>') > 0) return true;

            // ② 盘符开头：X:\ 或 X:/
            if (text.Length >= 3 && char.IsLetter(text[0]) && text[1] == ':' && (text[2] == '\\' || text[2] == '/'))
                return true;

            // ③ 带路径分隔符的串，且结尾是我们会出现的那几类文件名/目录名后缀。
            //    只认"末尾像已知扩展名"这一种，避免把"道路菜单/桥梁"这种正常说明整条放过不翻。
            bool hasSep = text.IndexOf('\\') >= 0;
            if (!hasSep) return false;
            int last = text.Length - 1;
            // 结尾是 .log/.json/.dll/.coc/.cache/.txt 之类（大小写不敏感），或结尾就是分隔符（纯目录）。
            while (last >= 0 && (text[last] == '\\' || text[last] == '/')) last--;      // 末尾斜杠先剥掉
            if (last < 0) return true;
            int dot = last;
            while (dot >= 0 && text[dot] != '.' && text[dot] != '\\' && text[dot] != '/') dot--;
            if (dot < 0 || text[dot] != '.') return false;                              // 最后一段没有扩展名
            return EndsLikeKnownExtension(text, dot);
        }

        // 只收录本模组自己会显示的那几类扩展名（.log/.json/.dll/.coc/.cache/.txt/.pdb），
        // 其它的一律不当路径 —— 判据宁可保守：漏拦一条路径字符串能改，误拦一整句说明会静默不翻。
        static bool EndsLikeKnownExtension(string text, int dot)
        {
            string ext = text.Substring(dot).ToLowerInvariant();
            return ext == ".log" || ext == ".json" || ext == ".dll" || ext == ".coc"
                || ext == ".cache" || ext == ".txt" || ext == ".pdb" || ext == ".so" || ext == ".bundle";
        }

        // 「数字 + 极少字母」的度量串（见规则④）。与 LooksLikeKeyBinding 一样是纯字符判断，零分配。
        public static bool LooksLikeMeasurement(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            int i = 0;
            while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
            int digits = i;
            if (digits < 1 || digits > 4) return false;             // 必须以数字开头，且数字不超过 4 位
            int letters = 0;
            for (; i < text.Length; i++)
            {
                char c = text[i];
                bool ascii = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
                if (!ascii) return false;                            // 中间出现空格/标点/非拉丁 → 不算度量串
                letters++;
                if (letters > 2) return false;                       // 后缀最多两个字母（否则 "300dpi" 这种会被误伤）
            }
            return letters >= 1;                                     // 纯数字走规则①，这里要求至少带一个字母
        }

        // 剥掉 {...} 占位符后还剩不剩「可翻的字母/表意字符」。占位符识别语义与 TransGuard.Mask 保持一致
        // （单行、close - i <= 64、不跨行）——那边不掩的，这里也不当作占位符，两处判定错开只会漏翻。
        // 为什么在本文件重写一份而不是去借 Mask：Scope.cs 与 TextKit.cs 都要能被离线壳各自独立编译。
        private static bool HasTranslatableText(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    int close = text.IndexOf('}', i + 1);
                    if (close > i && close - i <= 64 && text.IndexOf('\n', i, close - i) < 0)
                    { i = close; continue; }
                }
                char c = text[i];
                if (char.IsLetterOrDigit(c) && !char.IsDigit(c)) return true;
            }
            return false;
        }

        // 取语言主码（'-'/'_' 之前、小写）："en-US"→"en"、"zh-HANS"→"zh"、"pt-BR"→"pt"。
        private static string BaseLang(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return "";
            int dash = locale.IndexOfAny(new[] { '-', '_' });
            return (dash > 0 ? locale.Substring(0, dash) : locale).ToLowerInvariant();
        }

        // 「书写系统 == 目标语言」通用判定：返回 true = 可本地跳过不联网。
        private static bool IsAlreadyTargetScript(string text, string targetLocale, string activeLocale)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string tgt = BaseLang(targetLocale);
            if (tgt.Length == 0) return false;

            bool hasLatin = false, hasCyrillic = false, hasHan = false, hasKana = false, hasHangul = false, hasOtherScript = false;
            foreach (char c in text)
            {
                if ((c >= '\u0041' && c <= '\u005A') || (c >= '\u0061' && c <= '\u007A') ||   // A-Z / a-z
                    (c >= '\u00C0' && c <= '\u024F') || (c >= '\u1E00' && c <= '\u1EFF')) hasLatin = true;   // 拉丁扩展 A/B + 扩展附加(越南语)
                else if (c >= '\u0400' && c <= '\u04FF') hasCyrillic = true;                 // 西里尔
                else if ((c >= '\u4E00' && c <= '\u9FFF') || (c >= '\u3400' && c <= '\u4DBF') || (c >= '\uF900' && c <= '\uFAFF')) hasHan = true;    // CJK 统一/扩展A/兼容
                else if ((c >= '\u3040' && c <= '\u30FF') || (c >= '\uFF66' && c <= '\uFF9F')) hasKana = true; // 平/片假名 + 半角片假名
                else if ((c >= '\uAC00' && c <= '\uD7A3') || (c >= '\u1100' && c <= '\u11FF') || (c >= '\u3130' && c <= '\u318F')) hasHangul = true; // 谚文
                else if (char.IsLetter(c)) hasOtherScript = true;                            // 希腊/阿拉伯/天城等：保守当作异族，绝不据此跳过
            }

            // 中日韩：书写体系彼此正交，方向性规则即可（不需参照游戏语言）。
            switch (tgt)
            {
                case "zh": return hasHan && !hasKana && !hasHangul;   // 中文：含汉字且无假名/谚文
                case "ja": return hasKana;                             // 日文：必须含假名（纯汉字多为中文，仍需翻→不跳）
                case "ko": return hasHangul;                           // 韩文：必须含谚文
            }

            // 拉丁 / 西里尔目标：仅当「当前游戏语言 == 目标语言」且文字纯为该文字族时跳过（否则交引擎，避免漏翻外语正文）。
            string act = BaseLang(activeLocale);
            if (act.Length == 0 || act != tgt) return false;
            bool cleanLatin = hasLatin && !hasCyrillic && !hasHan && !hasKana && !hasHangul && !hasOtherScript;
            bool cleanCyrillic = hasCyrillic && !hasLatin && !hasHan && !hasKana && !hasHangul && !hasOtherScript;
            if (IsCyrillicTarget(tgt)) return cleanCyrillic;
            if (IsLatinTarget(tgt)) return cleanLatin;
            return false;   // 未知文字族目标：不本地跳，交引擎判定。
        }

        private static bool IsCyrillicTarget(string b)
        {
            switch (b)
            {
                case "ru": case "uk": case "be": case "bg": case "sr": case "mk": case "kk": case "mn": return true;
                default: return false;
            }
        }

        private static bool IsLatinTarget(string b)
        {
            switch (b)
            {
                case "en": case "fr": case "de": case "es": case "it": case "pt": case "nl": case "sv":
                case "da": case "no": case "nb": case "nn": case "fi": case "is": case "pl": case "cs":
                case "sk": case "hu": case "ro": case "hr": case "sl": case "lt": case "lv": case "et":
                case "tr": case "az": case "vi": case "id": case "ms": case "tl": case "ca": case "eu":
                case "gl": case "sw": case "af": case "la": return true;
                default: return false;
            }
        }

        // 快捷键启发式：只含 ASCII 字母/数字 + 分隔符，且含已知键名 token，或很短的纯 ASCII 组合。
        private static bool LooksLikeKeyBinding(string text)
        {
            if (text.Length > 24) return false;
            bool asciiOnly = true;
            bool hasPlusOrCombo = false;
            foreach (char c in text)
            {
                if (c > 127) { asciiOnly = false; break; }
                if (!(char.IsLetterOrDigit(c) || c == ' ' || c == '+' || c == '-' || c == '/' || c == ','))
                    return false; // 含其它符号（如 '?' '%'）→ 不是纯快捷键，交给别的规则
                if (c == '+' || c == '/') hasPlusOrCombo = true;
            }
            if (!asciiOnly) return false;

            string lower = text.ToLowerInvariant();
            string[] keyTokens =
            {
                "ctrl","control","alt","shift","enter","return","space","tab","esc","escape",
                "win","cmd","command","mouse","lmb","rmb","mmb","del","delete","ins","insert",
                "home","end","pgup","pgdn","pageup","pagedown","up","down","left","right","backspace","caps"
            };
            for (int i = 0; i < keyTokens.Length; i++)
                if (lower.IndexOf(keyTokens[i], StringComparison.Ordinal) >= 0) return true;

            // F1..F12
            for (int n = 1; n <= 12; n++)
                if (lower.IndexOf("f" + n.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0) return true;

            // 形如 "A"、"Q+E"、"W A S D" 的极短 ASCII 组合。
            if (hasPlusOrCombo) return true;
            string trimmed = text.Trim();
            if (trimmed.Length == 1 && char.IsLetter(trimmed[0])) return true;

            // 空格分隔的键位串（"W A S D"、"Q E R T"）：按空白切开后每个 token 恰好 1 个 ASCII 字母/数字，且至少 2 个 token。
            // 刻意【不】放宽到 2~3 字符的 token —— 那样 "to go"、"U S A" 这类真文字会被误判成键位而永远不翻，
            // 漏翻比多翻一次严重得多。走到这里 text 已保证是纯 ASCII 且只含字母数字与 + - / , 和空白。
            string[] parts = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                bool everySingleKey = true;
                foreach (string p in parts)
                    if (p.Length != 1 || !char.IsLetterOrDigit(p[0])) { everySingleKey = false; break; }
                if (everySingleKey) return true;
            }

            return false;
        }
    }
}
