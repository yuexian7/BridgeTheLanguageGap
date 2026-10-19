using System;
using System.Collections.Generic;
using System.Text;

namespace Cs2AutoTranslator
{
    // ===== 下拉框候选项的翻译判定层（v1.0.5 需求1）=====
    //
    // 为什么要有这一层，以及为什么要单独一个文件：
    // 下拉框的文字翻不出来这件事，根因不在引擎、也不在钩子，而在【控件的序列化时机】
    // （decompiled Game/UI/Widgets/DropdownField.cs:63-86 与 EnumField.cs:44-66 实证）：
    //   ① 候选项在建页那一刻被 itemsAccessor / enumMembers 取走一次，存进控件自己的数组；
    //   ② DropdownField.Update() 只有在 itemsVersion() 与 m_ItemsVersion 不等时才重取，
    //      而这个版本号来自 AutomaticSettings 的 valueVersionAction，绝大多数选项根本没有
    //      [SettingsUIValueVersion] ⇒ 版本恒为 0 ⇒ 建页时那一次就是最后一次（OptionsUISystem.RefreshPage()
    //      也只重跑 UpdateVisibility / UpdateNameAndDescription / UpdateWarning，从不重取 items）；
    //   ③ 前端 JS 按本地化 id 解析文字（Game.dll 侧只把 id/value/args 原样写进 JSON，
    //      真正的 Translate 调用是 cohtml 的 SWIG director 打进 UILocalizationManager.Translate 的），
    //      解析过一次就缓存住了，后台译文晚一秒落地它就永远看不到。
    // 所以修法只能是：模组自己在 C# 侧把候选项的 displayName 换成【已经翻好的成品文字】
    // （LocalizedString.Value(译文)，id 置空 ⇒ 前端不再查字典、原样上屏），再用公开的
    // Widget.SetPropertiesChanged() 逼这个控件下一帧重发 props。
    //
    // 本文件只放这套判断里【不需要任何游戏对象就能算】的部分：一项该翻不该翻、有译文了该怎么落地、
    // 关开关了该怎么退回原文、以及那行统计日志怎么写。真正的反射与控件读写在 Mod.cs 的 DropdownL10n 里。
    //
    // ⚠ v1.1.1（2026-09-30 实机）：这一层错在【源文本解析】而不是控件读写。v1.1.0 那一局 525 项候选
    // 里「字典查不到 159 + 无key 16」= 175 项被"查不到 ⇒ 不翻"这一条挡掉，写入译文 0，所以玩家看到的
    // 第三方模组下拉框一个都没动过。真凭据：decompiled UILocalizationManager.cs:27 —— 字典查不到时
    // 游戏 `data.Set(key)`，也就是【把 key 原样写上屏】；而 LocalizedString.cs:124 那条隐式转换让
    // 模组写 `displayName = "Access zones only"` 时那句英文进的就是 id 字段。两句合起来 = 这种项的
    // 待翻源文本就是 id 本身。判据见下面的 LooksLikeDisplayText（形状判别：像键名不翻、像人话才翻）。
    // 铁律：本文件【不得】引用 Unity / Game / Colossal 里的任何类型，也不得碰网络与磁盘
    // —— 否则 tests\t3 那个零游戏 DLL 的离线壳就编不动，这套判据又只能靠开一局游戏来验（历史上一半的
    // 下拉框事故都是因为「只能实机看」才发现的）。
    internal static class DropdownKit
    {
        // ===== 一个候选项在这一轮的处置 =====
        // 做成 int 常量而不是 enum：调用方要跟 Tally 的计数直接相加，省掉每次装箱/switch 之外的转换。
        public const int Skip = 0;       // 这一项不归我们管，别动它，也别计数成"待办"
        public const int Enqueue = 1;    // 还没译文：入队后台翻译，界面上先留着原文
        public const int Apply = 2;      // 有译文了：把成品文字写进控件
        public const int Restore = 3;    // 我们之前改过、现在不该再改：把建页时那份原始 LocalizedString 写回去
        public const int Keep = 4;       // 控件里已经是正确的译文：什么都不做（这条决定能不能停止轮询）

        // 本模组自己的本地化键都带这个串（真值在 ModInfo.kKeyMarker，与 Patches.Prefix 共用同一个常量，
        // 不存在"两边判据不一致"）。L10n.cs 的 SettingsLocale 已经按【游戏当前语言】从 12 语表里出文案，
        // 再机翻一遍只会把 "DeepL" 翻成别的东西。
        public const string SelfKeyMarker = ModInfo.kKeyMarker;

        public static bool IsSelfKey(string id)
        {
            return !string.IsNullOrEmpty(id) && id.IndexOf(SelfKeyMarker, StringComparison.Ordinal) >= 0;
        }

        // ===== 「我们自己的文案这一格要不要拦」=====（v1.0.5 需求2）
        // 拦（返回 true）的唯一情形：它是我们的键，【且】玩家选的目标语言在那 12 种官方界面语言之内 ——
        // 那时 L10n 手写表/SelfL10n 已经按游戏语言把文案出好了，再机翻只会翻坏品牌名（"DeepL"→"深度链接"）。
        // 不拦的情形：目标语言是 12 种之外的小语种。那时全屏都成了小语种、只有本模组的设置页还是英语，
        // 玩家要的是这一格也翻（他 2026-09-29 点名）。范围闸（勾选「游戏本体/模组选项」）仍在后面照常生效。
        // Prefix 与下拉框两条链都必须走这一个函数：判据分叉的后果是"标题翻了两遍但候选项没翻"。
        public static bool SkipSelfKey(string id, string targetLocale)
        {
            return IsSelfKey(id) && LanguageCatalog.IsOfficialUiLocale(targetLocale);
        }

        // ===== 这一轮该扫【哪一页】（v1.1.4，玩家点名：没打开的页一条都不该送去翻）=====
        // 一手凭据（decompiled Game/UI/Menu/OptionsUISystem.cs:1011，游戏自己的 OnUpdate 循环）：
        //   `if (!string.IsNullOrEmpty(m_SearchQuery) || m_ActivePageBinding.value == value.id)`
        //   ⇒ 游戏每帧只对【当前打开的那一页】重跑 UpdateNameAndDescription / UpdateVisibility，
        //     唯一的例外是玩家在搜索框里打了字（那时它把命中结果的各页并排显示）。
        // 为什么必须收窄到这一格：`RegisterSetting` 在模组注册选项时就对**每一个标签页**都 BuildTab()
        //   （交接文档 §10 第 62 条），所以控件树里 56 个页、2084 处行文字全都存在且可读写 ——
        //   v1.1.3 那版把整棵树全枚举了一遍，实机凭据（2026-10-02 16:00:01 那一行）是
        //   「新入队 46 ＋选项行文字 2084 处：新入队 294」：一把往队列里塞 340 条【玩家根本没打开的页】的文字，
        //   1.4 秒后撞上 Microsoft 的 429，三条线程一起歇 20 秒 ⇒ 玩家眼睛里就是「开了翻译半天没动静」。
        // 三条出口的取舍：
        //   · searching ⇒ 全扫（与游戏同一格判据，这时各页结果确实同屏）；
        //   · !activeKnown ⇒ 全扫。读不到活动页多半是游戏改版，那种情况下【宁可多扫】也不能把
        //     "打开的页也不翻"这个新功能关掉（用户 2026-10-02 的原话：不能影响功能的正常使用）；
        //   · activePage 为空串 ⇒ 一个都不扫：游戏还没选过任何页（面板从没打开过），这时没有任何
        //     文字在屏幕上，扫了就是白送。
        public static bool ShouldScanPage(string pageKey, string activePage, bool searching, bool activeKnown)
        {
            if (searching) return true;
            if (!activeKnown) return true;
            return !string.IsNullOrEmpty(activePage) && activePage == pageKey;
        }

        // ===== 还原扫描：这一页要不要走（v1.1.5）=====
        // 上面那条收窄治的是「送翻面太大」（玩家的额度与耐心），但它顺手咬到了反方向的一件事：
        // 译文是【写进控件对象】的，而"把写进去的东西放回原文"同样只走当前那一页 ⇒ 玩家点「清除缓存」
        // 或关掉总开关之后，【没打开的那几页】上我们写过的日文会一直挂在那儿，直到他切过去才退回来。
        // 实机凭据（2026-10-02 20:22:44 那次清代）：清完那一刻日志里没有 [下拉框] 这一行，
        // 第一条要到 20:22:47 才出现，而玩家的眼睛是「缓存清了、字还是日文」。
        // 所以还原按【整棵树】走：一次点击引起、一轮走完，不参与每 1.2 秒的周期扫描
        //（§10 第 81 条那条规矩针对的是周期热路径，这里的变化就是玩家明确按下的那一下）。
        // 两种整树扫描都从这里过：① "整体不要"（清代 / 关总开关 / 重新翻译）；
        // ② "只退被我关掉的这一档范围"（勾掉「模组选项及说明」那一类，走真实开关值，见 Mod.DropdownL10n.RevertOutOfScope）。
        public static bool ShouldVisitPage(bool fullSweep, string pageKey, string activePage,
                                           bool searching, bool activeKnown)
        {
            if (fullSweep) return true;
            return ShouldScanPage(pageKey, activePage, searching, activeKnown);
        }

        // ===== 整树扫描那一轮能不能排新活（v1.1.5）=====
        // 两种整树扫描【都不排】。第 ① 种靠"enabled 一律当假"就自然轮不到 Enqueue；
        // 第 ② 种走的是真实开关值（不然会把玩家还想留着的译文一起退掉），
        // 所以"别顺手把 55 个没打开的页塞进队列"必须单独钉在这里 ——
        // 否则 v1.1.4 那条收窄（只看活动页 + 每轮最多 24 条）当场被顶穿，等于把那次 429 洪水请回来。
        // 判据写成一条能被离线断言钉住的函数，而不是靠调用方记得传对 enabled。
        public static bool MayEnqueue(bool fullSweep) { return !fullSweep; }

        // ===== v1.2.1 需求2/3：懒翻的三把闸 =====
        // 用户原话（v1.2.1 那一轮）：「只显示在当前界面上的文字送去翻译（上下滚动显示的也算一起）。
        // 而鼠标移到设置项跳出的说明、其他的标签页里的文字这些都是需要鼠标移动或点击才能看见的，
        // 看见后再送去翻译」。
        // 判据全部照抄游戏自己的代码，不发明第二套（发明的那套 v1.2.0 已经失败过一次，见下面②的注释）：
        //   ① 页：OptionsUISystem.cs:1011 `if (!string.IsNullOrEmpty(m_SearchQuery) || m_ActivePageBinding.value == value.id)`
        //   ② 标签页：:901 `m_ActiveSectionBinding.Update(section?.id ?? string.Empty)`（ShouldVisitSection）
        //   ③ 行：:902 `m_WidgetBindings.children = section.GetItems(m_IsAdvanced)`，
        //      搜索时换成 :884 那份跨页命中清单（OnScreenSet / IsOnScreen）
        //   ④ 行自己的 hidden() 委托：:233-245 的 `item.isVisible`（ShouldVisitWidget 照旧保留）
        // known 的取向四条完全一致：读不到就放行 —— 宁可多扫，也绝不能"玩家打开了页也不翻"
        //（用户 2026-10-02：不能影响功能的正常使用）。四条闸的兜底方向必须同向，
        // 否则会出现"页放行了、行却全拦"这种谁都解释不了的怪形状。

        // ②标签页那一闸。搜索时整闸打开：命中项散在各页各分区，由 ③ 那份清单逐行筛。
        public static bool ShouldVisitSection(bool fullSweep, bool searching, bool idKnown,
                                              string activeSection, string sectionId)
        {
            if (fullSweep) return true;              // 还原扫描：整棵树都要走（v1.1.5 的成果不能被这闸吃掉）
            if (searching) return true;
            if (!idKnown) return true;               // 读不到"当前是哪个标签页" ⇒ 按旧行为放行
            return string.Equals(activeSection ?? "", sectionId ?? "", StringComparison.Ordinal);
        }

        // ④行自己的可见性那一闸（v1.2.0 原样保留，但它【不再是】标签页判据 —— 见下面 OnScreenSet 的注释）。
        public static bool ShouldVisitWidget(bool fullSweep, bool visible, bool visibleKnown)
        {
            if (fullSweep) return true;
            if (!visibleKnown) return true;
            return visible;
        }

        // ③「此刻真正在屏幕上的那一串控件」—— 游戏自己算好的 OptionsUISystem.m_WidgetBindings.children：
        //   · SelectSection(page, section) 里换成 section.GetItems(m_IsAdvanced)
        //     （GetItems :148-166 已经把【折叠掉的分组】与【简单/高级模式对不上的项】筛掉）；
        //   · 搜索时 FilterWidgets() 换成 :811-884 从各页命中项拼出来的那份。
        // 为什么这是唯一可信的一格（v1.2.0 用 isVisible 的那一版为什么没做到）：
        //   `Widget.isVisible => !m_Hidden`，而 m_Hidden 只被 hidden() 委托写（decompiled Widget.cs）——
        //   它表达的是"模组自己要不要藏这一行"，跟"玩家现在滚到哪、点的是哪个标签页"毫无关系。
        //   非当前标签页的控件建页时就全部存在，m_Hidden 是从没刷新过的残留值 ⇒ 那道闸按定义拦不住别的标签页。
        //   实机凭据：玩家那一局日志「选项行文字 85 处 … 玩家看不见的行跳过 2 处」，两道 isVisible 闸合计只拦住 2 处。
        // 上下滚动不改这份清单（滚动是 cohtml 侧的事），所以"滚到才看见的那几行"照用户口径算同一屏、一起翻。
        //
        // 集合按【引用】判等：控件类型哪天重写 Equals/GetHashCode 的话，值判等会把两个不同控件并进同一格，
        // 于是"这行在不在屏上"变成看它文字像不像 —— 那是我们承担不起的错法，所以只用 ReferenceEquals。
        // 空清单 + 没在搜索 ⇒ known=false（放行）：那是玩家刚清空搜索框的一瞬间
        // （m_SearchQuery 变成 ""、SelectSection 仍走 FilterWidgets 那支，children 于是是空表），
        // 这一刻按"读不到"处理退回①②④，免得眼前整页文字突然不翻。
        public static HashSet<object> OnScreenSet(System.Collections.IList children, bool searching, out bool known)
        {
            known = false;
            if (children == null) return null;
            if (children.Count == 0 && !searching) return null;
            var set = new HashSet<object>(children.Count, ReferenceComparer.Instance);
            foreach (object w in children) if (w != null) set.Add(w);
            known = true;
            return set;
        }

        // 这一行此刻在不在屏上。清单读不到 ⇒ 放行（兜底方向与上面三条一致）。
        public static bool IsOnScreen(bool listKnown, HashSet<object> onScreen, object widget)
        {
            if (!listKnown || onScreen == null) return true;
            return widget != null && onScreen.Contains(widget);
        }

        // ===== 选项行【说明】那一半的悬停闸（v1.2.3，玩家原话见 Mod.cs 调用处）=====
        // 需求（玩家第三次提）：「设置项的说明文本还是会一起翻译啊，根本不是鼠标移上去显示后才开始翻译」。
        // 前两版（v1.2.1「等标题落地」、v1.2.2「标题落地后再延一轮」）改的是【延多久】，都还是整页翻好了
        // —— 因为它们等的都不是 hover，而是自己那条链上的下一个状态。这一版改的是【等什么】：
        // 等一个真的只在 hover 那一刻才出现的信号。那个信号前端有，2026-10-19 逐字核过实机 index.js
        //（ Cities2_Data\Content\Game\UI\index.js，选项页信息栏 z4）：
        //   const E = "string"==typeof x ? x : null;        // x = g && r ? u ?? c : p，即当前被 hover/键盘聚焦那一行的 path
        //   infoColumn: E && [ infoTitle:       T.props.displayName ? <yd value={…displayName}/> : <OPTION hash={E}/>
        //                        infoDescription: T.props.description ? <yd value={…description}/> : <OPTION_DESCRIPTION hash={E}/> ]
        // ⇒ 两块信息栏整体只在【有一行在眼前】时才渲染，而且只渲染【那一行】的说明；<yd>（Localized）
        //   对带 id 的 LocalizedString 会调 UILocalizationManager.Translate(id)，正是本模组的取词钩子。
        //   实机凭据：运行时日志 5 条「首次入队待翻译文本（key=Options.OPTION_DESCRIPTION[…]）」，
        //   而扫描样例里 352/358 条说明都带这个形状的 id。移开鼠标 p 归 null ⇒ 前端不再取词，
        //   所以"取过词"这件事天然只发生在玩家真的把它移到眼前的那些行上 —— 这就是托管层拿得到的 hover 凭据。
        // 判据于是只有一句：这一行说明的 id，前端【取过词】吗？没取过 ⇒ 按住不送。三个入参分别是：
        //   · sweep（整树还原那一轮）⇒ 不看闸：那一轮要把所有页上我们写过的译文放回原文，得见到每一行；
        //     它本来也不排新活（Decide 里 MayEnqueue 先挡、Enqueue 分支进不去），放开闸不会多发一条请求。
        //   · hasId == false ⇒ 没有信号可等：说明是裸文字（模组硬塞 LocalizedString.Value(...) 或
        //     id==null 的 SettingsUIDescription），前端 <yd> 直接上屏、压根不调取词钩子，等下去就是永远
        //     不翻。实机 358 条说明里 6 条是这种，全是版本字符串一类本来就不该翻的文字。
        //   · requested == true ⇒ 玩家已经把它移到眼前，前端此刻正等着这句话，照常走后面的处置。
        // 兜底方向与前面几道闸（不可见 / 不在屏 / 范围外）一致但【相反】：这一格拿不准就【按住】，
        // 因为按住的最坏后果是"玩家移上去后 1.2 秒内才变中文"（这正是需求要的行为），
        // 而放过的最坏后果是"整页说明在没移上去时就被翻完"（这正是玩家三次点名的 bug）。
        public static bool HoldDescription(bool sweep, bool hasId, bool requested)
        {
            return !sweep && hasId && !requested;
        }

        // 说明 id 的形状（唯一一处判据：Mod.cs 的 Prefix 喂闸、HandleRowText 问闸，两边都只问这里）。
        // 两种形状都收：框架路径产的是 Options.OPTION_DESCRIPTION[<path>]
        //（decompiled AutomaticSettings.cs:358-373 的 GetDescription 与 ModSetting.cs:313-316 的
        // GetOptionDescLocaleID，都是方括号形状），第三方直接塞给前端的还有 Options.OPTION_DESCRIPTION:<name>
        // 这种冒号形状（Scope.cs:78 记的 13 条，含 GROUP/SECTION）。前缀比较不吃 '[' / ':'，
        // 就是为了让冒号形状也进闸 —— 它同样是"移上去才显示"的说明，没道理放过。
        public static bool IsDescriptionKey(string key)
        {
            if (key == null) return false;
            return key.StartsWith("Options.OPTION_DESCRIPTION", StringComparison.Ordinal);
        }

        // 说明那一半"按住"的记账名（与 Deferred 分开：它等的不是预算，是玩家把它移到眼前）。
        // 下一局日志里这两个数要能各自回答"这一轮为什么没送"：Deferred 是限流，
        // DescHeld 是这一格故意按住的。混在一起就分不清是"舍不得送"还是"没钱送"。
        // ⚠ 这格数【只在首轮见到那一行时】会涨：按住一次就记一次，之后同一行反复被扫到不再计数
        //（s.Requested 置位后走正常链），所以它的量级是"这一页有多少行没被移上去过"，一眼可验。
        public const string DescHeldLabel = "说明等悬停（前端取过词才送）";

        // 只按引用判等的比较器（net48 没有 System.Collections.Generic.ReferenceEqualityComparer）。
        private sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();
            private ReferenceComparer() { }

            public new bool Equals(object a, object b) { return ReferenceEquals(a, b); }

            public int GetHashCode(object o)
            {
                return o == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(o);
            }
        }

        // ⚠ 与上面那条配套的一个【故意不管】：整树扫描这一轮允许把"内存里已有的译文"写上控件（Apply），
        // 只是不排新活。原因是这一轮早就先过了"不是我们写的地方一眼都不多看"那道早退
        //（`Applied == null` 直接返回），所以能被写的位置就是"我们本来就写过的那几处"——
        // 数量与还原那一档同级（几处到几十处），而且一个请求都不发。
        // 反过来，若这里也一并跳过，"玩家刚把某一档范围重新勾上"就要多等一轮周期扫描才补得回来，
        // 白挨一次"怎么又没翻"的误会。

        // ===== 一轮扫描最多新排几条（v1.1.4）=====
        // 光收窄到一页还不够：一页动辄 40~100 处文字，一次性全排进去照样是 3 条线程 × 120ms 的
        // 突发速率（上面那次 429 就是在 10 条请求之内撞的）。所以每轮最多新排这么多条，剩下的
        // 【记成延后、不当"翻过"、也不 mark Tried】，1.2 秒后的下一轮接着排 ——
        // 队列于是按这个节奏被喂，免费档的请求速率上限就不再被一整页一次顶穿。
        // 24 条这个数：官方 Graphics 页一轮扫到 152 项候选（v1.1.3 实机），24 条 ⇒ 六轮 ≈ 7 秒排完，
        // 而每轮之间正好把「玩家眼前那几条」排在最前（同一轮里 Prefix 入队的永远先到，队列是 FIFO）。
        public const int ScanEnqueueBudget = 24;

        public static bool BudgetLeft(int alreadyEnqueuedThisPass) => alreadyEnqueuedThisPass < ScanEnqueueBudget;

        // ===== id 到底是「键名」还是「屏幕上那句话」（v1.1.1 的真凶）=====
        // 一手凭据两条，叠在一起就是"第三方模组的下拉框死活不翻"：
        //   ① decompiled Game/UI/Localization/UILocalizationManager.cs:19-29 —— Translate 里
        //      `activeDictionary.TryGetValue(key, out value) ? data.Set(value) : data.Set(key)`：
        //      **字典查不到时，游戏把 key 自己原样写上屏**。所以"查不到"不等于"没文字可翻"，
        //      而是"屏幕上显示的就是那串 key 的形状"。
        //   ② decompiled Game/UI/Localization/LocalizedString.cs:124-127 —— `implicit operator
        //      LocalizedString(string id) => Id(id)`：模组写 `displayName = "Access zones only"`
        //      这样一句裸文字时，那句文字进的是 **id** 字段、value 是 null。
        // 两条合起来：这类候选项的待翻源文本就是 id 本身。v1.1.0 及之前把它判成"字典查不到 ⇒ 不翻"
        // （本文件下面的 ResolveSource 第四条出口），实机凭据是 2026-09-30 20:30 那一局：
        // 525 项候选里【字典查不到 159】+【无key 16】= 175 项卡在这一格，而写入译文 0。
        // 样例里那三条 `id=同组 / id=同菜单 / id=同类资产`（ToolModeMemory）就是这句话的形状。
        //
        // ⚠ 但不能无条件翻：官方 key 查不到时上屏的也是 key 本身，那串是 `Options.LEVEL[Ultra]`
        //    `TOOLGROUPS.ALL.Tools` 这种，送去机翻只会产出"选项。等级[超]"这种垃圾并污染缓存。
        //    所以这里做的是一条【形状】判别：像键名 ⇒ 不翻；像人话 ⇒ 翻。
        // 判据来源（不是凭感觉）：官方 en-US 语言包 22,163 条 key 的 75 个首段命名空间
        // （Assets/Options/Tutorials/SelectedInfoPanel/…，现算于 state\official-locale\parsed\en-US.json）
        // 全部是"大写开头的 ASCII 标识符 + 点分 + 不含空格 + 大多带 [标识符]"，
        // 而人话句子必然含空格、或含非 ASCII 文字、或带方括号以外的标点。
        // 返回 true = 这串就是玩家眼睛里的那句话，可以送去翻译。
        public static bool LooksLikeDisplayText(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            bool hasSpace = false;
            bool hasBracket = false;
            bool hasDot = false;
            bool hasNonAsciiLetter = false;
            bool lowerDottedShape = true;   // 整串是否只由「小写字母 / 数字 / . / _」组成（mode.access、enabled.label）

            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                if (c == '[' || c == ']') hasBracket = true;
                else if (c == '.') hasDot = true;
                else if (char.IsWhiteSpace(c)) hasSpace = true;
                if (c > 127 && char.IsLetter(c)) hasNonAsciiLetter = true;
                if (!(char.IsLower(c) || char.IsDigit(c) || c == '.' || c == '_')) lowerDottedShape = false;
            }

            // ① 带方括号 = 官方 key 的标识符段形状（Options.X[Y] / Assets.NAME[Fire Station] / Common.ACTION[...]）。
            //    注意这一条优先于"非 ASCII"：`Assets.NAME[消防局]` 里确实有中文，但那串整体是键名。
            if (hasBracket) return false;

            // ② 以数字开头 ⇒ 这一格的内容就是数字本身（分辨率 1920 x 1080、倍率 2 x、帧率 60 fps、年份 2026）。
            //    与 Scope 规则④同一条理由：这类串翻完最坏是【丢数字】，比不翻更坏（实机凭据：'2x' -> 'X'）。
            //    排在"非 ASCII"之前：中文开头的一般名词不会以数字开头，而 `3 人` 这种就该留在原样。
            if (id[0] >= '0' && id[0] <= '9') return false;

            // ③ 含非 ASCII 字母（中日韩韩文等）且不带方括号 ⇒ 不可能是官方 key，是文案本身。
            if (hasNonAsciiLetter) return true;

            // ④ 全小写点分（mode.access / enabled.label / group.main）= 模组自己的裸键名。
            //    游戏查不到时上屏的也是这串键名，翻它只会把 "mode" 翻成"模式"，玩家更看不懂。
            if (hasDot && !hasSpace && lowerDottedShape) return false;

            // ⑤ 「Namespace.Subname」式官方 key：大写开头、含点、不含空格，且点后面还有一段名字。
            //    点后面为空（"Done."）或整串就是一个词（"Follow"）都不算，那是人话。
            if (hasDot && !hasSpace && id[0] >= 'A' && id[0] <= 'Z' && HasNamedSecondSegment(id)) return false;

            return true;
        }

        // "Options.LEVEL" → true；"Done." / "Menu." → false（点后面没有名字，就是句末标点）。
        private static bool HasNamedSecondSegment(string id)
        {
            for (int i = id.Length - 1; i > 0; i--)
                if (id[i] == '.') return i + 1 < id.Length && char.IsLetterOrDigit(id[i + 1]);
            return false;
        }

        // ===== 解析这一项的「待翻源文本」 =====
        // 判据与 Patches.Prefix 逐条对齐，一点不放宽：
        //   · 没有 id（前端拿的是裸文字）⇒ 返回 null 不翻。这类项在游戏里就是【故意】显示原文的
        //     —— 语言列表里的母语名（InterfaceSettings.GetLanguageValues 用 LocalizedString.Value）、
        //     我们的模型下拉框里的模型 slug、分辨率这类。把它们翻成"德语/英语"反而是倒退。
        //   · id 是本模组自己的 ⇒ null。
        //   · id 在字典里查不到、且【像键名】⇒ null。真键名上屏的就是那串键名本身
        //     （Options.LEVEL[Ultra] 这种），把键名送去机翻只会产出"选项。等级[超]"这种垃圾并污染缓存。
        //   · id 在字典里查不到、但【像人话】⇒ 就用 id 本身（LiteralId）：模组把显示文字直接当 key 传进
        //     LocalizedString，游戏原样上屏（UILocalizationManager.cs:27 那句 data.Set(key)），
        //     这一格就是 v1.1.0 挡住 175 项候选的那条出口。
        //   · 字典命中 ⇒ 用命中值。这一步还顺带吃到了 I18NEverywhere 的人工译文
        //     （它 patch 了 TryGetValue），所以人工翻译优先、我们只补它没覆盖的外语，与 Prefix 同一条链。
        //
        // 三个版本共用这一个判据：两参的给离线表用（默认字典已就绪），带 miss 的给实机用 ——
        // 2026-09-27 第一局日志里出现过「448 项候选、七个动作全 0」，那种一行字说明不了是"没东西可翻"
        // 还是"链路断了"，因为四条 null 出口和字典没就绪全都被压成了同一个 0。所以这里把原因分出来。
        // LiteralId 也是一条【成功】出口（不是失败），放在同一个枚举里是为了让样例日志的桶名与它对齐。
        public enum SourceMiss { Ok, NoId, SelfKey, NotInDict, DictNotReady, TooShort, LiteralId, FallbackValue }

        public static string ResolveSource(string id, string dictValue)
        {
            SourceMiss ignored;
            return ResolveSource(id, dictValue, true, out ignored);
        }

        // dictReady = 调用方【确实拿到】了当前语言字典（GameManager.instance 为 null 时是拿不到，
        // 不是"字典里没有这条"）。这一格不分开，主菜单里那一轮就会把全部候选项误报成"查不到"。
        public static string ResolveSource(string id, string dictValue, bool dictReady, out SourceMiss miss)
        {
            return ResolveSource(id, dictValue, dictReady, true, out miss);
        }

        // guardSelf = 「这一轮要不要拦我们自己的文案」，真值只该来自 SkipSelfKey(id, target) 那一条判据
        // （目标语言在官方 12 语之内才拦）。传 false 时自己的键照常送去翻译。
        public static string ResolveSource(string id, string dictValue, bool dictReady, bool guardSelf, out SourceMiss miss)
        {
            if (string.IsNullOrEmpty(id)) { miss = SourceMiss.NoId; return null; }
            if (guardSelf && IsSelfKey(id)) { miss = SourceMiss.SelfKey; return null; }
            if (!dictReady) { miss = SourceMiss.DictNotReady; return null; }
            if (string.IsNullOrEmpty(dictValue))
            {
                // 字典没有这一条：先按形状决定"id 这串文字本身能不能当源文本"。
                if (LooksLikeDisplayText(id) && id.Length >= 2) { miss = SourceMiss.LiteralId; return id; }
                miss = SourceMiss.NotInDict; return null;
            }
            if (dictValue.Length < 2) { miss = SourceMiss.TooShort; return null; }   // 单字符（"A"、"%"）不翻，与 Prefix 同一条下限
            miss = SourceMiss.Ok;
            return dictValue;
        }

        // ===== 选项行【标题 / 说明】的源文本（v1.1.3，玩家点名 Real Weather Sync 与 Plop the Growables）=====
        // 为什么下拉框那套判据不够用：这一格文字的来源不是 DropdownItem，而是 NamedWidget.displayName /
        // .description，而模组普遍用 `LocalizedString.IdWithFallback(key, "英文原句")` 或干脆
        // `LocalizedString.Value("英文原句")` 交给前端（一手凭据：Game/UI/Menu/AutomaticSettings.cs:348/365
        // 官方自己就是这么建的；decompiled LocalizedString.cs:64-72 的 IdWithFallback 同时带 id 和 value）。
        // 前端看见 value 非空就【直接把那串英文上屏，根本不调 UILocalizationManager.Translate】
        // ⇒ 我们挂在 Translate 上的 Harmony 钩子对这类文字一次都看不见（实机凭据：2026-10-02 那一局，
        // 同一页里标题有 [懒翻译]、说明一句都没有，而 dll 里的说明句一条都没进过日志）。
        // 所以这一路必须由【选项页扫描】主动接管，判据顺序如下（与 ResolveSource 同一套口径，一条都不放宽）：
        //   ① 自己的键（guardSelf 为真时）⇒ SelfKey；
        //   ② 字典命中优先 ⇒ Ok：人工译文/I18NEverywhere 改过的值仍然赢过机翻（与 Prefix、下拉框同一条规矩）；
        //   ③ 没有命中值但 value 非空 ⇒ 就用 value，出口记 FallbackValue（这就是上面那条不走取词的路）；
        //      仍要过 LooksLikeDisplayText 的形状闸：`Options.LEVEL[Ultra]` 这种键名兜底不翻。
        //   ④ 都没有 ⇒ 退回 id 那一套（LiteralId / NotInDict / DictNotReady / NoId），行为与旧的一致。
        // value 为空是常态（游戏自己的行就是纯 id），那种情况走 ④，不会多花一次字典查询。
        public static string ResolveRowSource(string id, string value, string dictValue, bool dictReady,
                                             bool guardSelf, out SourceMiss miss)
        {
            if (guardSelf && !string.IsNullOrEmpty(id) && IsSelfKey(id)) { miss = SourceMiss.SelfKey; return null; }

            if (!string.IsNullOrEmpty(dictValue))
            {
                if (dictValue.Length < 2) { miss = SourceMiss.TooShort; return null; }
                miss = SourceMiss.Ok;
                return dictValue;
            }

            if (!string.IsNullOrEmpty(value))
            {
                // 兜底文字就是玩家眼睛里那句话：形状闸照旧（`Options.LEVEL[Ultra]` 这种键名形状不翻）。
                // ⚠ 形状闸没过 ⇒ 【不要】直接判死：这可能只是一句键名兜底 + 一条查不到的 key，
                //   落到下面 id 那一套去给准确出口（NoId / NotInDict），日志里才说得出为什么没翻。
                if (value.Length >= 2 && LooksLikeDisplayText(value)) { miss = SourceMiss.FallbackValue; return value; }
            }

            if (!dictReady) { miss = SourceMiss.DictNotReady; return null; }
            // 自己的键在上面第一行就拦掉了，这里 guardSelf 传 false 只是避免重复判一次。
            return ResolveSource(id, null, dictReady, false, out miss);
        }

        // ===== 单条决策 =====
        // translated  == TransMap 里已落地的译文（没有就传 null）
        // resolved    == TransMap.IsResolved(source)：已知这条不需要翻译（或已判死、已按本地规则跳过）
        // applied     == 我们【上一次】往这个控件里写进去的译文（没写过就传 null）。注意它记的是"我们写过什么"，
        //                不是控件现在的文字 —— 游戏随时可能自己把整批候选项换掉，那种情况下控件里的项是全新对象，
        //                弱表里没有记录，applied 自然是 null。
        // enabled     == 总开关且未处于"刚清除缓存"的暂停态（与 Prefix 头两行的闸门一致）
        // inScope     == 这一项的本地化 key 落在玩家勾选的翻译范围里（Scope.Classify + ScopeEnabled，
        //                与 Prefix 第 4 行同一道闸）。取消勾选「模组选项及说明」时，模组下拉框里已经写进去的
        //                译文必须退回原文 ——  Decide 收到 inScope=false 就是这么处理的。
        public static int Decide(string source, string translated, bool resolved, string applied,
                                 bool enabled, bool inScope)
        {
            if (source == null) return Skip;

            // 关掉翻译 / 关掉这一类范围：只负责把【我们自己写进去的】那几项还原，
            // 别人（其它模组的补丁）写的东西一律不碰。
            if (!enabled || !inScope) return applied != null ? Restore : Skip;

            if (!string.IsNullOrEmpty(translated) && translated != source)
                return applied == translated ? Keep : Apply;

            // 已知不需要翻译（已是目标语言 / 纯符号 / 快捷键 / 三次都失败判死）：
            // 若控件里还挂着我们早先写进去的译文（例如换了目标语言之后旧译文还在），退回原文。
            if (resolved) return applied != null ? Restore : Skip;

            return Enqueue;
        }

        // ===== 一轮扫描的统计 =====
        // 只为两件事存在：① 实机验证时那一行日志就是证据（玩家/作者不用逐个下拉框点开确认）；
        // ② 决定「还要不要再扫一轮」—— Pending>0 就继续，全 0 就停手，不留下每秒空转的后台开销。
        internal sealed class Tally
        {
            public int Widgets;       // 本轮看到的下拉框控件数
            public int Items;         // 这些控件里的候选项总数
            public int Enqueued;
            public int Applied;
            public int Restored;
            public int Kept;
            public int Skipped;       // 本地预筛判定"无需翻译"的项（纯数字/键位/已是目标语言）
            public int Unsafe;        // 译文不合格、被拦下没上屏的项
            public int Pending;       // 还欠着译文、下一轮要回来看的项数（Enqueue 与 Apply 之间的中间态）

            // ---- 诊断用（不参与任何判定，只进日志）----
            // 2026-09-27 第一局实机：「79 个下拉框 / 448 项候选」七个动作全 0。那一行既可能是"这个语言本来
            // 就没东西要翻"，也可能是"字典没拿到 / key 全是裸文字 / 游戏改版"，而这两种的处置完全相反
            // （前者不用管，后者要改代码）。所以把"没取到源文本"的四条出口和元素形状不符分开计数，
            // 并把页/分区数一起报出来 —— 分区数顺带就是"其它标签页有没有被扫到"的直接凭据
            // （官方把每个标签页建成同一个 page 下的一个 section，见 §10 第 62 条）。
            public int Pages;         // 本轮扫到的选项页数
            public int Sections;      // 其中有候选项行的分区数（一个标签页 = 一个分区）
            public int NoId;          // 候选项没有本地化 key（裸文字，故意不翻）
            public int SelfKey;       // 是本模组自己的文案
            public int NotInDict;     // key 在当前语言字典里查不到、而且形状像键名
            public int DictNotReady;  // 这一轮根本没拿到字典（区别于"查不到"）
            public int TooShort;      // 命中值短于 2 字符
            public int NotItem;       // 控件里的元素没有可写的 displayName（游戏改版才会这样）
            public int Literal;       // 字典查不到、但 id 就是屏幕上那句话 ⇒ 直接拿 id 当源文本（v1.1.1）
            public int LangWidgets;   // 整栏判成"选择语言"的下拉框，一条都不翻（v1.1.2，玩家点名）
            public int TimeWidgets;   // 整栏判成"数字+单位"的档位/时间选择框，一条都不翻（v1.1.2）

            // 源文本拿到了、但玩家没勾这一类范围开关 ⇒ Decide 返回 Skip，七个动作一个都不加。
            // 2026-09-29 实机就是被这一格坑的：日志「405 项候选：七个动作全 0」，而诊断段只解释了 255 项，
            // 剩下 150 项既不是"没源文本"也不是"翻了"，凭空消失 ⇒ 又花了一局才挖出来是范围闸。
            // 凡"全 0 也算正常"的统计行都要能把为什么没做说全（§10 第 66 条那条教训的延续）。
            public int OutOfScope;

            // ---- 选项行标题与说明（v1.1.3，与候选项完全分开的两套计数）----
            // 为什么必须单列：这一格文字【不走取词钩子】（模组给的是带英文兜底的 value，前端直接上屏），
            // 所以玩家看见"整页英文"时，日志里连一条 [懒翻译] 都不会有 —— 没有独立计数就无从分辨
            // "没请求"与"请求了但不该翻"。实机凭据：2026-10-02 那一局，Plop the Growables 的
            // 标题有 7 条 [懒翻译]、说明句一条都没有。
            public int Rows;              // 本轮看到的 NamedWidget 行（标题+说明各算一处，这里数的是【处】）
            public int RowApplied;        // 写进去译文了几处
            public int RowEnqueued;       // 排了几处（等下一轮）
            public int RowKept;           // 已经是我们的译文，没动
            public int RowSkipped;        // 本地预筛判"不用翻"（已是目标语言/纯数字/单位档…）
            public int RowOutOfScope;     // 范围没勾
            public int RowFallback;       // 源文本来自【英文兜底值】（那条不走取词的路）⇒ 这一版的生效凭据
            public int RowRestored;       // 退回模组原文（关翻译/改范围/换语言）

            // ---- 送翻触发面收窄（v1.1.4，玩家点名「没打开的页一条都不该送去翻」）----
            // 这两个数就是那条规矩的凭据：下一局日志里 SkippedPages 应当等于「页数 - 1」，
            // 而 Deferred>0 说明本轮确实被预算挡下过东西（那些东西下一轮会补，不是丢了）。
            public int SkippedPages;      // 本轮【因为不是活动页】而整页跳过的页数
            public int Deferred;          // 本轮因为超出 ScanEnqueueBudget 而留到下一轮的处数

            // v1.2.0 需求2：本轮【因为玩家看不见】而整行跳过的控件数（游戏自己的 isVisible 为假）。
            // 为什么必须单列成一格：它是"懒翻真的在懒"的唯一凭据 —— 下一局日志里这个数应当
            // 远大于 Rows（一个模组页里大部分行是折叠分区/高级选项/说明，只有玩家眼前那几行可见）。
            // 少了这一格，"没翻"与"懒得翻"在日志里长得一模一样（§10 第 66 条那个教训的延续）。
            public int Invisible;

            // v1.2.1 需求3：把"看得见"的判据换成游戏自己的在屏清单之后，三把闸各要有一格凭据。
            // 下一局日志期望的形状：玩家切到某一页时「不在屏上的行跳过」吃掉同页其它标签页与折叠分组的
            // 绝大部分，而「选项行文字」≈ 眼前那一屏的行数（几十而不是几百）。
            // 少了这三格，"没翻"与"懒得翻"在日志里又会长得一模一样（§10 第 66 条）。
            public int SkippedSections; // 因为"不是玩家点着的那个标签页"而整个分区跳过
            public int OffScreen;       // 因为"不在游戏这次算好的在屏清单里"而跳过的行
            // v1.2.3：说明这一半被悬停闸【故意按住】的处数（这一行的说明 id 前端还没取过词）。
            // 三代各改了一版判据：v1.2.1 等"标题落地"、v1.2.2 等"标题落地 + 再延一轮"，玩家两次都说
            // 整页还是一起翻；v1.2.3 等的是前端真的为这一行说明调过取词钩子（DropdownKit.HoldDescription）。
            // 下一局期望的形状：玩家刚点开一页、鼠标还没动过时，这一格 ≈ 该页说明的总条数减去眼前那几行；
            // 而"选项行写入"里带 说明 的样例只在玩家移上去之后才出现。
            public int DescHeld;
            public int OnScreen = -1;   // 本轮在屏清单里有几个控件；-1 = 读不到（退回旧行为）

            // 活动页那一格（v1.1.4）：由调用方把游戏自己记的 m_ActivePageBinding.value 传进来。
            // 只进日志，不参与判定 —— 但它是"这一轮为什么只扫了这一页"的唯一答案，
            // 下一局核对实机时第一眼就要能在日志里看到它。
            public string ActivePage;

            // v1.1.5：这一轮是【整棵树还原扫描】（玩家点了清除缓存 / 关了总开关）。
            // 只影响日志那一格怎么写：这时候报"活动页 X"会误导 —— 这一轮每一页都走了，
            // 而且一条新活都没排（目的只是把我们写进控件的译文放回原文）。
            public bool Sweep;

            // v1.1.4：预算与页闸的凭据格。
            public int Budget;            // 本轮用的预算上限（ScanEnqueueBudget，写日志用）

            // ---- 分页明细（v1.1.1，只进日志，不参与任何判定）----
            // 2026-09-30 那一局的分类计数能说清"175 项卡在字典查不到"，但说不清【是哪个模组的哪个下拉框】，
            // 而玩家报的永远是某一个具体框（"Access Anarchy 那个框没翻"）。样例每类只抓 3 条、24 条封顶，
            // 抓不全；所以这里按【页】各记一行：官方页与模组页混在一张树里，按页归拢后一眼能看出
            // "哪一页有裸文字命中、哪一页整页卡在范围外"。页数上限 20（实机 55 个页里绝大多数是零候选项的
            // 空页，而且 Row() 只在还要记的时候才建，超上限直接返回 null 由调用方跳过）。
            internal sealed class PageRow
            {
                public string Tag;
                public int Items;         // 这一页里的候选项总数
                public int Dict;          // 源文本来自字典命中（SourceMiss.Ok）
                public int Literal;       // 源文本来自 id 裸文字本身（v1.1.1 新出口）
                public int NotInDict;     // 键名形状、字典又查不到 ⇒ 不翻
                public int NoId;          // 连 id 都没有（游戏故意上屏裸值）
                public int SelfKey;       // 本模组自己的文案
                public int TooShort;      // 命中值过短
                public int OutOfScope;    // 玩家没勾这一类范围
                public int Applied;       // 真的写进控件了
                public int Enqueued;      // 排队等译文
            }

            private const int kMaxRows = 20;
            private readonly List<PageRow> _rows = new List<PageRow>();
            private readonly Dictionary<string, PageRow> _rowIndex = new Dictionary<string, PageRow>();

            // 返回 null = 这一页没记上（超过上限），调用方据此跳过计数，不报错。
            public PageRow Row(string tag)
            {
                if (string.IsNullOrEmpty(tag)) return null;
                PageRow r;
                if (_rowIndex.TryGetValue(tag, out r)) return r;
                if (_rows.Count >= kMaxRows) return null;
                r = new PageRow { Tag = tag };
                _rows.Add(r);
                _rowIndex[tag] = r;
                return r;
            }

            public string PagesDetail()
            {
                if (_rows.Count == 0) return "";
                var sb = new StringBuilder("明细");
                for (int i = 0; i < _rows.Count; i++)
                {
                    PageRow r = _rows[i];
                    sb.Append(" ｜ ").Append(r.Tag).Append(" 项").Append(r.Items);
                    if (r.Dict > 0) sb.Append(" 字典").Append(r.Dict);
                    if (r.Literal > 0) sb.Append(" 裸文字").Append(r.Literal);
                    if (r.NotInDict > 0) sb.Append(" 查不到").Append(r.NotInDict);
                    if (r.NoId > 0) sb.Append(" 无key").Append(r.NoId);
                    if (r.SelfKey > 0) sb.Append(" 自己").Append(r.SelfKey);
                    if (r.TooShort > 0) sb.Append(" 过短").Append(r.TooShort);
                    if (r.OutOfScope > 0) sb.Append(" 范围外").Append(r.OutOfScope);
                    if (r.Enqueued > 0) sb.Append(" 入队").Append(r.Enqueued);
                    if (r.Applied > 0) sb.Append(" 写入").Append(r.Applied);
                }
                return sb.ToString();
            }

            // ---- 第一次扫描时按类别抓几条【真实 key】进日志（只进日志，不参与任何判定）----
            // 分类计数只说"有 106 项字典查不到"，不说【是哪些】。而 2026-09-29 那个问题
            // （AccessAnarchy 的下拉框到底卡在哪一步）只有看到真 key 的形状才能判 —— 猜的成本太高：
            // 猜"没 key"、猜"字典查不到"、猜"范围没勾"三种猜测的修法完全相反。
            // 所以每类最多抓 3 条、整轮最多 24 条，只在第一行诊断后打一次。
            private const int kSamplePerBucket = 3;
            private const int kSampleTotal = 24;
            private readonly List<string> _samples = new List<string>();
            private readonly Dictionary<string, int> _sampleTaken = new Dictionary<string, int>();

            // 调用方拼样例字符串前先问一句：样例是限量的，别让 400 项每条都白拼一次串（这在主线程每轮都跑）。
            public bool WantsSamples { get { return _samples.Count < kSampleTotal; } }

            public void Sample(string bucket, string detail)
            {
                if (_samples.Count >= kSampleTotal) return;
                int n;
                _sampleTaken.TryGetValue(bucket, out n);
                if (n >= kSamplePerBucket) return;
                _sampleTaken[bucket] = n + 1;
                _samples.Add(bucket + "：" + detail);
            }

            public string Samples()
            {
                return _samples.Count == 0 ? "" : "样例→ " + string.Join(" ｜ ", _samples.ToArray());
            }

            // 本轮已经新排了多少条（候选项 + 选项行两处合用一个预算，因为限流算的是【总请求数】，
            // 分两本账的话每一本都可以各送 24 条，等于预算翻倍）。
            public int NewQueues { get { return Enqueued + RowEnqueued; } }

            // 「这一轮没有欠账」= 可以不再自动扫了。⚠ 字典没就绪也算欠着（那是暂时性的，
            // 停下来等下一次取词请求就可能一直等不到 —— 2026-09-27 那一局就是这么静默停住的）。
            // v1.1.4：被预算挡下的那些（Deferred）同样算欠账 —— 它们没被 mark Tried、也没进队列，
            // 这一格要是漏掉，ScanEnqueueBudget 就会把一整页的文字永久卡在第二轮之后
            //（玩家眼前的页反而不翻了，那是比洪水更糟的回归）。
            public bool Idle { get { return Pending == 0 && DictNotReady == 0 && Deferred == 0; } }

            public void Count(SourceMiss m)
            {
                switch (m)
                {
                    case SourceMiss.NoId: NoId++; break;
                    case SourceMiss.SelfKey: SelfKey++; break;
                    case SourceMiss.NotInDict: NotInDict++; break;
                    case SourceMiss.DictNotReady: DictNotReady++; break;
                    case SourceMiss.TooShort: TooShort++; break;
                    case SourceMiss.LiteralId: Literal++; break;   // 成功出口，不是失败，别混进"没取到源文本的"那一串
                }
            }

            // 一行中文摘要（写进日志，不参与任何判定）。
            public string Line()
            {
                var sb = new StringBuilder();
                // v1.1.4：每一行统计都先说清"这一轮看的是哪一页"——换页能不能自己补上（§F 第 37 条）
                // 只看这一格，不用等第一次那一行诊断。
                // v1.1.5：还原扫描那一轮改成先标 [全树还原] —— 这时报"活动页 X"是误导，
                // 因为这一轮每一页都走了（玩家点了清除缓存 / 关了总开关，要的是"全部放回原文"）。
                if (Sweep) sb.Append("[全树还原] ");
                else if (!string.IsNullOrEmpty(ActivePage)) sb.Append("[活动页 ").Append(ActivePage).Append("] ");
                sb.Append(Widgets).Append(" 个下拉框 / ").Append(Items).Append(" 项候选：")
                  .Append("写入译文 ").Append(Applied)
                  .Append("，已是译文 ").Append(Kept)
                  .Append("，退回原文 ").Append(Restored)
                  .Append("，新入队 ").Append(Enqueued)
                  .Append("，本地跳过 ").Append(Skipped)
                  .Append("，范围外 ").Append(OutOfScope)
                  .Append("，不合格未上屏 ").Append(Unsafe)
                  .Append("，等译文 ").Append(Pending)
                  // v1.1.4：这一格是「本轮被预算挡下、下一轮再排」的处数。它必须出现在正常那一行里，
                  // 否则玩家/作者看到"这一页只排了 24 条"会当成漏翻 —— 那是故意限速，不是丢了。
                  .Append("，延后 ").Append(Deferred).Append(' ');
                // v1.1.3：选项行标题/说明单独一段。这一段没有就说明这一轮根本没看到 NamedWidget
                //（还没进过选项页），玩家报"某一页的说明不翻"时，这一行第一段就是要看的数。
                // v1.1.5：还原扫描那一轮【只看我们写过的地方】，所以 Rows 不计数（没写过的地方一眼都不多看），
                // 但退回原文的处数必须照样看得见 —— 玩家报"清了缓存还是日文"时要看的就是这一格。
                // 判据因此从"这一段有没有数"改成"这一轮有没有碰过选项行这一层"。
                if (Rows > 0 || RowApplied > 0 || RowRestored > 0)
                    sb.Append("｜选项行文字 ").Append(Rows).Append(" 处：写入 ").Append(RowApplied)
                      .Append("，已是译文 ").Append(RowKept)
                      .Append("，新入队 ").Append(RowEnqueued)
                      .Append("，退回原文 ").Append(RowRestored)
                      .Append("，本地跳过 ").Append(RowSkipped)
                      .Append("，范围外 ").Append(RowOutOfScope);
                // v1.1.4：整页跳过数只在【真跳了】的时候进这一行（常态是"页 56 → 跳 55"，
                // 那一格就是"没打开的页一条都没送"的直接凭据；为 0 时说明是搜索那一类例外，不必刷）。
                if (SkippedPages > 0) sb.Append("｜整页跳过 ").Append(SkippedPages).Append(" 页");
                // v1.2.0 需求2：看不见的行一处都没送。与「整页跳过」是同一性质的两格
                //（一个按页、一个按行），合起来才是"懒翻"的完整凭据：玩家在日志里应当看到
                //「看不见的行 >> 看得见的行」，否则就是这一道闸没生效。
                if (Invisible > 0) sb.Append("｜玩家看不见的行跳过 ").Append(Invisible).Append(" 处");
                // v1.2.1 需求3：另外两把闸（标签页 / 在屏清单）同样只在真跳了的时候进这一行。
                // 这两格才是玩家那句「每个模组选项界面的所有文字都给我送去翻译」的直接答案：
                // 上一版的 Invisible 只拦住 2 处，就是因为 isVisible 那把闸按定义管不到标签页。
                if (SkippedSections > 0) sb.Append("｜非当前标签页跳过 ").Append(SkippedSections).Append(" 个分区");
                if (OffScreen > 0) sb.Append("｜不在屏上的行跳过 ").Append(OffScreen).Append(" 处");
                if (DescHeld > 0) sb.Append("｜" + DescHeldLabel + " ").Append(DescHeld).Append(" 处");
                return sb.ToString();
            }

            // 诊断那一半：只在【第一次真看到控件】时跟在 Line() 后面打一次，之后不再打（否则每轮刷两行）。
            public string Diag()
            {
                var sb = new StringBuilder();
                // 活动页那一格放在最前面：v1.1.4 之后"这一页为什么没翻"的第一个问题就是"它是不是当前打开的那一页"。
                sb.Append("活动页 ").Append(string.IsNullOrEmpty(ActivePage) ? "(未记录)" : ActivePage);
                if (Budget > 0) sb.Append("；每轮最多新排 ").Append(Budget).Append(" 条");
                sb.Append("；本轮扫到 ").Append(Pages).Append(" 页 / ").Append(Sections).Append(" 分区");
                if (SkippedPages > 0) sb.Append("，整页跳过 ").Append(SkippedPages).Append(" 页");
                // v1.2.1 需求3：这一格回答"游戏这一次算出来的在屏清单里有几个控件"。
                // 懒翻生效时它应当 ≈ 眼前一屏的行数（十几到几十），而不是整个页的几百行；
                // (读不到) 则说明字段名随版本改了，扫描退回 v1.1.4 那一档。
                sb.Append("；在屏控件 ").Append(OnScreen >= 0 ? OnScreen.ToString() : "(读不到)").Append(" 个");
                sb.Append("；没取到源文本的：无key ").Append(NoId)
                  .Append("，自己的文案 ").Append(SelfKey)
                  .Append("，字典未就绪 ").Append(DictNotReady)
                  .Append("，字典查不到 ").Append(NotInDict)
                  .Append("，命中值过短 ").Append(TooShort)
                  .Append("，元素形状不符 ").Append(NotItem);
                // v1.1.1：这一串不是"失败原因"而是一条成功出口，单独报出来 —— 它等于
                // "字典查不到、但游戏会把 id 原样上屏，所以我们直接翻 id"的项数，是这一轮修复的生效凭据。
                sb.Append("；其中 id 就是屏幕上那句话（直接拿 id 当源文本）").Append(Literal).Append(" 项");
                // v1.1.2：整栏跳过的那两类（选择语言 / 数字档位）单独报，玩家问"这个框怎么没翻"时
                // 这一格就是答案本身 —— 它是【故意不翻】，不是漏翻。
                if (LangWidgets > 0 || TimeWidgets > 0)
                    sb.Append("；整栏不翻：语言选择框 ").Append(LangWidgets).Append(" 个，数值档位框 ").Append(TimeWidgets).Append(" 个");
                // v1.1.3：这一格是这一版【新开通的那条路】的生效凭据 —— 源文本来自模组给前端的那串
                // 英文兜底值（那种文字永远不调 Translate，所以我们以前根本看不见它）。
                if (Rows > 0 || RowFallback > 0)
                    sb.Append("；选项行文字里，源文本是模组自带的英文兜底值的：").Append(RowFallback).Append(" 处");
                return sb.ToString();
            }

            // 累计（跨多轮统计用；实机日志只在真有变化时打一行，所以这里也留着便于自测）
            // ⚠ 分页明细（PageRow）不在这里合并：日志的分页那一行只在第一次扫描打，跨轮累加反而会把
            // 同一项算两遍。Add() 目前只服务于离线自测里的"动作计数"。
            public void Add(Tally other)
            {
                if (other == null) return;
                Widgets += other.Widgets; Items += other.Items; Enqueued += other.Enqueued;
                Applied += other.Applied; Restored += other.Restored; Kept += other.Kept;
                Skipped += other.Skipped; Unsafe += other.Unsafe; Pending += other.Pending;
                OutOfScope += other.OutOfScope;
                Pages += other.Pages; Sections += other.Sections;
                NoId += other.NoId; SelfKey += other.SelfKey; NotInDict += other.NotInDict;
                DictNotReady += other.DictNotReady; TooShort += other.TooShort; NotItem += other.NotItem;
                Literal += other.Literal;
                LangWidgets += other.LangWidgets; TimeWidgets += other.TimeWidgets;
                Rows += other.Rows; RowApplied += other.RowApplied; RowEnqueued += other.RowEnqueued;
                RowKept += other.RowKept; RowSkipped += other.RowSkipped; RowOutOfScope += other.RowOutOfScope;
                RowFallback += other.RowFallback; RowRestored += other.RowRestored;
                SkippedPages += other.SkippedPages; Deferred += other.Deferred;
                Invisible += other.Invisible;
                SkippedSections += other.SkippedSections; OffScreen += other.OffScreen;
                DescHeld += other.DescHeld;
                // OnScreen 是"本轮清单里几个控件"这种瞬时读数，不是累计量：取对方那个数
                // （-1 表示读不到）。两边一正一负时保留正的那个，别让合并把凭据抹成 -1。
                if (other.OnScreen >= 0 && OnScreen < 0) OnScreen = other.OnScreen;
            }
        }

        // 达到"这一项已经处理完了"的处置：Apply / Keep / Restore / Skip 都不需要再回头看，
        // 只有 Enqueue（等译文）与它带来的 Pending 需要下一轮。纯函数，测试壳直接钉。
        public static bool NeedsAnotherRound(int action) => action == Enqueue;

        // ===== 整栏用途判定（v1.1.2，玩家点名「语言下拉框、时间/分钟这种档位框不需要翻译」）=====
        // 为什么要在【整栏】这一层判，而不是只靠单条判据（Scope 规则⑦⑧）：
        //   ① 实机凭据（2026-10-01 15:09:07）：`写入译文：页:Cs2AutoTranslator… id=Français → 上屏=英语` ——
        //      法语被翻成"英语"。这种栏里的候选项本来就是【各语言的母语写法】，玩家找自己的语言靠的
        //      就是那串字长自己本来的样子，翻成中文反而是倒退；而别的模组的写法五花八门
        //      （"Magyar nyelv"、"English (United Kingdom)"），逐条认一定会漏。
        //   ② 档位/时间框（1 min / 5 min / 30 min / 1× / 2×）整栏都是"数字+单位"，逐条已经能拦住大部分，
        //      但混着一个 "Default" 就会漏一条、显得半生不熟；整栏判一次更稳。
        // 判据是【过半】而不是"有一条像就算"：一栏里只有一条写着 English 不代表这是语言栏，
        // 而一栏里过半都是语言名，几乎不可能是别的用途。要求至少 2 条，避免单项框被误判。
        public enum ColumnKind { Normal, LanguagePicker, UnitPicker }

        public static ColumnKind ClassifyColumn(IList<string> itemTexts)
        {
            if (itemTexts == null || itemTexts.Count < 2) return ColumnKind.Normal;
            int lang = 0, unit = 0, total = 0;
            for (int i = 0; i < itemTexts.Count; i++)
            {
                string t = itemTexts[i];
                if (string.IsNullOrEmpty(t)) continue;
                total++;
                if (LanguageCatalog.IsLanguageName(t)) lang++;
                else if (Scope.LooksLikeUnitValue(t) || Scope.LooksLikeLocaleCode(t) || Scope.IsNumericOnly(t)) unit++;
            }
            if (total < 2) return ColumnKind.Normal;
            if (lang >= 2 && lang * 100 >= total * 60) return ColumnKind.LanguagePicker;
            if (unit >= 2 && unit * 100 >= total * 70) return ColumnKind.UnitPicker;
            return ColumnKind.Normal;
        }

        // 这一栏该不该【整栏】跳过翻译。返回 true 时调用方一条都不碰，并在日志里计一栏。
        public static bool SkipWholeColumn(ColumnKind k)
        {
            return k == ColumnKind.LanguagePicker || k == ColumnKind.UnitPicker;
        }

        // ===== 目标语言换了之后的失效判定 =====
        // 弱表里记着"我们给这一项写过哪句译文"和"那是翻成哪种语言的"。语言一换，旧译文必须作废：
        // 先退回原文、再按新语言重新排队（Decide 看不到语言，所以这一步由调用方在 Decide 之前做）。
        public static bool IsStale(string appliedLocale, string targetLocale)
        {
            if (string.IsNullOrEmpty(appliedLocale)) return false;     // 没写过，谈不上过期
            return appliedLocale != targetLocale;
        }

        // ===== 写进控件前的最后一道安全检查 =====
        // decompiled 实证过一次的崩溃（交接文档 §10 第 15c 条）：隐藏行的值是 null 时，
        // Colossal.UI.Binding 的 non-nullable string writer 会在 UIUpdate 里抛 ArgumentNullException，
        // 整帧 OptionsUISystem 更新一起炸掉。我们往 displayName 里塞裸文字同样吃这条约束：
        // 空串/纯空白/超长（引擎把整段说明当一句回来的跑飞结果）一律不上屏。
        public const int MaxAppliedLength = 400;

        public static bool IsSafeToDisplay(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            if (text.Length > MaxAppliedLength) return false;
            for (int i = 0; i < text.Length; i++)
                if (!char.IsWhiteSpace(text[i])) return true;
            return false;   // 整串都是空白
        }

        // ===== 候选项数组的形状判定 =====
        // 返回 null = 这不是"带候选项的文字数组"，调用方直接跳过。
        // 只做纯字符串/类型名层面的判断，真正的反射在 Mod.cs；放这里是因为【哪些属性名算候选项列表】
        // 是一张会随游戏改版变的表，必须能被离线断言钉住，而不是散在反射代码里。
        static readonly string[] kListProps = { "items", "enumMembers" };

        public static IEnumerable<string> ListProperties()
        {
            for (int i = 0; i < kListProps.Length; i++) yield return kListProps[i];
        }

        // 元素类型是否"带一个可读写的 displayName（本地化文字）"。
        // 由调用方把属性名/类型名传进来，这里只核对命名，避免离线壳引用游戏类型。
        public static bool ElementLooksLikeAnItem(string displayNamePropName, string displayNameTypeName)
        {
            return displayNamePropName == "displayName" && displayNameTypeName == "LocalizedString";
        }
    }
}
