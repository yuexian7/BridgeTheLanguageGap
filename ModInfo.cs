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
        /// ⚠ 这是【本地待发布】的版本：Paradox Mods 上已发布的那版仍是 1.1.5，v1.1.6 起（含 v1.2.0 / v1.2.1）
        /// 全部只落在源码、本地 xml 与本机 Mods 目录 —— 用户每一轮都写明「改完还是先部署本地测试，
        /// 不要上传官方模组平台」。发布前后都别把这句话当旧的删掉，它是"这份号还没上线"的唯一现场记录。
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
        /// 1.1.7（2026-10-12，新会话，用户两条新需求）：
        ///   ① 自定义 AI 那八行的「思考强度」从一行下拉框改成两行 —— 上面一行只决定【发不发这个字段】
        ///      （选「不发送」时请求体里一个思考字段都没有，平台按它自己的默认档走，而多数平台的默认是
        ///      会思考、照常计费；选「发送」时下面才多出「档位」一行），下面一行决定【发哪一档】，
        ///      首档从 Off 改成 None（显式不思考），gpt 与 glm 给满七档（None → Max）。
        ///      四档直连大模型引擎（Gemini/Groq/OpenRouter/SiliconFlow） deliberately 不改：它们必须发一个
        ///      明确档位，给一个"不发送"的选项等于把玩家往坑里推。落盘仍用同一个键，空串 = 不发送，
        ///      所以不新增磁盘字段；老盘的 Off/Low/High 原样照读（Off → 发送+None，请求体一字不变）。
        ///   ② 引擎被服务端直接回绝（401/402/403/404：模型不存在、余额不足、key 失效）时改成【熔断】：
        ///      旧写法把熔断计数推到上限后仍让每条文本各发一次请求，一次坏配置就在几秒内刷出满屏
        ///      [ERROR]，而且一次 404 会把整批 12 条各判死（日志还说"退回单条重试"，那是句做不到的许诺）。
        ///      现在：开 30→60→120→封顶 300 秒的冷却（EngineKit.BreakerCooldownMs，纯函数、离线有断言），
        ///      期间一条请求都不发、文字全部留在队列里不烧重试预算、界面保留原文也不再挂「…」；
        ///      同类的失败日志按「引擎+状态码」60 秒内只认真记一条，判死日志同样节流。玩家点一下
        ///      「保存设置并翻译」/「测试」/「获取模型列表」/换引擎换厂商都会立刻解除熔断（ResetBackoff）。
        /// 1.1.6（2026-10-11，同一个会话，代码审查后的一轮稳定性/性能清理，无新功能、无行为变更）：
        ///   ① 保存/重新翻译时的「缓存预载」整表扫描 + 逐条 base64 解码移到后台线程：
        ///     此前它在主线程做，缓存攒到几万条时玩家每按一次快捷键就卡一下；扫描期间
        ///     玩家又点保存 ⇒ ClearStamp 变了，这批结果按代数作废（与
        ///     EngineKit.ResultStillWanted 同一判据），不会把旧目标语言的译文写进新设置；
        ///   ② 引导阶段（Bootstrap 跑在后台工作线程）不再跨线程直接读 GameManager /
        ///     localizationManager / activeDictionary（Unity/Colossal 原生对象跨线程硬读是
        ///     偶发、难复现的崩溃来源）：改为主线程每帧探一次、结果抄进托管静态字段，
        ///     后台线程只等那个字段；Patches.SyncSettings 里那行多余的 ActiveLocale 读取也删了
        ///     （守护每 2 秒已在主线程刷新它）；
        ///   ③ 「世界文字强制刷新」加 400ms 节流：保存/清除缓存/重新翻译都走它，而保存是
        ///     快捷键 0.5 秒可连按的，每一次都清三份渲染缓存 + 重烤 + 可能重读整张语言字典。
        ///     压得够近的两次合并成一次——【延后，绝不丢】（关掉翻译时玩家指望画面立刻回原文，
        ///     那一下不能吞），由 Tick() 在退出闸门【之前】补做；
        ///   ④ 热路径分配：LooksLikeKeyBinding 的 31 元素键名表 + F1~F12 提前建一份复用，
        ///     找键名改用 OrdinalIgnoreCase（省掉 ToLowerInvariant 那份字符串拷贝），
        ///     空格分隔键位串改就地扫描（省掉 Split 的数组）；BaseLang 加两层缓存 +
        ///     IndexOfAny 的临时数组提出来（目标语言/游戏语言在同一局里是同一份串，引用相等即命中）；
        ///   ⑤ 「获取模型列表」加单飞：连点不会同时起多个请求线程；
        ///   ⑥ HarmonyId 占位名 com.yourname.* 换成 com.yuexian.*。
        /// 1.2.0（2026-10-16，新会话，用户八条需求）：
        ///   ① 自定义 AI 里【玩家自填端点】那一家的思考强度从四档放开到七档（None → Max，与 gpt/glm
        ///     同集合）。其余 8 家内置厂商仍按各家真实集合收窄 —— 用户明确说"8家内置按真实集合来，
        ///     我说的是自定义里的自定义"。落点是 Setting.AiThinkPlain 与 CustomAi.ThinkingLevels 两处
        ///     （两边靠【名字】互转，加成员不换序号 ⇒ 老盘的 Low/High 原样读得回来）。
        ///   ② 选项页翻译改成【严格懒翻】：扫描器在页 → 分区 → 行三道都加"玩家看不看得见"闸，
        ///     判据全部照抄游戏自己那两格（OptionsUISystem 的 Page/Section.UpdateNameAndDescription
        ///     都先看 isVisible）。看不见的折叠分区、高级选项、同页其它标签页、鼠标还没移上去的说明
        ///     一条都不送；它们真的出现时（点开/移上去/切标签页）下一轮扫描补上。还原扫描
        ///     （清除缓存/关总开关）不看这几道闸，整棵树照旧退。
        ///   ③ 8 家自定义 AI 厂商各补一个「获取 API key」按钮（放在「获取模型列表」左边），
        ///     链接逐家核实过官方控制台（表在 CustomAi.KeyPage）；自填端点那家没有页、按钮不出现。
        ///   ④ 重要模组名恒显英文：Scope 的专有名词表收进 Anarchy / Move It / Skyve / Plop the
        ///     Growables 等一批（判据仍是整串相等，句中出现的同名不拦），提示词的「不翻清单」
        ///     也点名这一类名字 —— 白名单管整串、提示词管句中，两边合起来才不漏。
        ///   ⑤ AI 提示词按【六类翻译范围】分别写（Scope.Category：模组名/模组选项/资产名/资产描述/
        ///     世界标签/游戏本体），每类给各自的角色/语境/风格；批量只收同一范围的文本
        ///     （用户口径"不同范围的肯定不可能放在同一个请求里"），缓存仍按源文本共享。
        ///     公共的「不翻清单」（专有名词、快捷键、数字单位、占位符、路径、JSON/XML）单独一段，
        ///     六类共享 ⇒ prompt cache 前缀一致、命中率更高（用户口径"相同部分尽量一样"）。
        ///   ⑥ 测试按钮那颗探针改走瘦身提示词（一句"翻译这句话，直接回复译文"），
        ///     整套本地化规则一条都不带 —— 探针只回答"通不通"，不产出要上屏的译文。
        ///   ⑦ 上述提示词按游戏 12 种官方语言写母语指令段（AiPrompt.Instructions），
        ///     游戏语言是日语就连"不要翻专有名词"这条约束也是日语。
        ///   ⑧ 引擎设置与安全性复查。抓到并修掉两件事：a) 「获取模型列表」把带 key 的 URL
        ///     整个写进日志 ⇒ API key 明文落盘（新增 EngineKit.RedactUrl，日志/异常/节流键三处都过）；
        ///     b) Groq 默认模型 llama-3.1-8b-instant 已被官方 2026-06-17 下线（替代 gpt-oss-20b）⇒
        ///     新玩家装上就是 404。另补齐三条引擎说明的中英繁三语漂移（Yandex / Groq / DeepL 的代理提醒）。
        /// ⚠ 源码与测试表里那些写着「v1.0.5 需求1」「v1.0.5 第四条」的注解，指的都是【1.1.0 那一轮】。
        /// 1.2.1（2026-10-18，新会话，用户实机测完 v1.2.0 之后的四条反馈，仍然只部署本地不上传平台）：
        ///   ① 「提示词里怎么可以加 If the text is already in the target language, return it unchanged.？」
        ///     —— 玩家实测翻日语时个别文本直接把这句英文翻译上了屏。根因是指令太长、小模型把指令当内容。
        ///     整句删除，连带删掉「不翻清单」末尾那条同义的"原文已是目标语言"（本地 Scope.ShouldSkipLocal
        ///     在入队前就判掉了，模型侧不需要再知道）。并按用户口径【凡是本地已有判断的约束一律不写进提示词】：
        ///     键位串、数字/单位/测量值、文件与操作路径、花括号占位符（TransGuard 已换成私有区哨兵，
        ///     模型根本看不到花括号）四条全部移出，只留专有名词、markup/代码、私有区哨兵三条。
        ///     另在输出契约末尾补一句"不要把本指令的任何词写进译文"，正面挡这一次的事故形状。
        ///     批量正文里与 system 逐字重复的那一大段（母语指令 + 语境 + 不翻清单）也一并删掉：
        ///     批量正文现在只剩编号行，编号协议挪进 system 末尾且【只有批量请求带】（PromptScope 多钉一格）。
        ///   ② 「提示词不管游戏语言是啥都写成英文」—— 12 种官方语言的母语指令段（s_Instructions /
        ///     Instructions()）整块删除。要翻成哪种语言只看【目标语言】那一格（Target() 里写母语名，
        ///     简体中文 / 繁體中文 / 日本語 这类名字比英文名更不容易被模型认错）。
        ///   ③ 「还是没变啊，每个模组选项界面的所有文字都给我送去翻译了」—— v1.2.0 那一版押错了判据：
        ///     `Widget.isVisible => !m_Hidden` 只反映模组的 hidden() 委托，跟"在不在屏幕上"无关，
        ///     非当前标签页的控件建页时就全部存在、m_Hidden 是从没刷新过的残留值（实机凭据：他那一局
        ///     「选项行文字 85 处 … 玩家看不见的行跳过 2 处」，两道 isVisible 闸合计只拦住 2 处）。
        ///     换成游戏自己算好的那一格：`OptionsUISystem.m_WidgetBindings.children`（SelectSection 里
        ///     = section.GetItems(m_IsAdvanced)，搜索时 = 各页命中清单），它已经把【其它标签页 / 折叠分组 /
        ///     与简单-高级模式不符的项】全筛掉了；再加 m_ActiveSectionBinding 做分区循环的短路。
        ///     上下滚动不改这份清单 ⇒ 照玩家口径"滚动到也算看见"。换标签页/点开折叠分组会改变清单，
        ///     所以 PageKeyOf 的编码里补上标签页 id 与在屏控件数，Tick 才会立刻补扫那一屏。
        ///     说明（hover 才出现的那一半）当时的结论是"托管层没有 hover 凭据、只能等标题落地"，
        ///     那条妥协在 v1.2.2 被玩家否掉，口径见下面 1.2.2 一节，不再重复。
        ///     游戏渲染文本按玩家口径【保持原样】：进存档就整份送翻（世界标签那一路不加"看得见"闸）。
        ///   ④ 「检查每次请求的前缀有没有混入隐形时间戳或动态参数」—— 逐条查过三个 body 生成函数与
        ///     自定义 AI 的分发：整个 EngineKit.cs 没有任何 DateTime / TickCount / Random / 计数器（时间戳
        ///     只出现在日志行），提示词的六个变量位只有 目标语言 与 翻译范围，都是一局内固定的值。
        ///     人眼复查管不了下一版 ⇒ 加离线断言：同一套入参只换文本时，文本出现位置之前必须逐字节相同、
        ///     同入参连跑两次整个 body 必须一致、六个范围共用的前缀必须够长（tests/t3 的 prompt.audit /
        ///     prompt.prefix，六条通道各钉一行）。拼接顺序也因此定死：越通用越靠前。
        /// </summary>
        internal const string kVersion = "1.2.5";

        /// <summary>
        /// 1.2.5（2026-10-21，同一会话续测，仍然只部署本地不上传平台）—— 玩家两条反馈：
        /// ① 「测试的请求正文太啰嗦：system 已经写了 Translate the user's text…，正文又把指令抄一遍。
        ///    正文直接发要翻的内容；system 前两句固定、后一句 Translate into X 跟目标语言走，最大化缓存命中。」
        ///    ⇒ 三个协议（OpenAI 兼容 / Gemini / Anthropic）的 ProbeBody 统一改成
        ///      system = AiPrompt.ProbeSystemFor(locale)（固定两句话 + " Translate into 母语名."），
        ///      user   = 原文本身（AiPrompt.TestProbe(text)）。
        ///      计费口径同步换成这两段（CustomEngine.TestProbe 的 Tokens.Charge 与实发同源）。
        /// ② 「翻译好慢，调研沉浸式翻译为什么快；思考强度到底生效吗？发送/不发送、档位怎么调请求体都没变，
        ///    每个引擎都看一下，测试请求体也要跟设置走。」
        ///    ⇒ 先答第二个问题（实机凭据：作者设置是 厂商=custom、模型=free-b、VendorThinking_custom 为空；
        ///      即使把发送打开，旧 CustomField 也按模型名认代际，free-b 认不出 ⇒ 一个字段都不发）。
        ///      v1.2.5 起「自定义」那一格按玩家选的接口格式发通用写法（OpenAI reasoning_effort /
        ///      Anthropic adaptive+output_config / Gemini thinkingBudget），对方不认就由 PostLlm 的
        ///      400/422 剥字段兜底。八家内置厂商与四档直连引擎的模型名判据不动：那些是按各家官方文档
        ///      逐条核过的（gpt-4o 发 reasoning_effort、claude-3-5 发 thinking 都是 400）。
        ///    ⇒ 每次请求在日志里留一行 `[请求] 引擎/模型 用时=Nms 思考=…`，测试按钮另有一行
        ///      `[测试] 请求参数：… 思考=… system=N字 正文=N字`。三种"没变"从此一眼可分：
        ///      玩家关了发送 / 模型名认不出 / 发出去被 400 剥掉。
        ///    ⇒ 速度：按实机日志量了一遍（每请求 2~7 秒；17 条挤在一次请求里，10 秒后才一起上屏）。
        ///      沉浸式翻译快在【流式 + 多个小请求并发 + 只翻可见 DOM】，我们没有做流式（风险高、收益只在观感），
        ///      但"一次吞满 40 条"这一格确实是首屏慢的主因：队列有积压时一条 worker 拿满 40，
        ///      另外 4 条与后面几十条只能等这一大批生成完。本版加 BatchKit.ShouldLeaveForOthers：
        ///      手上有 12 条且队列还有活就让给别人；队列空了仍可攒到 40（没人抢，多带只省请求）。
        ///      ⚠ 这不是把玩家上一轮要的"多送几条"撤掉：40 的硬上限没动，变的只是"有人在等时先让路"。
        /// ③（2026-10-26，同版本补修，版本号不变）「思考档位选 none 时，接口依然有思考过程」—— 玩家看到的是
        ///    「我明明选了不思考，日志里那一行还是它在想」。三处一起改：
        ///    ⇒ 所有档位下拉的首档统一成 Off（八家厂商行原来是 None，四档直连引擎本来就是 Off）：
        ///      None 这个词从所有下拉里退场，只剩 ThinkLevel 成员留给老盘 —— ParseLevel 仍认 "none"，
        ///      IsOff 仍是 Off||None ⇒ 存过 None 的老存档行为一字不变（这一条是有断言钉死的）。
        ///    ⇒ 逐家复核"关"到底发什么（12 格里只有 2 格"不发才是不思考"，其余必须显式关）：
        ///      OpenAI/Grok/Groq/OpenRouter/custom-openai = reasoning_effort:"none"；
        ///      Claude/GLM/DeepSeek-v4/custom-anthropic = thinking:{type:"disabled"}；
        ///      Qwen/SiliconFlow = enable_thinking:false；Gemini = thinkingBudget 该代下限；
        ///      没有关闭档的就落那一家真实存在的最低档（Kimi K3、GLM 5.3 系、Gemini 3.x 全系、groq 的 gpt-oss）。
        ///      本轮补掉两条会 400 的形状：Grok 4.5/4.6/4.7 与 fast-reasoning 只收 low/medium/high(/xhigh)，
        ///      发 none 会被拒 ⇒ 关落 low；Gemini 3.7-flash 与 3.8 都不收 MINIMAL ⇒ 下限抬到 low。
        ///      ⚠ 八家内置 + 四档直连的模型名判据仍按各家官方文档，本轮只动上面这两条。
        ///    ⇒ 自填端点那一格（厂商选「自定义」）改成三态下拉：关闭 = 显式发不思考字段（哨兵词 "Disabled"，
        ///      openai/anthropic 协议发 thinking:{type:"disabled"}、gemini 协议发该代 thinkingBudget 下限）；
        ///      发送 = 按档位行发；不发送 = 一个字段都不发。落盘用 "Disabled" 当第三态（空串已被"不发送"占着），
        ///      读盘两侧对称，老盘的空串/档位词语义不变。
        /// ④（2026-10-29，同版本补修，版本号不变）「没按板块攒批：第一个板块 5 个设置项字数不多，应该把板块标题
        ///    ＋这 5 个设置项标题放在同一个请求，后面两个板块依次发送，下拉框候选项跟着该设置项的请求一起发」——
        ///    实机日志（17:11:40~17:12:23，Custom/free-work）6 次请求分别回 2/2/5/2/2/2 条，用时 11.4/11.7/2.2/
        ///    22.1/30.1/42.5 秒 ⇒ 请求条数≈文字条数的一半，5 条那一次只用 2.2 秒：慢在请求次数，不是再调窗口。
        ///    ⇒ 请求单位从「一条」升级成「一个板块（一个 section）」：扫描器走一棵 section 时开收集器
        ///      （TransMap.BeginSection），板块标题（Options.GROUP[…] 那几条被 Prefix 暂存起来的）先认领进来，
        ///      随后该 section 的选项行标题与下拉候选项按显示顺序收进同一条 string[]，BatchKit.ChunkUnit 按
        ///      CanBatch / MaxItems=40 / MaxTotalChars=1800 切块 ⇒ 一板块一请求，块内顺序＝界面顺序。
        ///    ⇒ TransMap 新增 Units（BlockingCollection<string[]>）+ Staged（Prefix 遇到 Options.* 先暂存 2.5 秒
        ///      等扫描器认领，没人认领就 FlushStaged 按单条入队，旧行为只是晚 ≤2.5 秒）；原有 Pending/PendingWorld/
        ///      HeadFront/HeadWorld 全部不动，Take 改成返回 string[]（单条包成 1 元素数组），取走一个单元按
        ///      unit.Length 记在飞、每条最终各还一次 ⇒ IsIdle 判据不被单元打破。
        ///    ⇒ 不按 group 细分（组序号有 simple/advanced 两列，选哪列要看私有字段 m_IsAdvanced ⇒ 宁可一页一块，
        ///      也不去拼一个可能不存在的 key）；非批量引擎（机翻/Cloudflare）拿到多元素单元会按正序拆回队头。
        ///    ⇒ 日志新形状：`[板块] 页:… 区… 收齐 N 条，一次请求送出（v1.2.5）。`
        /// </summary>

        /// <summary>
        /// 1.2.4（2026-10-19/20，同一天第四次续测，仍然只部署本地不上传平台）—— 三条反馈 + 一次当场纠偏：
        /// ① 「还是会有，我都清除缓存了，怎么还是有？难道这个还有记忆功能？清除缓存后所有的翻译痕迹和缓存都要删除的。」
        ///    ⇒ 症结在判据、不在缓存文件：1.2.3 那张"前端为哪个说明 id 取过词"的表
        ///      （DropdownL10n.s_DescRequested）当时按"只增不清"写，理由是"它记的是既成事实"。
        ///      但那张表【实际决定要不要送翻】⇒ 它本身就是一种翻译痕迹：玩家点完清除缓存、
        ///      回到那一页，先前 hover 过的行照样自动翻，他看到的就是"记忆功能"。
        ///    ⇒ 本版新增 DropdownL10n.ForgetHoveredDescriptions()，由 ClearCacheNow 紧跟
        ///      RevertAll("已清除缓存") 之后调一次，清表并复位"到顶"标志，日志记
        ///      「[下拉框] 清除缓存：一并抹掉 N 条说明悬停凭据（…）」。清完之后每条说明都要重新
        ///      移到眼前才送翻 —— 这正是玩家要的形状。排空仍只有这一处（§10 第 9 条：判据只一处）。
        /// ② 「这次测试发现翻译速度比改提示词版本前要慢很多，无论是机翻还是AI翻译。你再优化一下。」
        ///    ⇒ 先排掉"调参退步"：git diff c67faaf..v1.2.3 对 kWorkerCount / BatchKit.MaxItems /
        ///      ScanEnqueueBudget / 各种间隔【一个数都没动】（那一版只给 EngineKit 加了一个 max_tokens 字段）。
        ///      实测那一局 155 条待翻文字里 145 条走单条请求、批量请求整局只发出 1 次（10 条全对上）
        ///      ⇒ 不是批量协议不行，是它从没被喂饱过。根因两条都在"取货"：扫描器一行一行入队；
        ///      5 条 worker 全堵在 Take(500) 上、队列一出现第一条就同时醒各自抢走一条 ⇒
        ///      每条线程手上只有 1 条 ⇒ todo.Count < 2 ⇒ 走单条。旧写法还是非阻塞捞（超时 0），
        ///      捞不到就 break，"凑批"在实机上几乎从不发生。
        ///    ⇒ 本版把取货改成【有界等一小会儿】：手上还只有 1 条时，以 BatchKit.SliceMs(40ms) 为一片
        ///      反复捞，最多等 BatchWindowMs(250ms)；一凑到第二条立刻不再等（够走编号协议了）。
        ///      这不是拿延迟换批大小 —— 等的是"本来就排在队列里、马上就到"的那几条；
        ///      就算队列真空着也只多等四分之一秒，而单条路径本来就要一次网络往返。
        ///      ⚠ 这一等只发生在 Batchable=true 的那几家（Groq/OpenRouter/SiliconFlow/Custom/Gemini）；
        ///      MT 那几家 Batchable=false，根本走不到 TryBatch，耗时不受影响。
        /// ③ 「同一个请求送去翻译的条数是否可以多一点，而且尽量一个板块或多个板块整体送去翻译，
        ///    按顺序翻译下来视觉上比较舒服，不然实机都是这个板块部分设置项翻译好了然后另一个板块的
        ///    几个设置项好了，很乱。」
        ///    ⇒ 一半原因在 ② 那 145 次单条往返（一条一条回来，自然是东一格西一格）；另一半是
        ///      【不合批的条目原来被退回队尾】，等于把眼前这几行排到整屏之后。
        ///    ⇒ 本版新增 TransMap.ReturnToHead + HeadFront/HeadWorld 两张队头表，由 TakeFrom 优先取；
        ///      TryBatch 里不合群/不合范围的条目改走 ReturnToHeadOrDrop（代际判据与 RequeueOrDrop 相同，
        ///      只是退回位置从队尾改成队头）。它们本来就排在玩家眼前更前面（扫描自上而下走树），
        ///      退队头 ⇒ 下一个取到它的线程立刻按它自己的范围重新攒批 ⇒ 队列顺序与视觉顺序一致。
        ///      IsIdle / HasFrontWork / Reset() 三处同步（少一处就是真 bug，见 docs/硬约束总表.md 第 90 条）。
        ///    🔴 玩家当场纠偏：「我说的是设置项标题和设置项……这不包括设置项的说明，
        ///      说明依然还是鼠标移上去显示后才翻译」—— 我曾按"板块整片放行"实现了板块级凭据
        ///      （s_DescSections / NoteSectionTouched / DescriptionRequested(id, sectionKey)），
        ///      被这句话否掉，该实现【已整块删除】，源码里留了"已撤回，别再加回去"的留档，
        ///      fixtures AA3 段与 tests/t3 各钉一条红线断言。需求③ 的"整板块送翻"落在
        ///      【标题与候选项】那一路（它们本来就不看闸、按扫描顺序入队），与说明那一半无关。
        /// 顺手：BatchKit 的天花板从"条数"改成按"总量"钉 —— MaxItems 12→40、MaxChars 120→300、
        ///   新增 MaxTotalChars(1800) 与 Fits(usedChars, next)。条数本身不是危险项，把模型单次输出
        ///   额度撑爆才是（Anthropic 那一家 max_tokens 钉死 2048）；40 条短路名能带满，40 条长说明
        ///   会自动缩成几条 ⇒ 条数随文本长度自动收缩，而不是因为怕截断就一律不许攒。
        ///   ⚠ 40 这个数没有再往上试的依据：抬之前先看日志里「一批 N 条」对上是几条。
        /// ④（同日追加，版本号不变）「模组日志log文件只保留最近10次游戏的记录就行了（包括闪退，
        ///    只要游戏启动并记录下来就算），在退出游戏时自动把前面的记录删除。注意不要在游戏退出时
        ///    对安全性稳定性有影响。」
        ///    ⇒ 先回答玩家的疑问：**翻译缓存不在日志文件里**。两者是 ModsData\Cs2AutoTranslator\ 下
        ///      两个独立文件（translation.cache / Cs2AutoTranslator.log），源码里两条写盘链互不引用，
        ///      所以裁日志不会碰到翻译额度（缓存是不可再生资产，删了就是白烧玩家的钱）。
        ///    ⇒ 本版之前日志只是"超过 8 MB 归档、归档留 3 份"，是容量上界、没有"次数"概念
        ///      （本机实测那份攒了 112 次会话 / 3.26 MB）。本版按【次游戏】管：
        ///      LogKit（判据，放进 TextKit.cs 以便离线壳钉住）+ ModLog.TrimSessionsOnExit（I/O）。
        ///      数会话 = 数 `=== session start ` 开头且 `===` 结尾的行（必须从**行首**匹配：日志正文里
        ///      也会出现这几个词，而正文行首是 HH:mm:ss，天然不匹配）；从文件尾往前数第 10 个会话头
        ///      当切点（落在会话头上，不从半个会话中间切，否则最近一局会被砍掉前半截）。
        ///      闪退那一局也算一次，并落在**保留**的那一侧 —— 它正是玩家最想留的凭据。
        ///    ⇒ 稳定性（玩家点名要求）逐条收口：① 只碰 FilePath 这一份文件，不 glob、不碰 .log.bak /
        ///      translation.cache / 设置 json；② 与 ModLog.Write 共用 _lock（否则退出瞬间后台 worker
        ///      正在 AppendAllText 的那一行正好落在替换窗口里）；③ 异常全吞、不抛、不 Sleep、不重试 ——
        ///      任何失败都只是"这一局不裁"；④ 只挂 Application.quitting（ProcessExit 兜底那条【不】裁剪：
        ///      它可能在进程已拆一半时执行）；⑤ 写 *.tmp → File.Replace 原子替换，绝不在原文件上原地截断；
        ///      ⑥ 崩溃残留的 *.tmp 由 PruneOldLogs 开头回收（只删这一个确定名字）。
        ///    ⚠ 上一稿曾加"先 File.Copy 到 .old 当退路"，已删：本机实测（目标被 FileShare.None 独占）
        ///      File.Replace 抛 IOException 而**原文件逐字节未变**（第一步就失败，不存在中间态），
        ///      那次全文件拷贝反而是整条链上最容易失败的一步、且会留永不回收的垃圾。见 §10 第 91 条。
        /// ⚠ 本版不改 DropdownKit 的零依赖底线（不碰 Unity/Game/Colossal 类型、不加锁、不打日志）。
        /// </summary>

        /// <summary>
        /// 1.2.3（2026-10-19，同会话续测，仍然只部署本地不上传平台）—— 两条反馈：
        /// ① 「该API KEY消耗token数的数据不会实时刷新吗？我看了一下测试后数值完全没变，游戏重启后才变化。
        ///    如果只是测试这个请求没记进去，那就加进去。如果确实这个数值如果实时变化对性能有影响的话，
        ///    可以做成在翻译请求全部完成且没有新的请求动作时超过1秒后再去读取刷新。当然原来的方式对性能
        ///    没啥影响就不用改了。」
        ///    ⇒ 查下来是【测试这一次请求没记】：v1.2.2 及以前只有 CustomEngine.Translate 那条链
        ///      TranslatorSetting.AddTokens，测试按钮走的是 CustomEngine.TestProbe 这一条，压根没记，
        ///      所以"重启后才变化"是别的链把数补上去的假象。本版在 TestProbe 里补同一句记账，口径与
        ///      Translate 逐字相同（平台报了 usage 用真值、没报按字数估），喂进去的提示词是这一条真实
        ///      发出的那两句（AiPrompt.ProbeSystem + AiPrompt.TestProbe）—— 为此把散在三个 ProbeBody 里的
        ///      system 文案提成一个常量，谁改文案谁就同时改了计费，不可能漂移。
        ///    ⇒ 界面刷新【一行都不加】：选项行的 token 值是 LocalizedValueField（ReadonlyField 派生），
        ///      本模组那几个 Tok* 行没有 [SettingsUIValueVersion] ⇒ valueVersion 为 null ⇒
        ///      ReadonlyField.Update() 走轮询支路，每帧 GetValue() 比对，数字一变就自己标脏重发 props。
        ///      也就是说界面早就已经是实时的，玩家那条"1 秒后再刷"的性能妥协没有必要触发（判据见
        ///      tests/t3 的 token 段与 decompiled ReadonlyField.cs:41-64）。
        /// ② 「设置项的说明文本还是会一起翻译啊，根本不是鼠标移上去显示后才开始翻译，再仔细修复好。」
        ///    —— 这是这条反馈第三次被退回（v1.2.1「说明等标题落地」、v1.2.2「标题落地后的下一轮再排」
        ///    都是在改"延多久"，等的却是自己这条链上的下一个状态），所以本版换的是【等什么】：
        ///    · 一手证据（前端 Content\Game\UI\index.js，逐字核过选项页那一段）：设置行的说明不是
        ///      "随每个控件一起发出去、只是 CSS 藏着"，而是【整块信息栏只在有一行被移到眼前时才渲染】，
        ///      且只渲染那一行的说明 —— `E && … K4.infoColumn, children: [ infoTitle: props.displayName ? …
        ///      : <Sc.Options.OPTION hash={E}/>, infoDescription: props.description ? …
        ///      : <Sc.Options.OPTION_DESCRIPTION hash={E}/> ]`，其中 E 是该行选中项的 id，`<yd>`
        ///      （Localized）带 id 时在渲染那一刻调 translate(id, fallback)。
        ///    · 托管侧因此【确实有】hover 凭据：那一调进 cohtml director → UILocalizationManager.Translate
        ///      → 本模组的 Harmony Prefix。实机日志里 5 行「[Hook] 前缀已生效，首次入队待翻译文本：
        ///      '…'（key=Options.OPTION_DESCRIPTION[…]）」就是它；同期 358 条说明 id 里 352 条都是
        ///      Options.OPTION_DESCRIPTION[…]（其余 6 条是模组硬塞的裸文字，id 为 null）。
        ///    · 本版改法：Prefix 把"前端为哪个说明 id 取过词"记进一张只增不删的集合
        ///      （DropdownL10n.NoteKeyRequested / DescriptionRequested），扫描器送说明前先问一句
        ///      DropdownKit.HoldDescription —— 没取过词就按住（日志记「说明等悬停（前端取过词才送）」）。
        ///      也就是说判据从"我们这条链走到哪一步"换成了"前端真为这一行取过词"，与前两版不是同一个形状。
        ///    · 仍然说明白的边界：id 为 null 的裸文字说明（版本字符串一类，实机 6/358）前端从不取词，
        ///      按不住也不该按（等下去就是永远不翻），所以闸只对带 id 的说明生效；键盘焦点那一支
        ///      （前端 `x = g && r ? u ?? c : p`）也会渲染出说明，同属"移到眼前"，一并算数。
        /// ⚠ 本版不改 DropdownKit 的零依赖底线（不碰 Unity/Game/Colossal 类型、不加锁、不打日志），
        ///   悬停凭据表因此挂在 Mod.cs 那一侧（那里本来就有锁与日志）。
        ///   ⚠ v1.2.4 起"只增不删"这半句被实机推翻并由 ForgetHoveredDescriptions 改掉 ——
        ///   下面这段是 1.2.3 当时的原话，留作历史凭据（别照着它改回去）。
        /// </summary>

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
