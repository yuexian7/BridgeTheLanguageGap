namespace Cs2AutoTranslator
{
    /// <summary>
    /// 模组身份信息（版本号 / 作者署名 / 三个跳转链接）的唯一来源。
    ///
    /// 为什么要单独收一个文件：版本号原先只活在 Mod.cs 载入横幅那一整串中文里，改版本时必须去
    /// 长句子里找一个子串，找不到就出现「产物已经是新版、启动日志还写着旧版」这种事故
    /// （姊妹模组 AccessAnarchy 的 v0.7.9 就这么翻过一次）。「关于」板块要把版本号单独显示一行，
    /// 更不可能再从横幅字符串里截。现在它是常量，门禁脚本 scripts/verify.mjs 逐字比对三处：
    /// Mod.cs 横幅里的 v&lt;版本&gt; / Properties\PublishConfiguration.xml 的 ModVersion / 本文件的 kVersion。
    ///
    /// 离线可测：这个类只用编译期常量，不碰任何游戏类型，所以 tests\t3 的离线壳能把它编进去直接断言
    /// （见交接文档 §10 第 29~31 条：能离线测的前提是「这个类一个游戏类型都不碰」）。
    /// </summary>
    internal static class ModInfo
    {
        /// <summary>
        /// 模组版本号，不带 v 前缀，与平台 ModVersion 同格式。
        /// ⚠ 这是【本地待发布】的版本：v1.1.0（原来的 1.0.5）只在源码与本地 xml 里，Paradox Mods 上已发布的
        /// 那版仍是 1.0.4（平台 modVersion 4）。发布前后都别把这句话当旧的删掉——它是"这份号还没上线"的唯一现场记录。
        /// 为什么 1.0.5 改号成 1.1.0：这一轮实际落地的改动远超一个修订号的分量（下拉框跟随翻译 + 范围类目改判 +
        /// 专有名词保护 + 吞吐 + 引擎全线复探 + 构建临时文件自清理），用户 2026-09-30 拍板改号。
        /// ⚠ 编号规矩（用户定的，后续照走）：同一会话内继续改动只加最后一位（1.1.1、1.1.2…），
        ///   开新会话才加中间那位（1.2.0）；用户一般会直接写明。
        /// 1.1.1（2026-09-30 当晚，同一个会话）：用户实机反馈「下拉框依旧没有被翻译」。日志（525 项候选、
        /// 写入译文 0、字典查不到 159 + 无key 16）定位到【源文本解析层】错了：很多模组把选项文字直接当
        /// 本地化键名传进 LocalizedString.id（LocalizedString.cs:124 的隐式 string→id 转换），字典里当然查不到，
        /// 我们旧判据就把整批判成"不是文案"跳过；而游戏在查不到时本来就把那串 key 原样上屏
        /// （UILocalizationManager.cs:27 的 data.Set(key)），所以那串 id 正是玩家看见的那句话。
        /// 判据见 DropdownKit.LooksLikeDisplayText；另一半是 Scope.ClassifyItem（模组页上的候选项改归
        /// 「模组选项及说明」，否则解开一道闸又撞上「游戏本体」那道，本机实测 ScopeGameCore=false）。
        /// 1.1.2（2026-10-01，同一个会话，用户三条反馈）：① 「连不上翻译服务，换引擎也没用」—— 根因不在引擎：
        ///   实机 15:32 起先 SecureChannelFailure、后 ConnectFailure「由于目标计算机积极拒绝」，微软与谷歌两条通道
        ///   同时报同一句 ⇒ 系统代理指向的端口没了。Http 现在在连接层失败时【自动换一条出口重试】（走代理 ↔ 直连）
        ///   并记住可用那一档 2 分钟，玩家修好代理不必重启游戏；② 「下拉框只翻了一半就再也不补」—— 一次限流风暴
        ///   就能把一整屏文字各烧掉一次重试机会（三条 worker 撞同一堵墙 = 每条 3 次用完判死）。现在外部条件
        ///   （429 / 连不上）走 TransMap.NoteTransient，【不烧文本预算】，并按 429 自适应拉长请求间距；
        ///   ③ 「语言下拉框、时间/分钟这种档位框不该翻」—— Scope 规则⑦⑧⑨ + DropdownKit.ClassifyColumn 整栏判定。
        /// 1.1.3（2026-10-02，同一个会话，用户四条反馈）：
        ///   ① 部分模组整页连"设置项的说明"都不翻（实机凭据 Real Weather Sync 154183 / Plop the Growables 75826，
        ///     日志里它们的【说明句】一条 [懒翻译] 都没有，而标题句有 ⇒ 说明根本没被送去取词）。根因在取词之外
        ///     的另一条路：选项行的标题/说明走 NamedWidget.displayName / .description，模组给的是
        ///     LocalizedString.IdWithFallback(key, "英文兜底")，前端看见 value 非空就直接上屏，
        ///     【不会调 UILocalizationManager.Translate】⇒ 我们那道 Harmony 钩子对这类文字永远看不见。
        ///     现在由选项页扫描直接接管这两处文字（NamedWidget 的 action 包装 + 属性写入，带还原），
        ///     并给 Scope 补一条：`Options.OPTION:X` 这种【冒号形式】的 key 归「模组选项及说明」——
        ///     现算凭据：官方 1595 条 Options.* key 里冒号形式【0 条】（state\official-locale\parsed\en-US.json）。
        ///   ② 性能（对照 AccessAnarchy 那类"高频热路径做与变化无关的重活"，见交接文档 §10）：
        ///     RegisteredModIds 的节流判据在"没装别的模组"时恒假 ⇒ 每个 Options.* 取词都重新反射；
        ///     Prefix 把 Scope.Classify 排在缓存命中之前 ⇒ 每次取词都 Substring 建串（现加 key→类目小缓存）；
        ///     选项页扫描每轮重新反射取 PropertyInfo/MethodInfo（现按类型缓存）。
        ///   ③ 卸载安全：OnDispose 现在真的 UnpatchAll（原来只有装补丁和"被别人拆了重打"，没有拆）。
        ///   ④ 界面文案：「游戏画面图层」那段说明从总开关挪到它自己那一格，并按用户给的话重写总开关末句。
        /// 1.1.4（2026-10-02，同一个会话，用户一条反馈）：「开启翻译后很久都没翻译，过一会才开始正常翻译。
        ///   是不是开启瞬间在翻译大量的什么文本？我一开始就说过：只有在打开新界面时才判断哪些文本送去翻译，
        ///   不是没打开前就全部送去翻译。」—— 实机凭据（本机日志最后一局 2026-10-02 16:00:00 点开关）：
        ///   16:00:01 那一行是「页 56 / 分区 103 / 选项行文字 2084 处 / 新入队 46 + 294」，
        ///   ⇒ 扫描器把【56 个玩家一个都没打开的设置页】全枚举了一遍，一把塞进 340 条；
        ///   16:00:02 撞 Microsoft 429，16:00:03「所有翻译线程一起歇 20 秒」，16:00:23 才恢复 ⇒ 就是那句"半天没动静"。
        ///   同一局 16:00:00 还在主菜单（三份世界缓存 0 条），所以这次的洪水与路名无关，纯粹是整棵树全枚举。
        ///   三处改：① DropdownKit.ShouldScanPage + Mod.DropdownL10n.ReadPageGate —— 扫描只看游戏自己记的
        ///   m_ActivePageBinding.value（判据抄 OptionsUISystem.cs:1011：搜索时才各页并排，那种情况照旧全扫；
        ///   读不到活动页就退回旧行为，宁可多扫也不把功能关掉）；② DropdownKit.ScanEnqueueBudget=24 的每轮预算，
        ///   超出的记 Deferred（不 mark 已处理、下一轮原样补，Tally.Idle 因此为假，绝不会"只翻前 24 条"）；
        ///   ③ 退避分档（前台/世界两份冷却 + 两份间距 + 两份熔断计数）+ EngineKit.RateLimitCooldownMs 梯子
        ///   3→6→10→封顶 20 秒：世界文字那场洪水撞的墙不再冻住界面文字，瞬时限流也不再一歇就是 20 秒。
        /// 1.1.5（2026-10-02，同一个会话，用户两条反馈）：「目标语言是日语，清除了缓存后模组界面的文本仍然是日文，
        ///   并在很短时间内变回了中文，这是不是说明缓存没清干净？」—— 答案是【缓存清干净了，但控件里的字不是缓存】。
        ///   实机凭据（本机日志同一局）：20:22:44「已清空内存译文表并删除磁盘缓存文件」+「预载 0 条历史译文」
        ///   ⇒ 内存表、待追加队列、磁盘文件三处都空；可玩家看见的那句日文【不在任何缓存里】，
        ///   而是我们写进控件对象的（DropdownField 的 item.displayName / NamedWidget 的 displayName、description），
        ///   游戏把这些对象一直养在选项页树里，只有我们的扫描会再去改它们。而还原这一步从 v1.1.4 起
        ///   只走"当前打开的那一页"——收窄送翻面的副作用正好咬在还原这一侧，凭据是同局的三条：
        ///   20:22:36 AccessAnarchy 页「上屏=주차장…」（韩语）→ 20:22:44 清代那一刻【一行 [下拉框] 都没有】
        ///   → 20:22:47 第一条的活动页已换成 ToolModeMemory（「退回原文 60」），AccessAnarchy 那一页则落进
        ///   「整页跳过 23 页」再没人管。他报的"仍然日文 → 短时间变回中文"就是这一页 1.2~3 秒后被补还原，
        ///   而其它页根本没被还原。四处改（第二句追问之后再加第五处，见下面⑤）：
        ///   ① DropdownL10n.RevertAll + Scan(reason, revertSweep) + DropdownKit.ShouldVisitPage —— 玩家点
        ///   「清除缓存」/关总开关/按「重新翻译」那一下，【整棵树】走一轮还原：每一页都看、enabled 一律当"翻译已关"、
        ///   一条新活都不排，且"不是我们写的地方"一眼都不多看（开销只与写过多少处有关，与树多大无关）；
        ///   ② EngineKit.ResultStillWanted + TranslationCache.ClearStamp —— 出发之后被清过的那几条在飞结果
        ///   一律作废：不写内存表、不写 translation.cache（旧写法只防"请求期间换了目标语言"那一格，
        ///   所以清代期间出去的请求会把刚删掉的文件重新写出来，那才是真正的"没清干净"），也不退回队列
        ///   （Reset 后排空过的新队列不该被旧活塞进去，而且 WorldSources 已清空，退回会把路名错投进前台那一队）；
        ///   L10n 本模组自翻那条链同样加闸（清代后只喂界面、不写盘、不整表重写）；
        ///   ③ DropdownL10n.LogScan 的"有动静"判据补上选项行那三个数，且还原扫描那一轮无条件打一行
        ///   （以前只动选项行的还原在日志里完全不留痕，玩家报这个问题时我们只能靠推理）；
        ///   ④ TransMap 的在飞计数只减到 0 为止（Reset 清零 + 清代前出发的请求回来各减一次 = 负数，
        ///   而 IsIdle 判的是 == 0 ⇒ 本局再也不会出现"真的排空了"）。
        ///   紧接着用户第二句：「如果是把所有的页上的这一档的译文都切回去，那是否对性能有较大影响，
        ///   没有的话就按你说的改」—— 评估结论是【没有较大影响】（一次点击一轮、不是周期活；没写过的槽位
        ///   只花一次弱表查询 + 判空，无反射读、无字典查、零请求；开销与"我们写过多少处"成正比而与树多大无关），
        ///   于是补第⑤处：DropdownL10n.RevertOutOfScope（走【真实】enabled，只退刚被关掉那一档，
        ///   玩家还想留着的那一档不无辜回退 = 不白烧额度）+ DropdownKit.MayEnqueue（整树那一轮一条新活都不排，
        ///   否则这一档会顺手把 55 个没打开的页塞进队列、把 v1.1.4 刚收窄的洪水请回来）。
        ///   接入点在 SaveNow：总开关关了走 RevertAll，还开着只是改了设置走 RevertOutOfScope。
        /// ⚠ 源码与测试表里那些写着「v1.0.5 需求1」「v1.0.5 第四条」的注解，指的都是【这一轮】：它从没以 1.0.5
        ///   的名义发布过，中途整体抬号成 1.1.0，不是两个版本。别按 1.0.5 去线上找这一轮的改动。
        /// </summary>
        internal const string kVersion = "1.1.5";

        /// <summary>作者署名。按作者本人要求逐字用 "yuexian"，不是平台账号名 "yuexian7"。</summary>
        internal const string kAuthor = "yuexian";

        /// <summary>
        /// 本模组全部本地化键的公共标记（ModsData 目录名、ModsSettings 文件名、L10n 的 SettingsLocale
        /// 键前缀用的都是这个串）。
        ///
        /// 它的用途是「这条取词请求是我们自己的文案」—— Patches.Prefix 与 v1.0.5 的 DropdownKit 都靠它
        /// 跳过自己的 12 语表，否则下拉框里那 14 个引擎名、8 家厂商名会被再机翻一遍
        /// （"DeepL" 变 "深度链接" 那种）。两边共用这一个常量，不存在"两条链判据不一致"。
        /// ⚠ 别顺手改它：它同时是磁盘路径名，改了等于给老玩家凭空造一份空设置。
        /// </summary>
        internal const string kKeyMarker = "Cs2AutoTranslator";

        /// <summary>
        /// 三个链接按钮的按钮组名。同组按钮会被框架合并成一行显示。
        ///
        /// ⚠ 必须带模组名：AutomaticSettings 用一个 **static** 字典 s_ButtonGroups 按组名收集按钮
        /// （decompiled AutomaticSettings.cs:718-733），组名撞车会把别的模组的按钮并进同一行。
        /// 原版先例是拿 "声明类名.属性名_ButtonGroup" 当默认组名，我们因为要三个按钮同行必须显式给名，
        /// 那就自己保证唯一。
        /// </summary>
        internal const string kLinkGroup = "BridgeTheLanguageGapLinks";

        /// <summary>请我喝杯咖啡 —— 作者的 Ko-fi 页。</summary>
        internal const string kKoFiUrl = "https://ko-fi.com/yuexian7";

        /// <summary>论坛页面 —— 本模组的 Paradox Mods 讨论帖（与 PublishConfiguration.xml 的 ForumLink 同一条）。</summary>
        internal const string kForumUrl = "https://forum.paradoxplaza.com/forum/threads/bridge-the-language-gap-blg.1940349/latest";

        /// <summary>RAINBOW 官网 —— 作者这个模组系列的站点。</summary>
        internal const string kRainbowUrl = "https://rainbow-series-hvpma89wi25.qoder.zone/#top";
    }
}
