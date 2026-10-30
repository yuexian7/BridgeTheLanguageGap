using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Colossal.Core;
using Colossal.Localization;
using Colossal.Logging;
using Game.Modding;
using Game.SceneFlow;
using Game.UI.Localization;
// v1.1.3：选项行的标题与说明挂在 NamedWidget 上。用别名而不是整条 using ——
// Game.UI.Widgets 里有个 Label/Button 之类与本项目里其它名字撞车的类型，整条引进来会把编译搞脏。
using NamedWidget = Game.UI.Widgets.NamedWidget;
// v1.2.0 需求2：懒翻的判据是 Widget.isVisible（游戏自己刷新标题/说明时看的就是它）。
// 单独引这一个接口名，仍然不整条 using Game.UI.Widgets（理由同上）。
using IVisibleWidget = Game.UI.Widgets.IVisibleWidget;
using HarmonyLib;
using cohtml.Net;
using TranslationData = cohtml.Net.ILocalizationManager.TranslationData;
using UnityEngine;

namespace Cs2AutoTranslator
{
    // 都市天际线2 原生模组入口。官方 IMod 契约只有两个方法：OnLoad(UpdateSystem) 与 OnDispose()
    // （从 Game.dll 元数据实测得到，不是旧教程写的 Load(LoadMode)/Unload()）。
    // 注意：ModManager 要求一个程序集里【有且仅有一个】公开的 IMod 实现，多一个就报 IsNotUniqueWarning，
    // 所以原先给 ModSetting 当空壳用的 FakeMod 已移除，设置类改用本类实例来构造。
    public class Mod : IMod
    {
        internal static ILog Log;
        // 供 TranslatorSetting.Register 用真实的 IMod 构造 ModSetting（OnLoad 之后、主线程上才有值）。
        internal static Mod Instance { get; private set; }
        private static Thread _worker;
        private static volatile bool _shutdown;
        private static volatile bool _quittingSeen;   // Application.quitting 是否已触发（本游戏裁剪了 isQuitting，故自己记）。
        private static Guid? _updaterGuid;
        private static volatile bool _uiDirty;
        private static int _lastRefreshTick;

        public void OnLoad(Game.UpdateSystem updateSystem)
        {
            Log = LogManager.GetLogger($"{nameof(Cs2AutoTranslator)}.{nameof(Mod)}").SetShowsErrorsInUI(false);
            // Application.persistentDataPath 只能在主线程取；OnLoad 正好在主线程，先算好存起来，
            // 之后后台线程一律走 ModPaths.DataDir，不再触碰任何 Unity API。
            ModPaths.Init(Application.persistentDataPath);
            // Scope 只认委托：反射 ModSetting.instances 与 UnityEngine.Time 都留在 RegisteredModIds（Mod.cs 离线不可加载）。
            // 这行接线漏了不会报错，只会让「模组选项及说明」范围静默失效，所以 PersistSettingsOnly 里打了一行 id 数量日志。
            Scope.SetModIdProvider(RegisteredModIds.Get);
            ModLog.Attach(Log); // 需求8：本模组专属日志文件（同时仍转发给游戏自己的日志系统）。
            ModLog.Info("=== 拯救语言不通（都市天际线2 自动翻译）已载入 (v1.2.5：测试请求正文只剩原文、system 固定前缀+目标语言；自定义端点的思考强度现在真的会改请求体（模型名认不出也按接口格式发，400 自动剥掉重发）；每条请求日志带用时与实际思考字段；攒批有积压时先拿 12 条让并发跑起来，首屏不再等一整批 40 条) ===");
            Instance = this;
            _shutdown = false;
            _quittingSeen = false;
            // v0.28 那套退出收口机制原样保留：IMod.OnDispose 是官方的正式拆除入口
            // （已由 Modding.log 里的 "Disposed ..." 行实证确实会触发），但硬退出/崩溃时未必走到，
            // 所以 Application.quitting 与 ProcessExit 仍作为兜底一起挂着。
            // BeginShutdown 内部有 _shutdown 幂等判断，多路触发不会重复执行。
            Application.quitting += OnUnityQuitting;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => BeginShutdown("ProcessExit");
            // 所有耗时/联网/等待都放后台线程：既不卡主线程，也不受玩家循环和开机 UnpatchAll 影响。
            // 反馈2：以前只有【一条】工作线程，一次只能有一个请求在飞（一批最多 12 条），
            // 玩家连点几个界面就会看到「文字都翻好了但排在后面等半天」。现在是 kWorkerCount 条并行。
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Cs2AutoTranslatorWorker1" };
            _worker.Start();
            ModLog.Info("[线程] 后台工作线程已启动（并发 " + kWorkerCount + " 条 × 每请求最多 " + BatchKit.MaxItems + " 条）。");
        }

        // 反馈2：同时在飞的网络请求数。3 条是 2026-09 那轮定的；v1.0.5 玩家报「整屏文本要等半天」，抬到 5。
        // 为什么是 5、以及每批条数后来为什么改了（v1.2.4，玩家报"翻译比改提示词之前慢很多"）：
        // · 加线程不减少总请求数，只是让它们重叠 —— 收益接近线性，直到撞上限流；真限流由全局退避兜住
        //   （CooldownMs / 连续失败计数），要防的是"5 条一起撞墙后各自退避一遍"。
        // · 上一版这里写着「每批条数不敢跟着抬，BatchKit.MaxItems 仍是 12」。那个顾虑本身没错
        //   （编号协议要求引擎把「1. 文本 / 2. 文本」逐行原样回出来，越长越容易有一格对不齐而整批作废），
        //   但它【不是】那一局慢的原因 —— 实测 155 条里 145 条走的是单条路径，批量请求整局只发出 1 次。
        //   真正的原因是取货：非阻塞捞 + 5 条线程一起被第一条唤醒 ⇒ 每轮手上只有 1 条 ⇒ 根本凑不成批。
        //   所以 v1.2.4 改的是取货（TryBatch 里有界等一小会儿，见 BatchWindowMs 那段），
        //   条数上限同时抬到 40，并用【字符总量】BatchKit.MaxTotalChars 兜住"长文本撑爆 max_tokens"，
        //   让批量条数随文本长度自动缩，而不是因为怕作废就一律不许攒。
        // · 线程越多也越可能把同一屏的文字拆成几批各翻各的；5 条差不多是这条收益曲线的拐点。
        //   要回退只需改回 3，日志第一行照实报并发数。
        private const int kWorkerCount = 5;

        private static void WorkerLoop()
        {
            if (!Bootstrap()) return;

            // 引导只在这条线程上做过一次，此刻才允许其余 worker 进场：
            // 它们全都直接进 TranslateLoop，不再碰注册界面/打补丁这些「一局只能做一次」的事。
            for (int i = 1; i < kWorkerCount; i++)
            {
                var t = new Thread(TranslateLoop) { IsBackground = true, Name = "Cs2AutoTranslatorWorker" + (i + 1) };
                t.Start();
            }
            TranslateLoop();
        }

        // 一次性引导：等管理器 → 主线程注册选项界面 → 载缓存/预填译文 → 打 Harmony 补丁。
        // 返回值 false = 引导失败（本局不再翻译），调用方应直接退出线程。
        private static bool Bootstrap()
        {
            try
            {
                // 1) 等本地化管理器就绪（v1.1.6：改为主线程探针 —— Bootstrap 跑在后台工作线程，
                //    此前在这里直接读 GameManager/localizationManager/activeDictionary，
                //    而 Unity/Colossal 原生对象跨线程硬读是偶发、难复现的崩溃来源）。
                _probing = true;
                ProbeLocalizationOnMainThread();
                if (!WaitFor(() => Patches.LocalizationReady, 120))
                { _probing = false; ModLog.Error("[线程] 超时：没等到本地化管理器。"); return false; }
                _probing = false;

                // 2) 在主线程注册官方选项界面（RegisterInOptionsUI 必须在主线程）。
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    try { TranslatorSetting.Register(Instance); ModLog.Info("[选项] 已在主线程注册官方选项界面（12 语言跟随游戏语言）。"); }
                    catch (Exception ex) { ModLog.Error("[选项] 注册异常: " + ex); }
                });

                // 3) 等选项注册完成（字典有内容已由第 1 步的主线程探针抄进 Patches.LocalizationReady）。
                if (!WaitFor(() => TranslatorSetting.Instance != null && Patches.LocalizationReady, 120))
                { ModLog.Error("[线程] 超时：选项界面或本地化字典未就绪。"); return false; }

                // 4) 载入磁盘译文缓存并把「当前引擎+目标语言」命中的历史译文预载进 TransMap（需求9a：
                //    重启后选项译文立即显示、不再逐条「…」重翻），再把设置镜像到热路径静态字段（SyncSettings）。
                TranslationCache.Load();
                Patches.SyncSettings();
                SeedCacheIntoTransMap();

                // 5) 在主线程订阅按钮 + 打 Harmony 补丁（延迟到游戏就绪，规避开机 UnpatchAll）。
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    TranslatorSetting.OnTestClicked += RunTest;
                    TranslatorSetting.OnSaveClicked += SaveNow;
                    TranslatorSetting.OnEnabledChanged += SaveNow; // 需求6：切换总开关即即时生效（关→回原文，开→重翻），无需再点保存。
                    // 反馈1：6 个范围开关与引擎下拉框也即时生效 —— 勾上立刻翻、取消立刻回原文，不再必须点保存。
                    // 刻意复用 SaveNow 而不是新写一条流水线：两者要做的事完全相同（写盘 + 同步热路径镜像 +
                    // 按新设置重解析 + 刷界面 + 清图层缓存），分两条路迟早一条改了另一条忘。
                    TranslatorSetting.OnLiveSettingChanged += SaveNow;
                    // 反馈3：换 AI 厂商走轻路径 —— 只存盘 + 重刷界面，把新厂商那三行输入框/用量行换出来。
                    // 不清译文表也不重翻：厂商只决定【之后】的请求往哪个端点发，已翻好的文字跟厂商无关，
                    // 为此重翻一遍既烧积分又让玩家等。
                    TranslatorSetting.OnVendorChanged += VendorSwitched;
                    // 反馈1（本轮更正）：换引擎同样走轻路径。玩家选引擎是给【下一句】用的，
                    // 不是让模组立刻把整张界面重翻一遍——那既烧额度又让玩家干等。
                    TranslatorSetting.OnEngineChanged += VendorSwitched;
                    TranslatorSetting.OnClearCacheClicked += ClearCacheNow;
                    TranslatorSetting.OnOpenLogFolderClicked += OpenLogFolderNow;
                    TranslatorSetting.OnOpenRegisterPageClicked += OpenRegisterPageNow;   // 需求2：跳转该引擎的注册页
                    TranslatorSetting.OnOpenApiKeyPageClicked += OpenApiKeyPageNow;       // v1.2.0 需求3：跳转该厂商的 API key 页
                    TranslatorSetting.OnFetchModelsClicked += FetchModelsNow;             // 需求2：拉取可选模型列表
                    Patches.Apply();
                    // 注册「补丁守护」updater：每帧节流检查，被别的全局 UnpatchAll 拆掉就自动重打 + 重刷界面 + 周期存盘。
                    // 用游戏自己的 MainThreadDispatcher（非 Harmony），不受 UnpatchAll 影响。
                    _updaterGuid = MainThreadDispatcher.RegisterUpdater((Func<bool>)Patches.EnsureApplied);
                });

                // 6) 进入常驻的「按需翻译」循环。
                ModLog.Info("[懒翻译] 后台翻译循环启动：玩家实际看到的、勾选范围内的、非目标语言文字会被逐条自动翻译，实时生效、无需重启。");
                return true;
            }
            catch (Exception ex) { ModLog.Error("[线程] 未处理异常: " + ex); return false; }
        }

        // 常驻循环：消费 Prefix 入队的「源文本」，自动检测源语言→翻成目标语言，填进 TransMap + 磁盘缓存。
        private static void TranslateLoop()
        {
            int lastMissingWarn = 0;   // 「缺必填项」警告的节流：这个分支在队列非空时会被反复走到，不节流就刷屏。
            while (!_shutdown)
            {
                // v0.28：落盘全部由本后台线程做【增量追加】（攒够一批或最晚 5 秒一次），主线程完全不碰缓存磁盘 I/O。
                TranslationCache.Flush(false);
                // 反馈2：撞墙时几条 worker 一起歇，别让另外几条接着去撞同一堵墙。
                // v1.1.4：这一等从此【分档】—— 前台（界面文字）还有活的时候只等前台那一档，
                // 世界文字（路名/区名）那场洪水造成的限流不许把玩家眼前那一屏冻住 20 秒。
                WaitForCooldown(TransMap.HasFrontWork);
                // v1.2.5 修复：Take 现在返回【请求单元】—— 扫描器收齐的一个板块一次取走（≥2 条），
                // 单条路径拿到的仍是长度 1 的数组。为什么让两种投递共用一个出口、而不是另开一条取货函数：
                // 单条那一路要的东西一个不少（src / 范围 / 代际 / 世界档），只是条数变了；
                // 共用出口才不会出现"单元那条路忘了还账"的半套实现（在飞计数是逐条借还的）。
                TransMap.FlushStaged(TransMap.StageTtlMs);   // 扫描器没来认领的暂存文字（选项面板没开/那一页没扫到）到期回落成单条
                string[] unit = TransMap.Take(500);
                if (unit == null) { RefreshUi(true); continue; } // 队列空闲：把还没刷出去的译文一次性刷到界面。
                string src = unit[0];                             // 单元的身份就是第一条：范围/代际/世界档都按它算

                // v1.2.0 需求5：这条文字的【翻译范围】跟着它一路走到请求里。入队时记下的快照
                // （TransMap.ScopeOf），Loop 在这里交给引擎 —— AI 引擎用它选提示词语境，
                // 机翻引擎不看它（它们的请求体里没有"语境"这一说）。
                Scope.Category scope = TransMap.ScopeOf(src);

                // v1.1.5：出发时抄一张"清代代际戳"（判据 EngineKit.ResultStillWanted）。
                // 这一条请求要在网上跑 0.1~8 秒，而玩家随时可能在这几秒里点下「清除缓存」——
                // 那时候刚被清空的内存表、刚被删掉的 translation.cache 文件、还有那两条刚排空的队列，
                // 都会被这条回来的结果重新填上。旧写法只防了「请求期间换了目标语言」那一格
                //（TranslatorSetting.Instance?.TargetLocale != target），没有"出发之后被清过"这一格。
                int gen = TranslationCache.ClearStamp;

                // 这一条走的是哪一档（与它进的是哪条队列同一本台账，见 TransMap.QueueFor）。
                bool world = TransMap.IsWorldSource(src);

                TranslatorSetting setting = TranslatorSetting.Instance;
                if (setting == null) { RequeueUnit(unit, gen); Thread.Sleep(500); continue; }

                string target = setting.TargetLocale;
                ITranslationEngine engine = TranslationEngines.Get(setting.TranslationEngine);
                if (!engine.IsConfigured(setting))
                {
                    // 必填项没填齐：不显示待翻译标记（免得永远挂着省略号），退回队列等用户填好。
                    // 顺带把「缺哪一项」写进日志 —— 14 个引擎缺的东西各不相同，光看界面回原文猜不出原因。
                    int now = Environment.TickCount;
                    if (now - lastMissingWarn > 20000)
                    {
                        lastMissingWarn = now;
                        ModLog.Warn($"[引擎] 「{engine.Name}」还缺必填项：{TranslationEngines.MissingField(setting) ?? "?"}。" +
                                    "已暂停翻译并在界面保留原文；去选项里填好后点「保存设置并翻译」。");
                    }
                    Patches.MarkerEnabled = false;
                    RequeueUnit(unit, gen);
                    Thread.Sleep(1500);
                    continue;
                }
                Patches.MarkerEnabled = true; // 引擎可用：待翻译文字显示「…」标记，翻好后原地消失。

                // v1.2.5 修复：单元只对【能攒批】的引擎有意义。机翻那几家 Batchable=false，本来一次翻一条，
                // 把整块混进它们的路径只会多一次失败机会 —— 拆回单条、退回队头（顺序不变）逐条送。
                if (unit.Length > 1 && !engine.Batchable)
                {
                    for (int i = 0; i < unit.Length; i++) TransMap.ReturnToHead(unit[i]);
                    continue;
                }

                // 反馈7：能攒批的引擎先攒批 —— 一个视野里几十条路名/区名，逐条请求就是几十次网络往返。
                // TryBatch 返回 false 表示「这次没凑够两条」或「src 命中了缓存不必发」，交回下面的单条路径。
                // v1.2.5 修复：unit.Length >= 2 时整块交给 TryBatch（seed），它不再等 250ms 窗口 —— 一个板块一次请求。
                if (engine.Batchable && (unit.Length > 1 || BatchKit.CanBatch(src)) && TryBatch(setting, engine, target, src, world, gen, unit)) continue;

                try
                {
                    // 命中磁盘缓存：不再联网。缓存里 value==src 表示「已知无需翻译」。
                    // ⚠ 这一格【不加】代际戳闸门，是有意为之：清代会把内存表与文件一起删干净，
                    // 所以现在还能命中的条目只能是"清代之后新写进去的"，那正是玩家要的当前内容。
                    string cached = TranslationCache.Get(engine.CacheName, src, target);
                    if (cached != null)
                    {
                        if (cached == src) TransMap.ResolveAsSkip(src);
                        else TransMap.SetTranslation(src, cached);
                        ResetFailStreak(world);
                        _uiDirty = true;
                        RefreshUi(false);
                        continue;
                    }

                    // 联网翻译：源语言一律 auto（自动检测），目标语言=用户设置。
                    // 需求5：送引擎前把 {占位符} 掩成哨兵，尽量让引擎别翻花括号内部；回来后还原。
                    string[] guardTokens;
                    string masked = TransGuard.Mask(src, out guardTokens);
                    // v1.2.0 需求5：范围跟着这条请求走（AI 引擎据此选六套提示词语境之一；机翻引擎不读它）。
                    // v1.2.1 需求1④：这一条是【单条】请求，形状也要钉进去（batch=false），
                    // 否则同一条 worker 线程刚跑完批量、线程那一格还留着真，提示词就带上编号协议了。
                    // ⚠ Clear 放 finally：Translate 内部自己吞异常返回 null，但 PostLlm 之外的
                    //   NullReference 这类意外一旦抛出，粘住的那一格会污染后面【每一条】请求。
                    PromptScope.Set(scope, false);
                    string raw;
                    try { raw = engine.Translate(setting, masked, "auto", target); }
                    finally { PromptScope.Clear(); }
                    // 反馈1/2：紧跟着把这次的 HTTP 状态取走（线程私有，取完即清）。失败还是成功都要取，
                    // 否则上一轮的 429 会挂在线程上，被下一轮无关的失败当成原因。
                    // v1.1.2：同一次还要取走"是不是连接层就没通"，两个值是同一次失败的两半，必须成对消费。
                    int status = EngineBase.ConsumeStatus();
                    bool connFail = EngineBase.ConsumeConnectFailure();
                    string trans = TransGuard.Unmask(raw, guardTokens);

                    // 翻译途中用户可能改了目标语言：结果作废，退回队列按新目标重翻。
                    if (TranslatorSetting.Instance?.TargetLocale != target) { RequeueOrDrop(src, gen); continue; }

                    // v1.1.5：请求在外面跑的时候玩家点了「清除缓存」/改了设置 ⇒ 这一条结果已经不是他要的了。
                    // 不回写内存表、不回写磁盘（那正是"清了又长回来"的那只手）、也不退回队列
                    //（TransMap.Reset 刚把 WorldSources 清空，此刻的 Requeue 会把路名错投进前台那一队）。
                    if (!EngineKit.ResultStillWanted(gen, TranslationCache.ClearStamp)) { DropStale(src); continue; }

                    if (string.IsNullOrEmpty(trans))
                    {
                        // 引擎无返回：分流见 OnFailure —— 限流/连接不通不烧这条文本的预算，文本自身翻不成才按次数判死。
                        OnFailure(src, status, connFail, world, gen, engine.Name);
                        continue;
                    }

                    ResetFailStreak(world);
                    FinishOne(src, trans, target, engine, gen);
                    _uiDirty = true;    // 无论翻成还是判定跳过，都要刷新界面（去掉「…」标记或换上译文）。
                    RefreshUi(false);   // 节流：突发翻译时最多每 UiRefreshIntervalMs（900ms）刷一次可见文字。

                    Thread.Sleep(PaceMsFor(world)); // 轻微节流，别刷爆免费额度/被限流（撞过 429 之后这一档会自动变长，见 SlowPace）。
                }
                catch (Exception ex)
                {
                    // 异常同样按「不确定」处理，但一样有次数上限（需求9d 只要求「不因一次抖动判死」，不要求无限重试）。
                    ModLog.Error("[懒翻译] 处理异常（稍后重试该条）: " + ex.Message);
                    OnFailure(src, EngineBase.ConsumeStatus(), EngineBase.ConsumeConnectFailure(), world, gen, engine.Name);
                }
            }
            TranslationCache.Flush(true);   // 正常退出循环：把攒下的增量条目强制写干净。
            ModLog.Info("[懒翻译] 后台循环退出。");
        }

        // ===== v1.1.5：清代期间回到本线程的结果怎么处置 =====
        // 两条动作，都只涉及【这一条文本】，不动全局那几笔账（退避/熔断/间距是服务端与本机的事实）：
        //   · RequeueOrDrop：还要 ⇒ 退回队列；出发之后被清过 ⇒ 直接丢弃。
        //     为什么不退回：TransMap.Reset() 刚把两条队列排空、把 WorldSources 那本台账清空，
        //     此刻的 Requeue 等于把旧活塞进【刚清空的新队列】，而且路名会被错认成界面文字挤进前台那一队
        //     —— 那是 v1.1.4 好不容易分开的两档，不该被一次点击搅回去。
        //   · DropStale：结果已经拿到了，但内存表与磁盘都不能写（判据见 EngineKit.ResultStillWanted）。
        //     界面上这一处不会就此丢掉：下一轮扫描 / 下一次取词会按【当前设置】重新入队，
        //     所以"丢弃旧结果"的净效果是"按玩家此刻要的语言重翻一遍"，额度上是多花一次，
        //     但语义正确 —— 而"清过的缓存不该自己长回来"是玩家明确表达过的期望。
        private static void RequeueOrDrop(string src, int gen)
        {
            if (EngineKit.ResultStillWanted(gen, TranslationCache.ClearStamp)) { TransMap.Requeue(src); return; }
            DropStale(src);
        }

        // v1.2.5 修复：整块退还。单元取走时一次性记了 N 笔在飞，只退第一条会让其余 N-1 条永远挂着
        // （IsIdle 从此为假 ⇒ 界面等不到"真的排空了"那次刷新）。
        // 正序退还：Requeue 是【队尾追加】，正序追加后出队顺序与原文显示顺序一致。
        private static void RequeueUnit(string[] unit, int gen)
        {
            if (unit == null) return;
            for (int i = 0; i < unit.Length; i++) RequeueOrDrop(unit[i], gen);
        }

        private static int _lastStaleLog;

        // ===== v1.2.4 需求3：不合群的条目退回【队头】而不是队尾 =====
        // 与 RequeueOrDrop 的差别只有一处：退回位置。攒批时被捞出来又塞不进去的那些（范围不同、
        // 太长、带换行），本来就排在玩家眼前更前面的位置（扫描自上而下走树），退回队尾等于让
        // "此刻屏上那几行"排到整屏之后去 —— 玩家这一轮的原话正是"按顺序翻译下来视觉上比较舒服"。
        // 退回队头 ⇒ 下一个取到它的线程立刻按它自己的范围重新攒批 ⇒ 队列顺序与视觉顺序一致。
        // 代际判据与 RequeueOrDrop 逐字相同（EngineKit.ResultStillWanted）：清过缓存就不再往回放。
        private static void ReturnToHeadOrDrop(string src, int gen)
        {
            if (EngineKit.ResultStillWanted(gen, TranslationCache.ClearStamp)) { TransMap.ReturnToHead(src); return; }
            DropStale(src);
        }

        private static void DropStale(string src)
        {
            TransMap.DropStale(src);
            int now = Environment.TickCount;
            if (now - _lastStaleLog < 8000) return;      // 一次清代会连着回来好几条：说一次就够，别刷屏
            _lastStaleLog = now;
            ModLog.Info("[清代] 玩家点「清除缓存」/按「重新翻译」的那一刻还有请求在路上：累计已丢弃 "
                        + TransMap.StaleDropped + " 条结果。它们没有写回内存译文表、也没有写进 translation.cache，"
                        + "所以清完之后缓存不会自己长回来；这些文字下一轮会按当前设置重新排队。");
        }

        // 反馈7：一条文本失败之后的统一处理 —— 决定退避多久、要不要收紧重试预算。
        // 旧写法是 Math.Min(30000, 500*failures) 且无限重排队：端点一挂（401/429/超时），
        // 整条队列每 30 秒才挪一步，几十条路名就是十几分钟，界面也一直等不到「首批翻完」的刷新。
        // 后来改成：单条最多 3 次；连续失败满 12 次判定端点死了，改成每条只试一次 + 歇 5 秒。
        //
        // 反馈2：并发 3 条线程之后，这两个计数必须是【全局】的，不能各数各的 ——
        // 三条各自数到 12 才熔断等于要撞 36 次墙；退避若只让撞墙那条歇，另外两条照样去撞。
        // 反馈1：同时按 HTTP 状态码分流，因为「被限流」和「这台账号根本用不了」是两回事：
        //   429 → 只是这一刻太挤，整句仍然要翻，退避后照原预算重试，绝不判死；
        //   401/402/403/404 → 账号或模型层面的硬结论（实测硅基流动的 402 余额不足、OpenRouter 的 404
        //   「此模型无免费档」都被旧逻辑当成网络抖动重试了一整轮），立刻进入熔断档位并说清原因。
        private const int DeadEndpointAfter = 12;
        private const int RateLimitCooldownCapMs = 20000;   // 连续限流时的封顶档（就是 v1.1.3 以前那一档）
        private const int ConnectCooldownMs = 12000;

        // ===== v1.1.7：账号级回绝（401/402/403/404）的熔断 =====
        // 为什么要单独一条路（2026-10-12 那一局的实测形状）：模型名不存在 / 余额不足 / key 失效这一类失败，
        // 旧写法是「把熔断计数直接推到上限，然后每条文本照旧各发一次请求」—— 于是一次坏配置在几秒内刷出
        // 满屏 `[ERROR] [引擎] 请求失败`，每条还各歇 5 秒，玩家看到的就是「第一次测试/翻译就刷一屏错」。
        // 这些失败的共同点是【换一条文本、立刻再试都不会变好】，所以正确做法是整条链先停下来：
        // 歇 30 秒 → 60 秒 → 120 秒 → 封顶 5 分钟，期间一条请求都不发，文字全部留在队列里（不烧重试预算）。
        // 玩家修好 key/模型/引擎后点一下「保存设置并翻译」，这条链立刻解除（PersistSettingsOnly → ResetBackoff）。
        private const int EngineBreakerCapMs = 300000;
        private const int BreakerLogEveryMs = 60000;      // 同一档位的熔断日志每分钟最多一条
        private static int _breakerStreak;                // 连续第几次被账号级回绝
        private static volatile int _breakerLogStreak;
        private static int _breakerLogTick;

        // ===== v1.1.4：退避记账【分前台 / 世界两档】 =====
        // 为什么必须分：世界文字（路名/区名）天然是一大坨 —— 进一趟存档动辄几千条，全挤在同一条链上
        // 撞限流。而 0.5 秒前刚把退避做成"全局一份"的后果就是玩家 2026-10-02 报的那句
        // 「开启翻译后很久都没翻译」：背景洪水造成的 429 把玩家眼前那一屏一起冻住 20 秒。
        // 规矩是用户定的那条：背景活永远不许停前景活。前台（界面文字）只等自己的账，
        // 世界档等 max(自己, 前台)（世界本来就排在后面，多等一会儿毫无影响）。
        private const int FRONT = 0;
        private const int WORLD = 1;
        private static int Tier(bool world) { return world ? WORLD : FRONT; }

        private static readonly int[] _failStreak = new int[2];
        private static int _cooldownUntil;          // 前台冷却终点
        private static int _worldCooldownUntil;     // 世界冷却终点
        private static int _rateStreak;             // 连续第几次撞 429 ⇒ 决定歇多久（见 EngineKit.RateLimitCooldownMs）

        // v1.1.2 自适应节流：撞限流就把【请求间距】拉长（120 → 400 → 900 → 1200 封顶），连续翻成一批后再降回来。
        // 为什么不是"加/减线程"：并发数是线程数，决定撞不撞墙的是【同一个 key 在单位时间里的请求次数】。
        // 旧写法是"5 条一起歇 20 秒，再以原速一起撞回去"，等于每 20 秒精确重现一次同一场限流风暴
        // （2026-10-01 15:08:36 就是这个形状：开局一秒内三条 429，接着一整屏文字各烧掉一次重试机会）。
        // 间距自适应治的正是这个，而且对不需要限流的引擎毫无代价（连续成功 20 条就提速回 120ms）。
        // v1.1.4：这一档也按【前台 / 世界】分开。同一条免费 key 上，世界洪水会把共享的间距顶到 1200ms，
        // 于是界面文字也按 1200ms 一条地走 —— 50 条就要 20 秒，那还是玩家眼里的"半天没动静"。
        private static readonly int[] _pace = new int[] { 120, 120 };
        private const int PaceMinMs = 120;
        private const int PaceMaxMs = 1200;
        private static readonly int[] _goodRun = new int[2];

        public static int PaceMs { get { return PaceMsFor(false); } }        // 诊断口径：报的是前台那一档
        public static int PaceMsFor(bool world) { return Volatile.Read(ref _pace[Tier(world)]); }

        private static void SlowPace(bool world)
        {
            int i = Tier(world);
            Interlocked.Exchange(ref _pace[i], Math.Min(PaceMaxMs, Volatile.Read(ref _pace[i]) * 2 + 100));
            Interlocked.Exchange(ref _goodRun[i], 0);
        }

        private static void EasePace(bool world)
        {
            int i = Tier(world);
            if (Volatile.Read(ref _pace[i]) <= PaceMinMs) return;
            if (Interlocked.Increment(ref _goodRun[i]) < 20) return;     // 攒够一程才提速，免得在限流边缘来回摆
            Interlocked.Exchange(ref _goodRun[i], 0);
            Interlocked.Exchange(ref _pace[i], Math.Max(PaceMinMs, Volatile.Read(ref _pace[i]) / 2));
        }

        // 一条文本翻成功了：这一档的连续失败计数清零、间距往回收。
        // 顺带把"连续限流第几次"也收回 —— 间距都已经回到地板了，说明这一程是真通畅了，
        // 下次再撞 429 应当重新按"第一次"只歇 3 秒，而不是继续用上次风暴留下的封顶档。
        // v1.1.7：熔断计数（_breakerStreak）也在这里收回 —— 翻得成说明 key/模型这一刻是好的。
        private static void ResetFailStreak(bool world)
        {
            Interlocked.Exchange(ref _failStreak[Tier(world)], 0);
            Interlocked.Exchange(ref _breakerStreak, 0);
            EasePace(world);
            if (!world && PaceMsFor(false) <= PaceMinMs) Interlocked.Exchange(ref _rateStreak, 0);
        }

        // ===== v1.1.7：玩家明确动作之后解除全部退避 =====
        // 点「保存设置并翻译」、点「测试」、拉模型列表、换引擎/换厂商都走这里（它们都经过 PersistSettingsOnly）。
        // 没有这一条，玩家修好 key 之后还要干等熔断剩下的几分钟 —— 那正是"我已经改好了，怎么还没反应"。
        //  deliberately 也贪心：顺手把已判死的文本复活不在这一步做（那是 TransMap.Reset 的事， SaveNow 会做）。
        internal static void ResetBackoff()
        {
            Interlocked.Exchange(ref _failStreak[FRONT], 0);
            Interlocked.Exchange(ref _failStreak[WORLD], 0);
            Interlocked.Exchange(ref _breakerStreak, 0);
            Interlocked.Exchange(ref _rateStreak, 0);
            Interlocked.Exchange(ref _cooldownUntil, 0);
            Interlocked.Exchange(ref _worldCooldownUntil, 0);
            _pace[FRONT] = PaceMinMs; _pace[WORLD] = PaceMinMs;
            Interlocked.Exchange(ref _goodRun[FRONT], 0);
            Interlocked.Exchange(ref _goodRun[WORLD], 0);
            Patches.MarkerEnabled = true;
        }

        // 打开熔断：两档冷却一起推（端点已经不认我们这个 key/模型了，世界文字继续冲也只是把同一堵墙撞得更响），
        // 并给一行"说清了是什么问题、要去改什么、多久之后自己再试"的日志。待翻译文字由调用方按弹回账处理。
        private static void OpenBreaker(int status, string engineName)
        {
            int streak = Interlocked.Increment(ref _breakerStreak);
            int ms = EngineKit.BreakerCooldownMs(streak, EngineBreakerCapMs);
            int until = Environment.TickCount + ms;
            for (int cur = Volatile.Read(ref _cooldownUntil); until > cur; cur = Volatile.Read(ref _cooldownUntil))
                if (Interlocked.CompareExchange(ref _cooldownUntil, until, cur) == cur) break;
            for (int cur = Volatile.Read(ref _worldCooldownUntil); until > cur; cur = Volatile.Read(ref _worldCooldownUntil))
                if (Interlocked.CompareExchange(ref _worldCooldownUntil, until, cur) == cur) break;
            // 端点已知不可用：别再给还没翻的文字挂「…」—— 挂上也不会消失，玩家眼里就是"卡住了"。
            Patches.MarkerEnabled = false;
            LogBreaker(streak, ms, status, engineName);
        }

        private static void LogBreaker(int streak, int ms, int status, string engineName)
        {
            int now = Environment.TickCount;
            // 同一档位每分钟最多一条：熔断期间本来就没有请求，能走到这里说明刚好到期又撞了一次。
            if (streak == _breakerLogStreak && unchecked(now - _breakerLogTick) < BreakerLogEveryMs) return;
            _breakerLogStreak = streak;
            _breakerLogTick = now;
            string who = string.IsNullOrEmpty(engineName) ? "当前引擎" : "「" + engineName + "」";
            ModLog.Warn("[引擎] " + who + " 被服务端直接回绝（HTTP " + status + "）："
                        + (EngineKit.DescribeStatus(status) ?? "详见上一条日志") + "。"
                        + "这不是网络抖动 —— 换一条文字、立刻再试都是同样结果，所以本模组先停 " + (ms / 1000) + " 秒："
                        + "期间一个请求都不发、界面保留原文、也不再挂「…」标记。"
                        + "去选项里换模型 / 换 key / 换引擎，然后点「保存设置并翻译」立刻恢复。"
                        + (streak > 1 ? "（这已是连续第 " + streak + " 次，下一次会歇更久。）" : ""));
        }

        // 单条与批量共用的"这条为什么没翻成"分流。connFail = 失败发生在【连接层】（没拿到任何 HTTP 响应）。
        // v1.1.2 最关键的一条：外部条件造成的失败（限流 / 本机出口不通）不再消耗这条文本的重试预算，
        // 改记在 TransMap 的"弹回账"上。旧写法一次限流风暴就能把一整屏文本各烧掉一次机会，三条 429 就够
        // 把半屏判死 —— 那就是玩家看到的「一个框 6、7 个选项只翻了一半，之后再也不补」。
        // 文本自身翻不成（引擎回空、账号级回绝）仍然按次数判死，否则一条坏文本会拖着整条队列。
        // v1.1.4：world = 这条源文本是不是【世界文字】（路名/区名）。退避与熔断计数从此分档记账，
        // 因为世界文字天生是一大坨：它撞的墙不许把玩家眼前那一屏一起冻住（用户 2026-10-02 报的就是这个形状）。
        // v1.1.7：账号级回绝（401/402/403/404）从"每条各试一次"改成【开熔断】—— 那条失败换一条文本也不会好，
        // 继续发请求只是在刷一屏错。见 OpenBreaker 上方那段实测凭据。
        private static void OnFailure(string src, int status, bool connFail, bool world, int gen, string engineName)
        {
            // v1.1.5：这条文本自己还要不要 —— 全局那几笔账（退避、熔断、日志）照记，
            // 因为限流/出口不通是【服务端与本机】的事实，与"玩家清没清缓存"无关；
            // 但这一条不再退回队列，也不再烧它的重试预算。
            bool stale = !EngineKit.ResultStillWanted(gen, TranslationCache.ClearStamp);

            if (EngineKit.IsPermanentStatus(status))
            {
                OpenBreaker(status, engineName);
                if (stale) DropStale(src);
                else TransMap.NoteTransient(src);      // 留在队里，不烧预算：修好之后点保存就原样重试
                return;
            }

            int i = Tier(world);
            int n = Interlocked.Increment(ref _failStreak[i]);

            if (status == 429)
            {
                int waitMs = NoteRateLimited(world);
                SlowPace(world);
                if (stale) DropStale(src);
                else if (TransMap.NoteTransient(src) == 0) LogGivenUp(src, TransMap.NoteFailure(src, TransMap.MaxAttempts));
                if (n == 1)
                    ModLog.Warn("[限流] " + EngineKit.DescribeStatus(429) + "："
                                + (world ? "世界文字（路名/区名）这一档先歇 " : "界面文字这一档先歇 ")
                                + (waitMs / 1000) + " 秒再继续，间距抬到 " + PaceMsFor(world) + " 毫秒。"
                                + (world ? "界面文字不等这一档：前面的队列照翻，玩家眼前那一屏不会因此卡住。"
                                          : "世界文字（路名/区名）跟着一起等，免得继续撞同一堵墙。")
                                + "已翻好的部分照常显示，不用等；这一屏里因限流没翻成的文字【不会被判死】，通了自动补上。");
                return;
            }

            if (connFail)
            {
                // 换出口重试已经在 Http.Send 里做过一轮，这里负责"歇一歇 + 别烧文本 + 说清是哪段路不通"。
                PushCooldown(ConnectCooldownMs, world);
                if (n == 1)
                    ModLog.Warn("[网络] 连不上翻译服务端（当前出口＝" + Http.EgressName + "，系统代理＝"
                                + (Http.ProxyHint() ?? "?") + "）。这不是 key 的问题，也不是这些文字的问题："
                                + "本模组已自动改用另一条出口（走代理 ↔ 直连）重试，之后仍不通就保留原文继续排队，"
                                + "出口一恢复自己补完，不用重启游戏。");
                if (stale) DropStale(src);
                else if (TransMap.NoteTransient(src) == 0) LogGivenUp(src, TransMap.NoteFailure(src, TransMap.MaxAttempts));
                Thread.Sleep(Math.Min(2000, 400 * n));
                return;
            }

            bool permanent = EngineKit.IsPermanentStatus(status);
            if (permanent) Interlocked.Exchange(ref _failStreak[i], DeadEndpointAfter);
            if (n == 1 && permanent)
                ModLog.Warn("[引擎] 服务端直接回绝（HTTP " + status + "）："
                            + (EngineKit.DescribeStatus(status) ?? "详见上一条日志")
                            + "。后续每条只试一次并歇 5 秒，请先去选项里修 key / 换模型 / 换引擎。");

            bool dead = Volatile.Read(ref _failStreak[i]) >= DeadEndpointAfter;
            if (dead && n == DeadEndpointAfter)
                ModLog.Warn("[限流] 连续 " + DeadEndpointAfter + " 次都没翻成，判定接口暂时不可用："
                            + "后续每条只试一次并歇 5 秒，先让界面按已翻好的部分刷新（修好 key/换引擎后点保存或重新翻译即可重来）。");

            if (stale) DropStale(src);
            else LogGivenUp(src, TransMap.NoteFailure(src, dead ? 1 : TransMap.MaxAttempts));
            if (dead) Thread.Sleep(5000);
            else Thread.Sleep(Math.Min(2000, 400 * n));
        }

        // 判死日志的节流戳。一条坏文本一行"[重试] …没翻成"是有用的Diagnostics，
        // 但一次坏配置会把整屏文字各判死一次，不节流就是几百行，把真正有用的那几行埋掉。
        private static int _givenUpLogTick;
        private static int _givenUpSuppressed;

        // 与 EngineBase.Fail 的节流同一套口径：写一条、把攒下的省略数一起带上。
        private static void LogGivenUp(string src, int n)
        {
            if (n > 0) return;                       // 还没判死，那一条由 NoteFailure 内部记账，不进日志
            int now = Environment.TickCount;
            if (unchecked(now - _givenUpLogTick) < 20000)
            {
                Interlocked.Increment(ref _givenUpSuppressed);
                return;
            }
            int skipped = Interlocked.Exchange(ref _givenUpSuppressed, 0);
            _givenUpLogTick = now;
            ModLog.Warn("[重试] 「" + Probe.Trunc(src, 30) + "」这一局先保留原文（改设置、点保存或按重新翻译都会重试）。"
                        + (skipped > 0 ? "（同样的结局已省略 " + skipped + " 条）" : ""));
        }

        // 撞限流之后歇多久 —— 由 EngineKit 那把纯函数梯子给（3 → 6 → 10 → 封顶 20 秒）。
        // v1.1.3 以前第一次就歇满 20 秒：免费档撞一次"每秒速率"级别的 429 根本不该等 20 秒，
        // 而 2026-10-02 16:00:02 那次就是撞在这里（10 条请求之内撞墙，之后一路正常翻了 400 多条），
        // 玩家看到的正是"开了翻译半天没动静"。返回实际推到的毫秒数，给日志用。
        private static int NoteRateLimited(bool world)
        {
            int ms = EngineKit.RateLimitCooldownMs(Interlocked.Increment(ref _rateStreak), RateLimitCooldownCapMs);
            PushCooldown(ms, world);
            return ms;
        }

        // 把冷却终点往后推（只抬高、不缩短：三条线程同时撞 429 时取最晚的那个）。
        // 分档规矩：【前台撞墙】两档一起推（接口在限我们，世界文字继续冲只会把风暴拖长）；
        //           【世界撞墙】只推世界档（玩家眼前的界面文字不该被背景洪水冻住 —— 用户 2026-10-02 那条规矩）。
        private static void PushCooldown(int ms, bool world)
        {
            int until = Environment.TickCount + ms;
            if (!world)
            {
                for (int cur = Volatile.Read(ref _cooldownUntil); until > cur; cur = Volatile.Read(ref _cooldownUntil))
                    if (Interlocked.CompareExchange(ref _cooldownUntil, until, cur) == cur) break;
            }
            for (int cur = Volatile.Read(ref _worldCooldownUntil); until > cur; cur = Volatile.Read(ref _worldCooldownUntil))
                if (Interlocked.CompareExchange(ref _worldCooldownUntil, until, cur) == cur) break;
        }

        // 前台有活干时只等前台那一档；前台空了、轮到世界文字时等两档里最晚的那个。
        private static void WaitForCooldown(bool frontOnly)
        {
            for (;;)
            {
                int remain = (frontOnly ? Volatile.Read(ref _cooldownUntil)
                                        : Math.Max(Volatile.Read(ref _cooldownUntil), Volatile.Read(ref _worldCooldownUntil)))
                             - Environment.TickCount;
                if (remain <= 0 || _shutdown) return;
                Thread.Sleep(Math.Min(remain, 1000));     // 分段睡：退出收口时最多多等 1 秒，不会卡在长睡眠里。
            }
        }

        // 拿到一条译文之后的统一收尾：单条与批量共用，免得两份逻辑各自走偏。
        private static void FinishOne(string src, string trans, string target, ITranslationEngine engine, int gen)
        {
            // v1.1.5 第一件事就是问这一句还要不要：出发之后玩家点了「清除缓存」或按了「重新翻译」
            //（两处都走 TranslationCache.Clear，它把清代代际戳抬一格）⇒ 这句译文属于【上一次】的结果，
            // 写下去就是"清了又长回来"：内存表被填回几条、刚删掉的 translation.cache 又被追加出同样的行。
            // 这里刻意【不】退回队列：清代把两条队列与 WorldSources 台账都清空了，此刻的 Requeue
            // 等于把旧活塞进新队列，而且路名会被错认成界面文字挤进前台那一队（v1.1.4 分好的两档又搅回去）。
            // 丢掉不亏：界面上这一处文字还在，下一轮扫描/下一次取词会按【当前】设置自己重新入队。
            if (!EngineKit.ResultStillWanted(gen, TranslationCache.ClearStamp)) { TransMap.DropStale(src); return; }

            // 需求5：完整性校验。译文若破坏了占位符（把 {token} 翻成中文、整个吞掉、或凭空增删花括号），
            // 判为不安全 → 还原原文（落进下面的 identity 分支：跳过 + 缓存 identity），绝不让坏译文上屏。
            // 注：不校验数字——机翻把数字与词互转（1,000↔千、双↔2）多为正常，旧数字校验会误杀正文。
            if (!TransGuard.IsSafe(src, trans))
            {
                ModLog.Info($"[校正] 译文破坏了占位符/花括号，已还原原文：'{Probe.Trunc(src, 30)}' -> '{Probe.Trunc(trans, 40)}'");
                trans = src;
            }

            if (trans == src)
            {
                // 译文==原文：确定性结论——本就是目标语言（或无需翻译），标记跳过并缓存这个结论。
                TransMap.ResolveAsSkip(src);
                TranslationCache.Put(engine.CacheName, src, target, src);
            }
            else
            {
                TransMap.SetTranslation(src, trans);
                TranslationCache.Put(engine.CacheName, src, target, trans);
                ModLog.Info($"[懒翻译] '{Probe.Trunc(src, 30)}' -> '{Probe.Trunc(trans, 40)}'");
            }
        }

        // 反馈7：攒一批、发一次、按编号对回去。三条硬约束：
        // ①只收单行短文本（BatchKit.CanBatch）——带换行的会把整批的行数打乱；
        // ②对不齐的槽位一律按失败处理（退回队列走单条），绝不把甲的译文安到乙头上；
        // ③整批失败只算「一次」全局失败，否则一次网络抖动就是 12 次计数，熔断形同虚设。
        // 返回值 false = 这批没发出去，调用方按原来的单条路径处理 src。
        // v1.1.5 多带一个 stamp（出发时的清代代际戳）：一批最多 40 条，一次清代如果不管这一格，
        // 回写的就是 40 条旧译文 + 40 行磁盘缓存 —— 比单条路径"污染"得更多。
        //
        // ===== v1.2.4 需求2/需求3：为什么实机「翻译比改提示词之前慢很多」=====
        // 玩家反馈「无论机翻还是 AI 翻译都慢很多」，而 git diff c67faaf..v1.2.3 对 kWorkerCount /
        // MaxItems / 扫描预算 / 各种间隔【一个数都没动】—— 所以慢的原因不是某一格调参，是【结构】：
        //   · 扫描器是【一行一行】入队的（走一行排一条），不是攒够一屏再一起排；
        //   · kWorkerCount=5 条线程全都堵在 Take(500) 上，队列里一出现第一条，5 条线程同时醒过来
        //     各自把这一条抢走 ⇒ 每条线程手上只有 1 条 ⇒ 下面 todo.Count < 2 ⇒ 走单条路径。
        // 实机凭据（v1.2.3 那一局，日志 23:02:47 起）：155 条待翻文字里 145 条走了单条请求，
        // 批量请求【总共只发出 1 次】（那一次 10 条全对上）。批量协议本身没问题，是它从没被喂饱过。
        // 另一只手是悬停闸（需求2 那一版加的）：整页说明不再随扫描批量入队，而是"玩家 hover 一行才排一条"
        // —— 这对 MT 引擎尤其致命，因为 MT 那几家 Batchable=false，本来就不攒批，一次往返只有一条文字。
        // ⚠ 但这一只手【不能】靠放宽说明来治：玩家本轮明确重申"说明依然还是鼠标移上去显示后才翻译"
        //   （见 HandleRowText 那道闸上方那段话）。说明那一半这一轮一条都不多翻，它变快的唯一来源是
        //   "同一轮里被 hover 过的几条能进同一个请求"（攒批）而不是"多放几条进去"。
        // 所以本轮两处一起改：
        //   ① 取货这一步改成【有界等一小会儿】（下面的 BatchWindowMs）：还没凑到第二条时，
        //      以 BatchKit.SliceMs 为一片小睡再捞一次，最多等 BatchWindowMs；凑够两条立刻不再等。
        //      这一等对【标题与候选项】尤其有用：扫描一轮会接连排进几十条同板块的标题，
        //      有界窗口正好把它们收进同一批（这就是玩家要的"一个板块整体送翻、按顺序下来"）。
        //      它不拿延迟换批次大小 —— 只等"本来就在排着、马上就到"的那几条。
        //   ② 不合群/不合范围的条目退回【队头】（TransMap.ReturnToHead），不是队尾。
        //      它们本来就排在玩家眼前更前面的位置（扫描是自上而下的），退到队尾等于让玩家眼前的
        //      那几行文字等完整屏路名 —— 玩家这一轮的原话是"按顺序翻译下来视觉上比较舒服"。
        //      退到队头 ⇒ 下一个取到它的线程立刻按它自己的范围重新攒批 ⇒ 视觉顺序与队列顺序一致。
        // ⚠ 这一等只发生在【引擎真的会攒批】的那几家（Batchable=true，见 EngineKit）；
        //   MT 那几家走不到这里（TryBatch 的调用点已按 Batchable 拦过），所以它们的耗时不因这一格而变
        //   —— 它们这一轮变快是靠"退回队头保序 + 标题/候选项本来就成串入队"，不是靠攒批。
        //
        // ===== v1.2.5 需求2：首屏优先 —— 有积压时一批先拿 12 条，不让一条线程吞满 40 =====
        // 玩家这一轮报"翻译好慢好慢"，实机日志逐条量过：每个请求 2~7 秒，而 17 条挤在一次请求里
        // （22:48:28 那一行）—— 17 条全部生成完才一起上屏，玩家看到的就是"半天没动静，然后一下全出来"。
        // 旧判据只有"最多 40 条"这一个上限，队列里堆着几十条时，最先拿到第一条的线程会一路捞到 40，
        // 另外 4 条线程与后面所有文字只能等这一大批。现在在 BatchKit.ShouldLeaveForOthers 里加第二道闸：
        //   手上够 12 条、且这一队还有积压 ⇒ 立刻停手，把后面的留给别的 worker；
        //   队列已经空了 ⇒ 继续攒到 40（没人抢，多带几条只省请求，不推迟任何人的首屏）。
        // ⚠ 40 的硬上限没动，所以玩家上一轮要的"同一个请求多送几条"仍然成立；变的只是"有人在等时先让路"。
        // ⚠ 12 是 5 条 worker × 12 = 60 条在飞，够铺满一屏；这个数不参与限流判据（429 仍由全局退避兜）。
        // ⚠ 没有做流式：那需要 SSE 解析 + 把半截译文按编号行安全地映射回上屏控件，任何一格错位都会
        //   把甲的译文安到乙头上 —— 与本项目"宁可整批作废也绝不猜"的铁律冲突，这一版不碰。
        //
        // 攒批窗口：手上还只有 1 条时，最多等这么久再放弃（以 BatchKit.SliceMs 为一片小睡反复捞）。
        // 250ms 的取法：扫描一轮 1.2 秒、一页几十行 ⇒ 同一板块的那几条通常在几十毫秒内接连入队；
        // 250 足以接住它们，又短到"就算队列真空着"也只多等四分之一秒（单条路径本来就要一次网络往返，
        // 这一格的量级远小于它）。凑够两条立刻收手，所以典型情形根本不睡满。
        private const int BatchWindowMs = 250;
        // v1.2.5 修复：seed 是【扫描器收齐的一个板块】（TransMap.Take 取到的请求单元）。
        //   · seed.Length >= 2 ⇒ 整块就是这一批，跳过下面整个攒批循环 —— 一个板块一次请求，不等 250ms 窗口
        //     （等窗口是给"一条一条入队"的旧路径接住同伴用的，板块这条路同伴已经在手上了）；
        //   · seed 为 null / 长度 1 ⇒ 与 v1.2.5 之前一字不差。
        private static bool TryBatch(TranslatorSetting setting, ITranslationEngine engine, string target,
                                     string src, bool world, int stamp, string[] seed)
        {
            var items = seed == null || seed.Length == 0 ? new List<string> { src } : new List<string>(seed);
            var deferred = new List<string>();
            // v1.1.7 性能：「这条是不是已经抓在手上了」原来是 deferred.Contains 的 O(n) 线性扫
            //（deferred 上限 48 项、内层最多转 12 圈 ⇒ 每批最多 576 次字符串比较，而且比对的是整句原文）。
            // 换成 HashSet 一句话的事，顺带把去重范围摆正：旧写法只查 deferred，没查 items，
            // 所以 src 自己（或已在批里的某条）再次被 TakeSameTier 捞上来时会被【塞进同一批两遍】——
            // 编号协议按行号对齐不会翻错，但那条文本白占一格 12 分之一的批量额度。seen 一本账管两边。
            // v1.2.5 修复：seed 整块都是"已经抓在手上"的，全部进 seen（否则重复捞到的那条会被当成新的塞第二遍）。
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < items.Count; i++) seen.Add(items[i]);
            // v1.2.0 需求5：一批只收【同一个范围】的文本（用户原话："不同范围的肯定不可能放在同一个请求里"）。
            // 为什么必须在这里拦、不能只靠队列天然分开：前台队列里同时会有「模组设置项」「资产名」
            // 「游戏本体界面文字」三类，它们的提示词语境完全不同 —— 混在一批里，一段语境要覆盖三种文本，
            // 结果就是三种都翻得似是而非。范围不符的条目原样退回（不算失败、不烧预算），
            // 下一条循环会按它自己的范围重新攒批。
            Scope.Category batchScope = TransMap.ScopeOf(src);
            // ===== v1.2.4 需求2/需求3：有界等一小会儿，把"马上就到"的那几条收进同一批 =====
            // 旧写法是非阻塞捞（TakeSameTier(src, 0)）：捞不到就 break。而扫描器是【一行一行】入队的，
            // 5 条工作线程又全堵在 Take 上、第一条一出现就各自抢走一条 ⇒ 每轮 TryBatch 手上永远只有 1 条
            // ⇒ 走单条路径（实机凭据：v1.2.3 那一局 155 条里 145 条单条，批量请求总共只发出 1 次）。
            // 现在改成：凑够两条之前，以 SliceMs 为一片小睡再捞一次，最多等 BatchWindowMs 就收手。
            // 为什么不是"固定等满窗口"：一凑到第二条（够用编号协议了）立刻不再等，正常情形下几乎不睡；
            // 这一等只为了接住"同一屏同一板块、本来就排在队列里"的那几条，不是拿延迟换批大小。
            // ⚠ 总量也有一道闸（BatchKit.Fits）：40 条 × 每条最多 300 字的极端组合会撑爆
            //   Anthropic 那一家钉死的 max_tokens=2048，所以按字符总量提前收手，条数随文本长度自动缩。
            // v1.2.5 需求2：再加一道【并发优先】的闸 —— 手上有 12 条、而队列里还有活时立刻停手。
            // 40 条的边界只在"整条队列只剩这一批"时才用到；有积压的时候一条线程拿满 40，
            // 另外 4 条线程与后面几十条文字只能干等这一大批生成完，玩家看到的就是"很久没动静"。
            int totalChars = 0;
            for (int i = 0; i < items.Count; i++) totalChars += items[i].Length;
            int windowEnd = Environment.TickCount + BatchWindowMs;
            bool seeded = items.Count >= 2;                     // 板块整块送来的：不再等窗口、不再捞队列
            while (!seeded && items.Count < BatchKit.MaxItems)
            {
                // 够 12 条且后面还有人排队 ⇒ 把剩下的留给别的 worker（判据是纯函数，B 段钉着）。
                if (BatchKit.ShouldLeaveForOthers(items.Count, TransMap.BacklogSameTier(src))) break;
                // 已经够两条（能走编号协议）⇒ 不再等：只把此刻【已经排在队列里】的顺手收进来。
                // 还只有一条 ⇒ 以 SliceMs 为一片小睡再捞，最多等满 BatchWindowMs。
                int wait = 0;
                if (items.Count < 2)
                {
                    int left = windowEnd - Environment.TickCount;
                    if (left <= 0) break;                       // 等满了还没凑到第二条 ⇒ 老老实实走单条
                    wait = Math.Min(left, BatchKit.SliceMs);
                }
                string more = TransMap.TakeSameTier(src, wait); // 只捞同一条队列：绝不把路名混进面板这一批
                if (more == null)
                {
                    if (items.Count >= 2) break;                 // 够一批了，队列此刻空着
                    continue;                                    // 只有一条，继续等（受窗口约束）
                }
                if (seen.Add(more))                              // 第一次抓到才处置
                {
                    if (BatchKit.CanBatch(more) && TransMap.ScopeOf(more) == batchScope
                        && BatchKit.Fits(totalChars, more))
                    {
                        items.Add(more);
                        totalChars += more.Length;
                    }
                    else deferred.Add(more);                     // 不能进批（或不同范围/总量超了）先记下，等会儿整批退回
                }
                else
                {
                    // 重复捞到（同一条既在队头表又在队列里，Requeue/ReturnToHead 都直接 Add、不经 Queued 去重）：
                    // 【不能就这么算了】—— 它已经被 TakeFrom 记了一笔在飞，这里丢掉就是计数永久不平，
                    // IsIdle 从此永远说不，界面等不到"真的排空了"的刷新。退回队头由下一次取货消化
                    //（届时它多半已被本批翻好，取货那一侧会走"已经有了"的短路）。
                    deferred.Add(more);
                }
                if (deferred.Count >= BatchKit.MaxItems * 4) break;
            }
            // 不合群/不合范围的退回【队头】：它们本来就排在玩家眼前更前面（扫描自上而下），
            // 退到队尾等于让眼前那几行等完整屏 —— 玩家这一轮点名要的就是"按顺序"。
            foreach (string d in deferred) ReturnToHeadOrDrop(d, stamp);

            // 磁盘缓存命中的就地解决，只把没命中的发出去（同一屏里大部分名字其实早就翻好了）。
            var todo = new List<string>();
            var masked = new List<string>();
            var guards = new List<string[]>();
            foreach (string t in items)
            {
                string cached = TranslationCache.Get(engine.CacheName, t, target);
                if (cached != null)
                {
                    if (cached == t) TransMap.ResolveAsSkip(t); else TransMap.SetTranslation(t, cached);
                    _uiDirty = true;
                    continue;
                }
                string[] gt;
                masked.Add(TransGuard.Mask(t, out gt));    // 逐条掩码，下标与 todo 严格对齐
                guards.Add(gt);
                todo.Add(t);
            }

            // 凑不满两条就别用编号协议了（单条走原路径语义完全一致）。src 若已被缓存解决，这批就算处理完。
            // v1.2.4：这一格退的是【已经在这一批里、但不发出去】的那几条 —— 它们可能排在 src 前后，
            // 一律退回队头，让它们按原顺序被各自的攒批接走（退队尾就乱了视觉顺序，见 ReturnToHeadOrDrop）。
            // v1.2.5 修复：整块送来的那一批不要求 todo[0] 就是 src —— src 若被磁盘缓存就地解决，
            // 它就不在 todo 里，但剩下那几条仍是同一个板块、顺序也还是显示顺序，照样一次请求送出去。
            if (todo.Count < 2 || (!seeded && todo[0] != src))
            {
                int start = (todo.Count > 0 && todo[0] == src) ? 1 : 0;
                for (int i = start; i < todo.Count; i++) ReturnToHeadOrDrop(todo[i], stamp);
                return start == 0;
            }

            string raw = null;
            try
            {
                // v1.2.0 需求5：批量提示词按这一批的【共同范围】给语境。
                // v1.2.1 需求1④：正文只剩编号行本身，整套指令（含编号协议）都在 system 字段里 ——
                // 所以这里要把形状也钉进去（batch=true），Body 里的 AiPrompt.System 据此补末尾那一句。
                // 三个接口的 system 字段都在 Body 里由 cat + locale 现拼，所以调用点只钉 PromptScope。
                PromptScope.Set(batchScope, true);
                raw = engine.Translate(setting, AiPrompt.BatchUser(masked.ToArray()), "auto", target);
            }
            catch (Exception ex) { ModLog.Error("[批量] 请求异常，本批退回单条: " + ex.Message); }
            finally { PromptScope.Clear(); }   // ⚠ 不能在 try 里清：上面抛异常时那一格会粘在 worker 线程上，
                                               // 下一条【单条】请求就带着编号协议出去，模型可能回 "1. 译文"。
            // 紧跟请求取走状态码（取完即清）：下面每个提前 return 都必须经过这里，
            // 否则这一轮的 429 会挂在线程上，被下一轮无关的请求当成失败原因。
            int status = EngineBase.ConsumeStatus();
            bool connFailBatch = EngineBase.ConsumeConnectFailure();

            // 请求期间玩家改了目标语言：整批作废，退回队列按新语言重来
            if (TranslatorSetting.Instance?.TargetLocale != target)
            {
                foreach (string t in todo) RequeueOrDrop(t, stamp);
                return true;
            }

            string[] parts = BatchKit.Split(raw, todo.Count);
            // v1.1.5：请求期间玩家点了「清除缓存」/改了设置 ⇒ 【整批作废】。
            // 这一批最多 12 条，放任它回写就是 12 条旧译文进内存表 + 12 行旧缓存重新建进
            // 刚被删掉的 translation.cache —— 玩家下一次看到的"缓存没清干净"就来自这里。
            // 逐条 DropStale（各还一笔在飞计数、各计一个丢弃数），下面那圈回写整个跳过。
            if (!EngineKit.ResultStillWanted(stamp, TranslationCache.ClearStamp))
            {
                foreach (string t in todo) DropStale(t);
                return true;
            }

            int ok = 0, bad = 0;
            // v1.1.2：这批是【被外部条件挡掉的】（限流 / 连接不通 / 账号级回绝）还是【这些文字自己翻不成】，
            // 处置完全相反。
            //   · 外部条件 ⇒ 整批走 TransMap.NoteTransient，不烧每条的重试预算（旧写法一批 12 条一次全废就是
            //     12 条各扣一次机会，两次限流风暴就够把一整屏判死 —— 玩家看到的「框里 6、7 个选项只翻一半」）；
            //   · 自己翻不成 ⇒ 仍按次数判死（引擎回空、连续失败到熔断线时每条只试一次就放过）。
            // v1.1.7：账号级回绝并进"外部条件"。它换一条文本也不会好，正确处置是熔断（见 OnBatchFailure）；
            // 旧写法在这里把整批 12 条各判死，是"一次坏配置就把玩家眼前那一屏永久打成原文"的直接原因。
            bool permanentBatch = EngineKit.IsPermanentStatus(status);
            bool external = status == 429 || connFailBatch || permanentBatch;
            bool dead = !external && Volatile.Read(ref _failStreak[Tier(world)]) + 1 >= DeadEndpointAfter;
            // 日志要说实话：这 12 条到底是"退回重试"还是"这一局先跳过"，两种结局对玩家的动作完全不同。
            string badWhy = external ? "被外部条件挡下，已保留重试预算，稍后自动补"
                          : dead ? "这一局先跳过（服务端直接回绝，换 key/模型/引擎后点保存或重新翻译即可重试）"
                          : "退回单条重试";
            for (int i = 0; i < todo.Count; i++)
            {
                string tr = parts[i] == null ? null : TransGuard.Unmask(parts[i], guards[i]);
                if (string.IsNullOrEmpty(tr))
                {
                    // 外部条件挡掉的 ⇒ 弹回队列、不烧预算（弹回满 KMaxTransient 次才降级成普通失败）。
                    if (external)
                    {
                        if (TransMap.NoteTransient(todo[i]) == 0) LogGivenUp(todo[i], TransMap.NoteFailure(todo[i], TransMap.MaxAttempts));
                    }
                    else LogGivenUp(todo[i], TransMap.NoteFailure(todo[i], dead ? 1 : TransMap.MaxAttempts));
                    bad++;
                    continue;
                }
                FinishOne(todo[i], tr, target, engine, stamp);
                ok++;
            }

            if (bad > 0)
            {
                if (ok == 0) OnBatchFailure(status, connFailBatch, world, engine.Name);   // 整批全废才算一次全局失败（约束③）
                else if (!permanentBatch) Interlocked.Increment(ref _failStreak[Tier(world)]);    // 部分对上：接口是活的，只记一笔、不拖慢后面的批次
            }
            else ResetFailStreak(world);

            if (ok > 0) { _uiDirty = true; RefreshUi(false); }
            ModLog.Info("[批量] 一次请求 " + todo.Count + " 条：对上了 " + ok + " 条，" + bad + " 条" + badWhy
                        + (raw == null ? "（接口没返回）" : "（模型没按编号回的行会被判对不齐，不会错配）") + "。");
            Thread.Sleep(PaceMsFor(world));
            return true;
        }

        // 整批全废的收尾：与 OnFailure 共用同一份全局计数和冷却，但【不碰 TransMap】——
        // 这批每一条已经在上面的循环里各自记账过了，这里只负责「要不要退避、退避多久、要不要换出口」。
        // 单条失败日志里那句人话也不用重复：这批退回单条后，单条路径会自己去撞同一次失败并写日志。
        // v1.1.7：账号级回绝（401/402/403/404）改走熔断，不再走"把计数推到上限、每条各试一次"那条老路。
        private static void OnBatchFailure(int status, bool connFail, bool world, string engineName)
        {
            if (EngineKit.IsPermanentStatus(status)) { OpenBreaker(status, engineName); return; }
            int i = Tier(world);
            int n = Interlocked.Increment(ref _failStreak[i]);
            if (status == 429) { NoteRateLimited(world); SlowPace(world); return; }
            if (connFail) { PushCooldown(ConnectCooldownMs, world); Thread.Sleep(Math.Min(2000, 400 * n)); return; }
            bool dead = Volatile.Read(ref _failStreak[i]) >= DeadEndpointAfter;
            Thread.Sleep(dead ? 5000 : Math.Min(2000, 400 * n));
        }

        // 把磁盘缓存里「当前引擎 + 当前目标语言」命中的历史译文，预载进运行时 TransMap（需求9a）。
        // 这样重启后玩家看到的旧译文能立即显示，不必逐条重新显示「…」再翻一遍。
        // v1.1.6：整表扫描 + 逐条 base64 解码是重活，而本方法同时被「保存设置」
        //（快捷键/按钮）与「重新翻译」在主线程调用 —— 缓存攒到几万条时玩家每按一次
        // 快捷键就卡一下。这里只在主线程取一份设置快照，扫描放到后台线程，做完再回
        // 主线程刷一次界面。
        // 单飞：连按/连改设置时只保留一轮，欠着的那轮用最新设置重跑（_seedAgain）。
        // 代数保护：扫描期间玩家又点了保存 ⇒ ClearStamp 变了，这批结果作废
        //（与 EngineKit.ResultStillWanted 同一判据），否则会把「旧目标语言」的译文
        // 写进新设置的译文表。
        private static int _seeding;
        private static volatile bool _seedAgain;

        private static void SeedCacheIntoTransMap()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) return;
            string engine = TranslationEngines.Get(setting.TranslationEngine).CacheName;
            string target = setting.TargetLocale;
            if (Interlocked.CompareExchange(ref _seeding, 1, 0) != 0) { _seedAgain = true; return; }
            new Thread(() =>
            {
                try
                {
                    int stamp = TranslationCache.ClearStamp;
                    int n = TranslationCache.SeedTransMap(engine, target, stamp);
                    if (n >= 0)   // >=0 = 这一轮没被清代打断
                    {
                        if (n > 0) { _uiDirty = true; RefreshUi(true); }
                        ModLog.Info($"[缓存] 预载 {n} 条历史译文进内存表（引擎={engine} 目标={target}）：重启后即时显示、不再重翻。");
                    }
                }
                catch (Exception ex) { ModLog.Error("[缓存] 预载失败: " + ex.Message); }
                finally
                {
                    Interlocked.Exchange(ref _seeding, 0);
                    // 跑的时候设置又变了：补一轮（用最新设置）。放在释放之后，避免新旧两轮互相踩 _seeding。
                    if (_seedAgain) { _seedAgain = false; SeedCacheIntoTransMap(); }
                }
            })
            { IsBackground = true, Name = "Cs2AutoTranslatorSeed" }.Start();
        }

        // 把新译文/去标记「原地」刷到当前可见界面：调用 Cohtml 的本地化重译（只刷新，不惊动 I18N 等订阅者）。
        // force=true（队列空闲时）立即刷；否则节流。v1.0.5 把 1500ms 降到 900ms：玩家报的"慢"里有一半是
        // 「译文其实早就落地的、界面还要等下一次刷新」，降这 600ms 不增加任何网络请求，代价只是全屏
        // 重译的频率从 0.67 次/秒变 1.1 次/秒（一次重译本身只是一次 cohtml 回调）。
        // 反馈2：多条 worker 会同时想刷界面，所以「查 + 改」这两个字段必须一起做，否则几个线程都判定
        // 「距上次已经 1 秒了」→ 同一帧重绘三遍。派活动作本身只是入队，放在锁外做。
        private const int UiRefreshIntervalMs = 900;
        private static readonly object _uiRefreshLock = new object();

        private static void RefreshUi(bool force)
        {
            lock (_uiRefreshLock)
            {
                if (!_uiDirty) return;
                int now = Environment.TickCount;
                if (!force && now - _lastRefreshTick < UiRefreshIntervalMs) return;
                _uiDirty = false;
                _lastRefreshTick = now;
            }
            try { MainThreadDispatcher.RunOnMainThread(Patches.RefreshVisibleText); } catch { }
        }

        // 供 Patches（守护线程/主线程 updater）调用：标记界面需刷新并立即强刷一次（需求9c：补丁重打后让原文变回译文）。
        internal static void MarkUiDirtyAndRefresh()
        {
            _uiDirty = true;
            RefreshUi(true);
        }

        // 「测试翻译」按钮回调（需求7）：只测引擎服务是否【连通】，不翻译界面、不写回 TransMap。
        // 在主线程触发，立刻把联网探测丢到后台线程，避免卡住 UI。
        private static void RunTest()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) return;
            PersistSettingsOnly(); // 需求5：测试前只顺手「静默存盘」(清洗 key + 写盘 + 同步镜像)，绝不清空/重翻全局译文。
            ITranslationEngine engine = TranslationEngines.Get(setting.TranslationEngine);
            // 缺哪一项由 EngineKit 判定（14 家各不相同：Cloudflare 缺 Account ID、百度可能缺 APP ID、自定义 AI 可能缺模型），
            // 在主线程上算好再带进后台线程，免得后台读到的设置已经被玩家改掉。
            string missing = TranslationEngines.MissingField(setting);
            TranslatorSetting.LastTestResult = $"正在测试「{engine.DisplayName}」服务连通性…";
            ModLog.Info("[测试] 用户点击测试按钮（只测连通性），引擎=" + engine.Name + " 模型=" + setting.EffectiveModel + " 缺项=" + (missing ?? "无"));
            new Thread(() =>
            {
                string result;
                try
                {
                    if (missing != null)
                        result = $"✗ 「{engine.DisplayName}」还缺必填项：{FieldLabel(missing)}。"
                               + (EngineKit.ShowRegister(engine.Name) ? "可点旁边的「跳转注册页面」去申请。" : "");
                    else
                    {
                        // 探针句子（v1.0.5 第四条，2026-09-29 用户改的主意）：**固定用英文** "Hello, world"，
                        // 只有目标语言本身就是英文时才换成中文「你好，世界」—— 英文→英文等于什么都没测。
                        // （上一版这里曾按【游戏语言】取句，用户实机后说想固定：一般目标语言都在英文之外，
                        //  英文句子对 14 家引擎都是最稳的一条探针。）判据与那张 44 语表都在 LanguageCatalog。
                        string probe = LanguageCatalog.ProbeFor(setting.TargetLocale);
                        // v1.2.0 需求6：测试按钮那颗探针只测【连通性】，不需要整套本地化提示词。
                        // 旧写法走 engine.Translate ⇒ 带 300+ 字符的 system 提示，一次烧掉一百多 token；
                        // 现在 AI 引擎改走瘦身入口（只发一句「翻译这句话，直接回复译文」），
                        // 机翻引擎本来就没有提示词，照旧走 Translate。
                        string trans = engine is AiProbeEngine ai ? ai.TestProbe(setting, probe, setting.TargetLocale)
                                                                  : engine.Translate(setting, probe, "auto", setting.TargetLocale);
                        // 反馈1：状态码能说出「为什么没成功」，比一律怪网络靠谱得多 ——
                        // 实测这一颗探针就是玩家唯一的诊断入口（402 是没余额、404 是模型没免费档）。
                        int testStatus = EngineBase.ConsumeStatus();
                        bool testConnFail = EngineBase.ConsumeConnectFailure();
                        // v1.1.2：连接层不通必须单独说，而且要说到具体那一段路。旧文案一律是「模型可能已下架或
                        // 额度不足…也可能是网络/key 不对」，玩家照着去查 key、换模型，而真正的原因在本机出口
                        // （实机凭据：2026-10-01 15:33 起换任何引擎都是 ConnectFailure「由于目标计算机积极拒绝」，
                        //  而游戏内外其它网络一切正常 —— 那是系统代理指向的端口没了，与 key 半点关系没有）。
                        string why = testConnFail
                            ? "连不上翻译服务端，问题在【本机出口】这一层：现在走的是「" + Http.EgressName + "」，"
                              + "系统代理指向 " + (Http.ProxyHint() ?? "?") + "。"
                              + "如果开着代理/VPN 而它已退出，系统代理却还指着那个端口 —— 要么把代理重新打开，"
                              + "要么在 Windows 的「Internet 选项 → 局域网设置」里取消代理。本模组已经自动试过另一条出口（走代理 ↔ 直连）。"
                            : EngineKit.DescribeStatus(testStatus);
                        // v1.1.7：账号级回绝（401/402/403/404）时把后台翻译也停下来。
                        // 测试按钮是玩家唯一的诊断入口，它都说"这个 key/模型不能用"了，后台再每条各试一次
                        // 只是在刷一屏错 —— 与 TranslateLoop 撞到同一类失败时走的是同一条熔断（OpenBreaker）。
                        bool refused = !testConnFail && EngineKit.IsPermanentStatus(testStatus);
                        if (refused) OpenBreaker(testStatus, engine.Name);
                        result = string.IsNullOrEmpty(trans)
                            ? $"✗ 「{engine.DisplayName}」请求没成功："
                              + (why ?? "模型可能已下架或额度不足（点上方「获取模型列表」会自动改用可用的免费模型），也可能是网络/key 不对")
                              + (refused ? "。后台翻译已随之暂停，修好后点「保存设置并翻译」立刻继续。" : "。详见日志。")
                            : $"✓ 「{engine.DisplayName}」服务连通正常（'{probe}' → '{trans}'）。";
                    }
                }
                catch (Exception ex) { result = "✗ 测试异常: " + ex.Message; }
                TranslatorSetting.LastTestResult = result;
                ModLog.Info("[测试] " + result);
                MarkUiDirtyAndRefresh();   // 立刻把结果刷到界面上，不等后台循环下一轮节流
            }) { IsBackground = true }.Start();
        }

        // 缺项名（key / appid / accountid / token / model）→ 玩家语言的字段标签，与输入框标签同一批 L10n 键。
        private static string FieldLabel(string field)
        {
            string loc = Patches.ActiveLocale;
            return SelfL10n.T(string.IsNullOrEmpty(loc) ? "en-US" : loc, "field." + field);
        }

        // 「跳转注册页面」按钮回调（需求2，主线程）：用系统默认浏览器打开当前引擎的申请页。
        // 网址表在 EngineKit.RegisterUrl —— 与「按钮显不显示」用的是同一张表，不会出现「按钮在但没网址」。
        private static void OpenRegisterPageNow()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) return;
            string url = EngineKit.RegisterUrl(setting.TranslationEngine.ToString());
            if (string.IsNullOrEmpty(url)) { ModLog.Warn("[注册页] 当前引擎没有登记注册页网址。"); return; }
            try
            {
                // 不指定程序名，交给系统的 URL 关联 → 默认浏览器。UseShellExecute 是 .NET Framework 上打开链接的必需开关。
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                ModLog.Info("[注册页] 已尝试用默认浏览器打开：" + url);
            }
            catch (Exception ex) { ModLog.Error("[注册页] 打开失败: " + ex.Message + "（可手动访问 " + url + "）"); }
        }

        // v1.2.0 需求3：「获取 API key」按钮回调（主线程）：打开当前选中那家自定义 AI 厂商的官方 key 页。
        // 与 OpenRegisterPageNow 是同一套机械（系统默认浏览器 + UseShellExecute），差别只在网址表：
        // 那边按【引擎】查 EngineKit.RegisterUrl，这边按【厂商】查 CustomAi.KeyPage。
        // 两个按钮各管一半、互不重叠：直连引擎（Gemini/Groq/…）用注册页，
        // 自定义 AI 的 8 家用 key 页；自定义 AI 那一格 RegisterUrl 恒为 null ⇒ 那边不出现，不会有两个按钮并列。
        private static void OpenApiKeyPageNow()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) return;
            // 与按钮显隐判据同一个函数、同一张表（CustomAi.HasKeyPage / KeyPage）——
            // 「按钮在但没网址」这种半拉子状态在这里结构上不可能出现。
            string vendor = CustomAi.Normalize(setting.CustomVendor.ToString());
            string url = CustomAi.KeyPage(vendor);
            if (string.IsNullOrEmpty(url))
            { ModLog.Warn("[API key 页] 当前厂商（" + vendor + "）没有登记官方申请页。"); return; }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                ModLog.Info("[API key 页] 已尝试用默认浏览器打开（" + vendor + "）：" + url);
            }
            catch (Exception ex) { ModLog.Error("[API key 页] 打开失败: " + ex.Message + "（可手动访问 " + url + "）"); }
        }

        // 「获取模型列表」按钮回调（需求2，主线程触发、后台线程拉取）：拉到的模型名进 TranslatorSetting 的列表，
        // 下拉框刷新后就能看到。四个 AI 引擎共用（自定义 AI + Groq / OpenRouter / SiliconFlow）。
        // v1.1.6：单飞。连点不会同时起多个请求线程（否则界面上显示哪一次的结果
        // 全看谁先回来）。已在跑时只更新提示文案、不重复发请求。
        private static int _fetchingModels;

        private static void FetchModelsNow()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) return;
            if (Interlocked.CompareExchange(ref _fetchingModels, 1, 0) != 0)
            {
                TranslatorSetting.FetchModelsResult = "正在获取模型列表，请稍候…（已有一个在跑）";
                MarkUiDirtyAndRefresh();
                return;
            }
            PersistSettingsOnly();
            string name = setting.TranslationEngine.ToString();
            string vendor = setting.TranslationEngine == TranslatorSetting.Engine.Custom ? CustomAi.Normalize(setting.CustomVendor.ToString()) : null;
            string key = Keys.Clean(setting.GetKeyFor(setting.TranslationEngine));
            string baseUrl = setting.TranslationEngine == TranslatorSetting.Engine.Custom
                ? CustomAi.Base(vendor, setting.CustomBaseUrl)
                : EngineKit.HostedBase(name);
            string url = setting.TranslationEngine == TranslatorSetting.Engine.Custom
                ? CustomAi.ModelsUrl(vendor, setting.CustomBaseUrl, key)
                : (name == "Gemini" ? GeminiApi.ModelsUrl(null, key) : OpenAiChat.Url(baseUrl, "models"));
            TranslatorSetting.FetchModelsResult = "正在获取模型列表…";
            // v1.2.0 需求8：这个 URL 里可能带着 key（Gemini 与自定义 AI 的 /models 都把 key 放在查询参数里），
            // 所以进日志前必须脱敏 —— 否则玩家点一下按钮、自己的 API key 就明文写进了本模组的日志文件。
            // 域名与路径原样保留：诊断"这个端点长什么样"要的就是它们。
            ModLog.Info("[模型列表] 引擎=" + name + " 厂商=" + (vendor ?? "-") + " 端点=" + EngineKit.RedactUrl(url));
            new Thread(() =>
            {
                try
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        TranslatorSetting.FetchModelsResult = "✗ 还没填 key，无法获取模型列表。";
                        return;
                    }
                    string resp = Http.Get(url, wc =>
                    {
                        // Gemini 的 key 已在 URL 查询参数里，不需要再带鉴权头。
                        if (name != "Gemini")
                            wc.Headers.Add(vendor == "claude" ? "x-api-key" : "Authorization",
                                           vendor == "claude" ? key : "Bearer " + key);
                        if (vendor == "claude") wc.Headers.Add("anthropic-version", AnthropicApi.Version);
                    });
                    List<string> models = vendor != null ? CustomAi.ParseModels(vendor, resp) : EngineKit.ParseModels(name, null, resp);
                    int total = models.Count;
                    // 反馈7：全部列出，免费的排在最前面。以前只留免费档，OpenRouter 428 个模型被砍到只剩 1 个，
                    // 玩家看着像坏了；现在既能选付费模型，也一眼看得见哪些不要钱。
                    List<string> ordered = EngineKit.FreeFirst(name, models);
                    int freeCount = 0;
                    foreach (string m in models) if (EngineKit.IsFreeModel(name, m)) freeCount++;
                    TranslatorSetting.SetFetchedModels(ordered);
                    // 反馈2/3：列表里已经没有当前这个模型 = 平台把它下架/改名/转付费了（OpenRouter 404、硅基流动 402
                    // 全是这一类），这时候顺手改用列表第一项（免费优先），否则玩家看到的是「列表能拉到、翻译全失败」。
                    string before = setting.AiModel;
                    string adopted = setting.AdoptFetchedModelIfStale();
                    if (adopted.Length > 0)
                        ModLog.Info("[模型列表] 原先的模型「" + before + "」平台已不提供，自动改用「" + adopted + "」（列表第一项，免费优先）。");
                    TranslatorSetting.FetchModelsResult = total == 0
                        ? "✗ 没解析出任何模型名（对方返回的内容可能变了，详见日志）。"
                        : adopted.Length > 0
                            ? $"✓ 已获取 {total} 个模型；原来的「{before}」平台已不提供，已自动改用「{adopted}」。"
                            : freeCount == 0
                                ? $"✓ 已获取 {total} 个模型，下拉框里可以选了。"
                                : $"✓ 已获取 {total} 个模型，其中 {freeCount} 个免费、已排在列表最前面。";
                    ModLog.Info("[模型列表] " + TranslatorSetting.FetchModelsResult);
                    // 关键一步：下拉框的候选项在【建页时】就被框架抓走存进控件了，RefreshPage 不会重取，
                    // 所以必须回主线程把新列表直接塞进那个控件，否则玩家看到的还是一个点不开的下拉框。
                    // 这一串都在后台线程，存盘与刷界面一律挪回主线程（SettingsStore.Save 只允许主线程碰）。
                    if (total > 0) MainThreadDispatcher.RunOnMainThread(() =>
                    {
                        Patches.PushModelItems();
                        if (adopted.Length > 0) VendorSwitched();          // 轻路径：把新模型名存盘 + 重刷界面，不重翻
                    });
                }
                catch (Exception ex)
                {
                    TranslatorSetting.FetchModelsResult = "✗ 获取失败: " + ex.Message;
                    ModLog.Error("[模型列表] 失败: " + ex.Message + " | 服务端：" + Probe.Trunc(Http.ErrorBody(ex) ?? "", 300));
                }
                finally { Interlocked.Exchange(ref _fetchingModels, 0); }
                MarkUiDirtyAndRefresh();
            }) { IsBackground = true, Name = "Cs2AutoTranslatorFetchModels" }.Start();
        }

        // 「保存设置并翻译」按钮回调（主线程）：先写盘并同步热路径镜像，再解除暂停、清运行时译文并按新设置重刷。
        private static void SaveNow()
        {
            if (!PersistSettingsOnly()) return;
            try
            {
                Patches.Paused = false;      // 需求2：点「保存设置并翻译」= 明确要（重新）翻译，解除清除缓存造成的暂停。
                TransMap.Reset();            // 目标语言/引擎可能变了：清空内存译文，按新设置重新解析。
                SeedCacheIntoTransMap();     // 用新「引擎+目标语言」把磁盘缓存命中的历史译文重新预载（需求9：切回旧目标即时恢复，不重翻）。
                _uiDirty = true; RefreshUi(true); // 立刻原地重刷当前可见文字：开启/换设置→重译，关闭总开关→全部回原文（需求8）。
                // 需求2：RefreshUi 只管【界面】文字。世界里的路名/区名/悬停标签卡在渲染系统的三份缓存里，
                // 而关掉总开关后 WorldLabels.Tick() 第一行就被 HookEnabled 挡掉，永远不会再去清 ——
                // 玩家看到的就是「关了翻译，画面上的译文还在」。所以这里无条件强制清一次：
                // 开 → 游戏按当前视野重新取名字并入队翻译；关 → 重新取回原文，画面立即恢复。
                WorldLabels.ForceRefresh(Patches.HookEnabled
                    ? "已保存设置并启用翻译，按当前视野重新取一遍世界文字"
                    : "已关闭翻译，世界文字恢复原文");
                // v1.1.5：关掉总开关是玩家明确说"这些译文我不要了"，所以【每一页】上我们写过的文字都要放回原文 ——
                // 世界文字那一头有 ForceRefresh 无条件兜底，界面这一头以前只靠"下一次扫描恰好在看那一页"，
                // 于是没打开的页会一直挂着旧语言的写法（同一格缺陷在清除缓存那一条上最扎眼，见 ClearCacheNow）。
                // 范围开关也走这条回调（TranslatorSetting.OnLiveSettingChanged += SaveNow），所以这里分两种：
                //   · 总开关关了 ⇒ 整树"整体不要"，全退回原文；
                //   · 总开关还开着、只是改了某一档范围/目标语言 ⇒ 整树"只退现在已归范围外的那几处"，
                //     玩家还想留着的译文一律不动（退了就得重翻 = 白烧额度），而且这一轮同样不排新活。
                if (!Patches.HookEnabled) DropdownL10n.RevertAll("已关闭翻译");
                else DropdownL10n.RevertOutOfScope("翻译范围或设置已改");
            }
            catch (Exception ex) { ModLog.Error("[保存] 应用异常: " + ex.Message); }
        }

        // 换 AI 厂商（反馈3）：写盘 + 重刷界面，仅此而已。与 SaveNow 的差别就是不做 TransMap.Reset() 和图层重刷。
        private static void VendorSwitched()
        {
            PersistSettingsOnly();
            MarkUiDirtyAndRefresh();
        }

        // 存盘前要清洗的凭据字段。写成表是为了「加一个引擎只改这张表」——
        // 漏一个的后果很阴：key 里混进输入法带来的不可见字符，写进 JSON 后肉眼看不出差别，但校验永远失败。
        private static readonly Func<TranslatorSetting, string>[] KeyGetters =
        {
            s => s.MyMemoryEmail, s => s.YandexKey, s => s.MicrosoftKey, s => s.MicrosoftRegion,
            s => s.DeepLKey, s => s.BaiduAppId, s => s.BaiduKey, s => s.GeminiKey, s => s.GroqKey,
            s => s.OpenRouterKey, s => s.SiliconFlowKey, s => s.CloudflareAccountId,
            s => s.CloudflareToken,
        };

        private static readonly Action<TranslatorSetting, string>[] KeySetters =
        {
            (s, v) => s.MyMemoryEmail = v, (s, v) => s.YandexKey = v, (s, v) => s.MicrosoftKey = v,
            (s, v) => s.MicrosoftRegion = v, (s, v) => s.DeepLKey = v, (s, v) => s.BaiduAppId = v,
            (s, v) => s.BaiduKey = v, (s, v) => s.GeminiKey = v, (s, v) => s.GroqKey = v,
            (s, v) => s.OpenRouterKey = v, (s, v) => s.SiliconFlowKey = v,
            (s, v) => s.CloudflareAccountId = v, (s, v) => s.CloudflareToken = v,
        };

        private static void CleanCredentials(TranslatorSetting s)
        {
            // 两个数组必须等长且一一对应（同一份字段清单的读写两面），长度不等就是漏改，直接跳过而不是错位赋值。
            if (KeyGetters.Length != KeySetters.Length) { ModLog.Error("[保存] 凭据字段读写表长度不一致，已跳过清洗。"); return; }
            for (int i = 0; i < KeyGetters.Length; i++) KeySetters[i](s, Keys.Clean(KeyGetters[i](s)));
            // 反馈3：AI 厂商的接口地址/key 按厂商分了 9 格，上表一个字段一行塞不下，交给设置类自己整表洗。
            s.CleanVendorCredentials();
        }

        // 只把当前选项写盘 + 同步到热路径静态字段，不碰运行时译文表、不刷新界面（需求5：测试连通性用它，
        // 免得「测试」按钮顺手把全局译文清空重翻）。返回是否成功写盘。
        private static bool PersistSettingsOnly()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) { ModLog.Warn("[保存] 选项尚未注册，无法保存。"); return false; }
            try
            {
                CleanCredentials(setting);

                bool ok = SettingsStore.Save(setting);
                if (ok) NoteSaved(setting);   // 反馈6：玩家自己存过一次，静默存盘就要认这个新基线，别 20 秒后又写一遍
                Patches.SyncSettings();  // 把总开关/范围/目标语言/引擎镜像到热路径静态字段（需求8/2）。
                // v1.1.7：玩家刚刚做过一个明确动作（保存/测试/拉列表/换引擎换厂商）⇒ 解除全部退避。
                // 没有这一条，玩家修好 key 之后还要干等熔断剩下的几分钟，看到的就是"我已经改好了却没反应"。
                // 引擎仍然是坏的话，下一条请求会立刻重新开一次熔断（streak 从 1 起，只歇 30 秒）。
                ResetBackoff();
                RegisteredModIds.Invalidate();   // 范围分类用到的模组 id 列表刷新一次。
                ModLog.Info($"[范围] 已注册模组 id：{RegisteredModIds.Get().Length} 个（0 个说明注入失效，「模组选项及说明」会静默不翻）。");
                TranslatorSetting.LastSaveResult = ok
                    ? $"✓ 已存盘：{SettingsStore.FilePath}"
                    : $"⚠ 保存失败：{SettingsStore.FilePath}（详见日志）";
                ModLog.Info("[保存] " + TranslatorSetting.LastSaveResult);
                return ok;
            }
            catch (Exception ex)
            {
                TranslatorSetting.LastSaveResult = "✗ 保存异常: " + ex.Message;
                ModLog.Error("[保存] 异常: " + ex);
                return false;
            }
        }

        // 「打开日志文件夹」按钮回调（需求8，主线程）：用系统文件管理器打开本模组专属日志所在目录，方便玩家取日志反馈。
        private static void OpenLogFolderNow()
        {
            try
            {
                string dir = Path.GetDirectoryName(ModLog.FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                if (Environment.OSVersion.Platform == PlatformID.Win32NT)
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                else
                    System.Diagnostics.Process.Start(dir);
                ModLog.Info("[日志] 已尝试打开日志文件夹：" + dir);
            }
            catch (Exception ex) { ModLog.Error("[日志] 打开日志文件夹失败: " + ex.Message); }
        }

        // 「清除缓存」按钮回调（需求10，主线程）：清空磁盘+内存译文缓存 → 全部回原文，并且【顺手关掉总开关】。
        // 反馈7：上一版刻意「不碰任何设置」，可暂停只是内存里的一个 bool —— 盘上 Enabled 仍是 true，
        // 于是重启后模组自己把整个存档重翻一遍，玩家看到的正是「我只是清了缓存，怎么又从头翻了」。
        // 清缓存的语义本来就是「这些译文我不要在」，所以关掉总开关才自洽：重启后也不会自动开翻。
        // ⚠ 顺序：先清盘再关开关。关开关触发的 SaveNow 会 TransMap.Reset + SeedCacheIntoTransMap + 刷界面 +
        // 刷世界文字，正好把「清完并保持原文」一次做完；这里再自己刷一遍就是连卡两下（见 RetranslateNow 上方注释）。
        private static void ClearCacheNow()
        {
            try
            {
                TranslationCache.Clear();        // 清内存 Dict + 删 translation.cache 文件（含 "self" 命名空间）
                TransMap.Reset();                // 清运行时译文 → Prefix 全部放行原文
                // v1.1.5 关键一步：把我们写进控件的译文【当场】放回原文，整棵树都走一遍。
                // 以前这一步是缺的 —— 还原只能等 DropdownL10n 的下一轮扫描，而那一轮从 v1.1.4 起
                // 只看玩家当前打开的那一页：实机凭据（2026-10-02）是 20:22:36 还在 AccessAnarchy 页
                // 上屏韩语、20:22:44 清代之后日志里【一行 [下拉框] 都没有】、20:22:47 第一条的活动页
                // 已经换成 ToolModeMemory（那一页退了 60 项），AccessAnarchy 那一页则落进
                // 「整页跳过 23 页」里再没人管 ⇒ 玩家眼睛里的"缓存清了、字还是外语"。
                // ⚠ 为什么放在翻总开关【之前】：还原扫描自己就把 enabled 当假（Scan 的 forceOff 那一格），
                // 不依赖 HookEnabled / Paused 的先后；而紧跟着的 SaveNow 里还有一次 RevertAll（关总开关那条路
                // 自己也要还原），放在前面的话【这一次点击只扫一遍】，日志里那一行写的也就是「已清除缓存」这个原因。
                DropdownL10n.RevertAll("已清除缓存");
                // v1.2.4 需求1：悬停凭据也是"翻译痕迹"的一种。不清它的话，点完清缓存再打开选项页，
                // 先前 hover 过的行会【不用再 hover 就自动送翻】—— 玩家看到的正是"这个还有记忆功能？"。
                // 放在 RevertAll 之后：还原那趟要看凭据表（s_Sweep 为真时不看闸，所以先后其实无碍），
                // 放这儿是为了让日志里"已清除缓存"那条一口气说完清了什么。
                DropdownL10n.ForgetHoveredDescriptions();
                TranslatorSetting s = TranslatorSetting.Instance;
                bool wasOn = s != null && s.Enabled;
                if (wasOn) s.Enabled = false;    // setter → OnEnabledChanged → SaveNow：写盘 + 可见文字全部回原文
                else
                {
                    // 总开关本来就是关的 → setter 同值不触发 → SaveNow 不会跑，这一次刷新得自己补。
                    _uiDirty = true; RefreshUi(true);
                    WorldLabels.ForceRefresh("已清除缓存，世界文字保持原文");
                }
                Patches.Paused = true;           // 放在 SaveNow 之后：它会把 Paused 打回 false，而路上那几条请求还得压住
                ModLog.Info("[缓存] 用户点击「清除缓存」：已清空所有翻译痕迹，并已关闭「启用翻译」总开关，"
                            + "界面与画面保持原文。想重新翻译请重开总开关或按快捷键（重启也不会自动开翻）。");
            }
            catch (Exception ex) { ModLog.Error("[缓存] 清除失败: " + ex.Message); }
        }

        // ===== 需求6：两个快捷键（启用/关闭翻译、重新翻译）—— 反馈5 定稿：游戏内置绑定控件 =====
        // 界面上那两行是 [SettingsUIKeyboardBinding]（玩家按一下键就完成设置，见 Setting.cs），
        // 这里负责让它真的响。关键一步是 shouldBeEnabled：ModSetting.RegisterKeyBindings() 只把 action
        // 【注册】进游戏输入系统，并不会启用它 —— 游戏自己的输入上下文只管自己的 action，模组的没人管。
        // 上一轮就是漏了这一步，才误判成「这条链是死路」（registered=True、action 也拿到了，却永远不触发）。
        // 挂在 Patches.EnsureApplied 上（游戏自己的 MainThreadDispatcher）：主线程，且模组【关闭时】照样每帧跑，
        // 否则「启用翻译」这个键永远按不出来。
        private static Game.Input.ProxyAction _toggleAction;
        private static Game.Input.ProxyAction _retranslateAction;
        private static float _nextArmTry;
        private static float _lastToggleFire;
        private static float _lastRetranslateFire;

        // 第五轮：这两条是「幻影按下」的闸门。注册成功／改绑定那一刻，输入系统会给刚启用的 action 补一次
        // 初始状态检查，那一下不是玩家按的；未绑键时框架的默认路径又是设备级（整块键盘都算按下）。
        // 两者叠起来就是实机看到的「重开游戏，模组自己清了缓存重翻一遍」，所以启用后先哑火 kGraceAfterArm 秒。
        private const float kGraceAfterArm = 3f;
        private static float _toggleGraceUntil;
        private static float _retranslateGraceUntil;
        private static bool _toggleBound;         // 这一跳读到的「真绑了一颗控件」，触发时直接用，不在每帧枚举绑定表
        private static bool _retranslateBound;
        // 第六轮：「这一行本局落地过一颗键」的闩。没落过地才回填（补上一局的键），落过地之后它变空只可能是
        // 玩家在界面里解除了 —— 那种空是意图，必须记下来并落盘，否则每两秒被上一局的键顶回去。
        private static bool _toggleLanded;
        private static bool _retranslateLanded;

        // 两次触发的最小间隔（秒）：按住不放不该连发。
        private const float kMinGap = 0.5f;

        // 反馈6：这一跳除了重试注册，还兼两件事——①把界面上刚改的按键抄进盘并落盘，②把后台悄悄涨上来的
        // token 用量定频落盘。两件事原本【没有任何触发口】：玩家绑完键不会去点「保存设置并翻译」，
        // 于是重启后键是空的、用量掉回上一次落盘那一刻。
        private const float kTick = 2f;                 // 对账节奏：绑一次键的间隔远大于它，不会拖泥带水
        private const float kFlushInterval = 20f;       // 用量最少隔这么久才写一次盘，免得每 2 秒碰磁盘
        private static float _nextFlush;
        private static long _lastSavedTokens = -1;      // 上一次落盘时盘上应有的合计；-1＝本局还没记过基线
        private static string _lastSavedModel;          // 上一次落盘时盘上的模型栏签名；null＝本局还没记过基线
        private static int _restoreTriesToggle;         // 回填次数：读回已经是那个键就清零，所以只惩罚「怎么写都不进」
        private static int _restoreTriesRetranslate;
        private const int kMaxRestores = 8;

        // ModSetting 把「属性里的绑定」推到「注册出来的 action」上的那一步是 nonpub 的：反射一次拿住，
        // 之后每次改完属性都要调它，否则选项页显示的是新键、实际响的还是没有。
        private static readonly System.Reflection.MethodInfo _applyKeyBindings = typeof(Game.Modding.ModSetting)
            .GetMethod("ApplyKeyBindings", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        internal static void PollHotkeys()
        {
            TranslatorSetting s = TranslatorSetting.Instance;
            if (s == null) return;

            float now = UnityEngine.Time.realtimeSinceStartup;
            if (now >= _nextArmTry)
            {
                _nextArmTry = now + kTick;      // 每两秒一跳：注册要等选项页与输入系统都就绪，没成下一跳再试
                if (_toggleAction == null) _toggleAction = Arm(s, TranslatorSetting.kActionToggle);
                if (_retranslateAction == null) _retranslateAction = Arm(s, TranslatorSetting.kActionRetranslate);
                ReconcileTick(s, now);
                SyncEnabled(s, _toggleAction, TranslatorSetting.kActionToggle, ref _toggleBound, ref _toggleGraceUntil, now);
                SyncEnabled(s, _retranslateAction, TranslatorSetting.kActionRetranslate, ref _retranslateBound, ref _retranslateGraceUntil, now);
            }

            if (FireReady(_toggleAction, _toggleBound, now, _toggleGraceUntil, ref _lastToggleFire))
            {
                s.Enabled = !s.Enabled;   // setter 同值不触发；这里必定变值 → OnEnabledChanged → SaveNow（存盘+立即生效）
                ModLog.Info("[快捷键] 启用/关闭翻译 → 现在是「" + (s.Enabled ? "开" : "关") + "」。");
            }

            if (FireReady(_retranslateAction, _retranslateBound, now, _retranslateGraceUntil, ref _lastRetranslateFire))
            {
                ModLog.Info("[快捷键] 重新翻译：清除缓存并重新执行翻译。");
                RetranslateNow();
            }
        }

        // 注册 + 取句柄。⚠ 这里【不启用】：启用交给 SyncEnabled 独占，因为「启用一颗没绑键的 action」
        // 等于把整块键盘当成热键（实机就是这么自己清了缓存的）。任何一步没成只返回 null，外层每 2 秒再试。
        private static Game.Input.ProxyAction Arm(TranslatorSetting s, string actionName)
        {
            try
            {
                if (!s.keyBindingRegistered) s.RegisterKeyBindings();
                Game.Input.ProxyAction a = s.GetAction(actionName);
                if (a == null) return null;
                ModLog.Info("[快捷键] " + actionName + "：注册完成 isSet=" + a.isSet
                    + " 界面按键=" + BindingShown(s, actionName)
                    + " 提交口=" + (_applyKeyBindings != null ? "有" : "无"));
                return a;
            }
            catch (Exception ex)
            {
                ModLog.Warn("[快捷键] 启用 " + actionName + " 失败: " + ex.Message);
                return null;
            }
        }

        // 一颗快捷键能不能算「按下」。三道闸，全是为了「玩家没碰键盘时模组不许自己动」：
        // ① 没绑控件不响（未绑时框架给的默认路径是设备级，整块键盘都算按下，见 Display.IsBindablePath）；
        // ② 刚启用／刚改完绑定的 kGraceAfterArm 秒内不响——输入系统会在这一刻补一次初始状态检查，
        //    那一下不是玩家按的；
        // ③ 两次触发至少隔 kMinGap。
        private static bool FireReady(Game.Input.ProxyAction a, bool bound, float now, float graceUntil, ref float lastFire)
        {
            if (a == null || !bound) return false;
            if (now < graceUntil) return false;
            if (now - lastFire <= kMinGap) return false;
            if (!a.WasPressedThisFrame()) return false;
            lastFire = now;
            return true;
        }

        // 启用与否由这一处独占：只有真的绑了一颗控件才开。从关到开的那一刻起算宽限期，
        // 躲开输入系统给「刚启用的 action」补的那次初始状态检查。
        private static void SyncEnabled(TranslatorSetting s, Game.Input.ProxyAction a, string actionName,
                                        ref bool bound, ref float graceUntil, float now)
        {
            if (a == null) { bound = false; return; }
            string live = LivePath(a);
            string prop = PropertyPath(s, actionName);
            bool wantBound = Display.IsBindablePath(live) || Display.IsBindablePath(prop);
            if (a.shouldBeEnabled != wantBound) a.shouldBeEnabled = wantBound;
            if (wantBound != bound)
            {
                // 宽限期只在「从不响到该响」那一刻开一次。判据用我们自己上一跳的状态，不用框架的 getter：
                // 万一它不认这次写入，每跳都重开一次宽限期就等于快捷键永远响不了。
                if (wantBound) graceUntil = now + kGraceAfterArm;
                ModLog.Info("[快捷键] " + actionName + "：" + (wantBound
                    ? "已启用，按键=" + (live.Length > 0 ? live : prop)
                    : "没有按键，暂不启用（否则整块键盘都会被当成热键）"));
            }
            bound = wantBound;
        }

        // 反馈1/6：界面那两行 ↔ 自管 JSON 那两条值，每两秒对一次账；顺带把后台涨上来的 token 定频落盘。
        // 首跳只记基线不写盘：刚 Load 完的量是上一局存进来的，不是本局的新账。
        private static void ReconcileTick(TranslatorSetting s, float now)
        {
            bool dirty;
            try { dirty = ReconcileBindings(s); }
            catch (Exception ex) { dirty = false; ModLog.Warn("[快捷键] 对账失败: " + ex.Message); }

            // 模型栏是【手填/手选一次就完事】的东西：填完不会再去点「保存设置并翻译」，而它又不像范围开关
            // 那样挂 OnLiveSettingChanged。只由用量驱动的静默存盘要等到 token 变了才顺手把它带走 ——
            // 玩家换了个不烧额度的档（用量不涨）就是「填了、重启又没了」。所以模型也当一条独立的判据。
            string model = ModelSignature(s);
            if (_lastSavedModel == null) _lastSavedModel = model;      // 首跳只记基线
            else if (model != _lastSavedModel) dirty = true;

            long tokens = s.TrackedTokens;
            bool firstTick = _lastSavedTokens < 0;
            if (firstTick)
            {
                _lastSavedTokens = tokens;
                _nextFlush = now + kFlushInterval;
            }
            // 首跳不为了用量写盘（那份就是刚 Load 进来的），但脏了照写：第一跳就采纳到按键时，
            // 下一跳 live 已等于 saved 再不会置脏，那个键就永远存不进盘。
            bool flushDue = tokens != _lastSavedTokens && now >= _nextFlush;
            if (!dirty && !flushDue) return;

            if (SettingsStore.Save(s, true)) NoteSaved(s);
        }

        // 落盘里那两条模型值的签名：格与格之间用 \n 隔开（值里不会出现换行）。
        // 两个都要：厂商选「自定义」时界面是手填文本框（CustomModelName），八家内置厂商仍是下拉框（AiModel），
        // 只盯一个就会漏掉另一条路；而这两个属性在切引擎/切厂商时被刻意清空，漏判就是「切完重启模型没了」。
        private static string ModelSignature(TranslatorSetting s) => (s.AiModel ?? "") + "\n" + (s.CustomModelName ?? "");

        // 任何一次落盘成功后都要走这里：静默存盘靠这两个值判断「盘上此刻是什么」，
        // 不然玩家自己点了保存，下一跳还会再白写一遍文件。
        internal static void NoteSaved(TranslatorSetting s)
        {
            _lastSavedTokens = s == null ? 0 : s.TrackedTokens;
            _lastSavedModel = s == null ? "" : ModelSignature(s);
            _nextFlush = UnityEngine.Time.realtimeSinceStartup + kFlushInterval;
        }

        // 反馈6：双向同步。一行键只可能有三种来历，逐条对上：
        // ①【界面上有真键】→ 采纳进盘并置脏。玩家在游戏里按一下键就绑好了，他不会为这件事再去点
        //    「保存设置并翻译」，不采纳就等于没存 → 重启后自然是空的。
        // ②【界面空、盘上有键、这一行本局还没落过地】→ 走 CommitBinding 回填，最多 kMaxRestores 次。
        //    「没落过地」＝这一行的键是我们从盘上搬回来的，界面上还没站稳（旧版这里就是被顶回默认）。
        // ③【界面空、盘上有键、这一行本局已经落过地】→ 判成玩家自己解除了：记下空值并落盘，且【不再回填】。
        //    上一版把 ② 无条件做，代价就是实机看到的「解除按键／点重置，过一秒那颗键又回来了」——
        //    每两秒一跳的对账在跟玩家抢那一行。
        // 为什么 landed 能分清 ② 和 ③：玩家只能解除他看得见的键，而界面上有键＝属性是 bindable＝第 ① 条
        // 那一跳就把 landed 置真了；开局从盘上搬回来的那颗，要等下一跳在第 ① 条里读到真键才算落地。
        // 已知风险（诚实记着）：万一框架某天在【已经落地之后】自己把那行打回默认，会被误读成玩家解除、
        // 空值落盘 → 重启后键真没了。判据是日志那句「界面里解除了 …」——玩家当时没点解除却出现这行就是误读。
        // 误读也留了自愈口：之后任何一跳属性重新出现真键，第 ① 条会立刻把它采纳回来。
        // 盘上躺着一条【不成键】的脏值（旧版写坏的 "<Keyboard>/" 就是这种）时直接丢掉并落盘，
        // 否则每跳都白回填一次，而那颗键本来就永远不会响。
        private static bool ReconcileBindings(TranslatorSetting s)
        {
            bool dirty = SyncOne(s, _toggleAction, TranslatorSetting.kActionToggle,
                ref s.SavedToggleKey, ref _toggleLanded, ref _restoreTriesToggle);
            return SyncOne(s, _retranslateAction, TranslatorSetting.kActionRetranslate,
                       ref s.SavedRetranslateKey, ref _retranslateLanded, ref _restoreTriesRetranslate)
                   || dirty;
        }

        private static bool SyncOne(TranslatorSetting s, Game.Input.ProxyAction a, string actionName,
                                    ref string saved, ref bool landed, ref int tries)
        {
            string raw = PropertyPath(s, actionName);
            if (Display.NormalizeBindingPath(saved).Length > 0 && !Display.IsBindablePath(saved))
            {
                saved = "";
                ModLog.Warn("[快捷键] 盘上那条 " + actionName + " 不成键，已丢弃：" + raw);
                return true;
            }

            // 采纳只看属性那份：玩家在界面按键，框架写的就是它；action 上那份要等提交口才跟上。
            if (Display.IsBindablePath(raw))
            {
                landed = true;      // 这一行【落地过】：此后它变空只可能是玩家自己解除了
                if (raw == Display.NormalizeBindingPath(saved)) return false;
                saved = raw;
                ModLog.Info("[快捷键] 记下令牌：" + actionName + " → " + raw);
                return true;
            }

            if (saved.Length == 0) return false;
            if (landed)
            {
                saved = "";
                ModLog.Info("[快捷键] 界面里解除了 " + actionName + " 的按键，按「没有按键」记下来。");
                return true;
            }
            if (a == null || tries >= kMaxRestores) return false;
            tries++;
            CommitBinding(s, a, actionName, saved, tries);
            // 只清计数，不置 landed：回填「写进去了」不等于「界面上真站住了」，
            // 站稳由下一跳的第 ① 条（属性里读到真键）认定，省掉一个自证自的口子。
            if (LivePath(a) == saved || PropertyPath(s, actionName) == saved) tries = 0;
            return false;
        }

        // 读选项属性里那条绑定的按键路径（归一化，取不到一律算空）。这一份是【选项页渲染的数据源】。
        private static string PropertyPath(TranslatorSetting s, string actionName)
        {
            try { return Display.NormalizeBindingPath(ReadBinding(s, actionName).path); }
            catch { return ""; }
        }

        private static Game.Input.ProxyBinding ReadBinding(TranslatorSetting s, string actionName)
        {
            return actionName == TranslatorSetting.kActionToggle
                ? s.ToggleTranslationBinding : s.RetranslateBinding;
        }

        // ⚠ ProxyBinding 是 struct：想改它，只能把整个新结构【赋回属性】。
        // 「取出来 → b.path = x」改的是一份副本，游戏里从来没生效过 —— 上一版快捷键重启就空在这里。
        private static void WriteBinding(TranslatorSetting s, string actionName, Game.Input.ProxyBinding b)
        {
            if (actionName == TranslatorSetting.kActionToggle) s.ToggleTranslationBinding = b;
            else s.RetranslateBinding = b;
        }

        // 读 action 上真在跑的那几条绑定 —— 【权威】那份。上一版的错就错在拿本地 struct 副本读回自己验证，
        // 于是日志里永远「写成功了」，游戏里那行一直是空的。
        // 组合键在 action 上是【一条一条】挂的（修饰键一段、主键一段），所以要拼起来才跟盘上那串同形，
        // 否则「读回已经等于要写的值」这条判据对组合键永远不成立。
        private static string LivePath(Game.Input.ProxyAction a)
        {
            if (a == null) return "";
            try
            {
                var sb = new StringBuilder();
                foreach (Game.Input.ProxyBinding b in a.bindings)
                {
                    string p = b.path;
                    if (string.IsNullOrEmpty(p)) continue;
                    if (sb.Length > 0) sb.Append(',');
                    sb.Append(p);
                }
                return Display.NormalizeBindingPath(sb.ToString());
            }
            catch { return ""; }
        }

        // 把上一局的键真的写回框架。两步缺一不可，上一版两步都没做到：
        // ① ProxyBinding 是 struct：必须用 WithPath 造一份新的【整体赋回属性】；「取出来改 .path」只改到副本。
        // ② 属性只是数据源，运行时吃的是注册出来的 action：还得让 ModSetting.ApplyKeyBindings 推上去
        //    （它是 nonpub，反射调；拿不到这一步就只能等玩家自己重按一次键）。
        private static void CommitBinding(TranslatorSetting s, Game.Input.ProxyAction a, string actionName,
                                          string want, int attempt)
        {
            try
            {
                WriteBinding(s, actionName, ReadBinding(s, actionName).WithPath(want));
                if (_applyKeyBindings != null) _applyKeyBindings.Invoke(s, null);
                ModLog.Info("[快捷键] 第 " + attempt + " 次把上一局的按键写回：" + actionName + " → " + want
                    + "（读回 属性=" + PropertyPath(s, actionName) + " action=" + LivePath(a) + "）");
            }
            catch (Exception ex) { ModLog.Warn("[快捷键] 写回 " + want + " 失败: " + ex.Message); }
        }

        // 反馈1/6：存盘前把界面上那条绑定【当前】的按键抄进自管 JSON。玩家是在游戏里按下键的，模组这边
        // 只有读的份，所以只能在写盘这一刻取一次。⚠ 空值不无脑覆盖：这一行本局还没落过地时读到空，多半是
        // 框架那行还没渲染出来（上一版丢键真正的两个根因是 struct 副本写入和写盘少一个闭合引号，见交接文档第 52/53 条），
        // 照抄就等于彻底抹掉玩家的键；落过地之后才把空值当成「玩家自己解除了」。设备级的 "<Keyboard>/"
        // 比空值更坏——它会让整块键盘变成热键，任何时候都不许进盘。
        internal static void CaptureBindings(TranslatorSetting s)
        {
            try
            {
                CaptureOne(s, TranslatorSetting.kActionToggle, ref s.SavedToggleKey, _toggleLanded);
                CaptureOne(s, TranslatorSetting.kActionRetranslate, ref s.SavedRetranslateKey, _retranslateLanded);
            }
            catch { /* 读不到就当没变，下一个存盘点再抄 */ }
        }

        // 跟 SyncOne 同一套判据：有真键就抄，空值只在【这一行已经落过地】时才落成「没有按键」。
        // 不这么做的话，玩家解除完立刻点「保存设置并翻译」——那一跳对账还没跑到，存下去的还是上一颗键。
        private static void CaptureOne(TranslatorSetting s, string actionName, ref string saved, bool landed)
        {
            string live = PropertyPath(s, actionName);
            if (Display.IsBindablePath(live)) { saved = live; return; }
            if (landed) saved = "";
        }

        // 该 action 界面上那条绑定当前实际生效的按键路径（只为日志可读，取不到就当未知）。
        private static string BindingShown(TranslatorSetting s, string actionName)
        {
            string path = PropertyPath(s, actionName);
            return Display.IsBindablePath(path) ? path : "(未按键)";
        }

        // 「重新翻译」= 清空缓存 + 立刻按当前设置重翻。
        // 刻意不复用 ClearCacheNow()+SaveNow()：那两个各自会强制刷一次世界文字（含一次 ReloadActiveLocale，
        // 重读整张语言字典不便宜），连做两次会明显卡一下。这里合成一条路径，只刷一次。
        private static void RetranslateNow()
        {
            try
            {
                TranslationCache.Clear();        // 清内存 Dict + 删 translation.cache 文件
                TransMap.Reset();                // 清运行时译文
                DropdownL10n.RevertAll("玩家触发重新翻译");   // v1.1.5：把我们写进控件的旧译文当场放回原文
                Patches.Paused = false;          // 与 ClearCacheNow 相反：不是要停在原文，而是要马上重翻
                if (!PersistSettingsOnly()) return;
                SeedCacheIntoTransMap();         // 磁盘缓存刚清掉，这里等于空跑；保留是为了跟 SaveNow 走同一条路径
                _uiDirty = true; RefreshUi(true);
                WorldLabels.ForceRefresh("玩家触发重新翻译，世界文字按当前视野重新取名字并入队");
                ModLog.Info("[快捷键] 已清空缓存并按当前设置重新开始翻译。");
            }
            catch (Exception ex) { ModLog.Error("[快捷键] 重新翻译失败: " + ex.Message); }
        }

        // 去掉控制字符与首尾空白那件事搬到 TextKit.cs 的 Keys.Clean 了（v1.1.7）。
        // 原来它待在这里，方法体 100% 纯 BCL 却因为 `class Mod : IMod` 的声明而进不了离线壳 —— §10 第 29 条点名的就是它。

        // v1.1.6：一次性引导用的主线程探针。把「本地化管理器是否就绪 + 当前语言」抄进
        // 托管静态字段（Patches.LocalizationReady / Patches.ActiveLocale），后台线程只等字段。
        // 为什么：Bootstrap 跑在后台工作线程上，而 GameManager/localizationManager 是
        // Unity/Colossal 原生对象，跨线程直接读属于偶发、难复现的崩溃来源。这里改为主线程
        // 每帧探一次（与守护 updater 每 2 秒刷新 ActiveLocale 同一思路），就绪后停。
        // _probing=false 时不再自我续期：Bootstrap 超时返回后探针自动熄灭，不空转。
        private static volatile bool _probing;

        private static void ProbeLocalizationOnMainThread()
        {
            MainThreadDispatcher.RunOnMainThread(() =>
            {
                try
                {
                    var lm = GameManager.instance?.localizationManager;
                    if (lm != null)
                    {
                        string al = lm.activeLocaleId;
                        if (!string.IsNullOrEmpty(al)) Patches.ActiveLocale = al;
                        int entries = lm.activeDictionary?.entryCount ?? 0;
                        if (entries > 100)
                        {
                            if (!Patches.LocalizationReady)
                                ModLog.Info($"[线程] 本地化管理器就绪，activeLocaleId={al}，字典 {entries} 条。");
                            Patches.LocalizationReady = true;
                        }
                    }
                }
                catch (Exception ex) { ModLog.Error("[线程] 等待本地化异常: " + ex.Message); }
                if (_probing && !Patches.LocalizationReady) ProbeLocalizationOnMainThread();
            });
        }

        private static bool WaitFor(Func<bool> cond, int seconds)
        {
            for (int i = 0; i < seconds; i++)
            {
                Thread.Sleep(1000);
                try { if (cond()) return true; }
                catch { /* 就绪前某些访问会抛异常，忽略后重试 */ }
            }
            return false;
        }

        // IMod 的正式拆除入口。原 BepInEx 版本里这里有两个 MonoBehaviour 钩子：
        //   OnDestroy()          —— 故意什么都不做，因为游戏开机/切场景就会销毁插件 GameObject（v0.20 踩过坑）；
        //   OnApplicationQuit()  —— 实测永不触发，因为 GameObject 早被销毁了。
        // 这两个问题在 IMod 下都不存在：本类不是 MonoBehaviour，没有 GameObject 生命周期，
        // 游戏卸载模组时直接调 OnDispose（Modding.log 里的 "Disposed ..." 行可证）。所以两个钩子合并成这一个。
        public void OnDispose()
        {
            BeginShutdown("OnDispose");
            UninstallGuard();
            FlushOnExit("OnDispose");
            // v1.1.3 卸载安全：拆掉我们自己在游戏方法上挂的两个补丁（顺序在 UninstallGuard 之后，
            // 所以守护不会再把它们打回去）。控件里写过的译文不用逐个还原：那些控件（选项页、候选项、
            // NamedWidget 行）都是游戏每次建页重新生成的对象，页面一重建就是原版文字。
            Patches.UnpatchOwn(2);
            // 官方模板的做法：把注册进选项界面的设置注销掉，避免残留一个指向已卸载模组的设置页。
            try
            {
                TranslatorSetting s = TranslatorSetting.Instance;
                if (s != null) s.UnregisterInOptionsUI();
            }
            catch (Exception ex) { ModLog.Error("[生命周期] 注销选项界面异常: " + ex.Message); }
            Instance = null;
            ModLog.Info("[生命周期] OnDispose 完成。");
        }

        // 反馈6：退出前把这一局的用量与按键补写一次盘。中途虽有 20 秒一跳的静默存盘，但最后那一段
        // （往往正是玩家「一路用到底才关游戏」的量）没有别的触发口，不补这一次就永久丢在内存里，
        // 重启后数字掉回去，看起来就像「token 越用越少」。
        // 只允许主线程碰：Application.quitting 与 OnDispose 都在主线程，所以挂在它们身上。
        private static bool _finalFlushed;
        private static void FlushOnExit(string from)
        {
            if (_finalFlushed) return;
            try
            {
                TranslatorSetting s = TranslatorSetting.Instance;
                if (s == null) return;
                _finalFlushed = true;
                if (SettingsStore.Save(s, true)) NoteSaved(s);
                ModLog.Info("[配置] 退出前补写一次（" + from + "）。");
            }
            catch (Exception ex) { ModLog.Warn("[配置] 退出前补写失败: " + ex.Message); }
        }

        // Application.quitting 回调（静态方法：不碰可能已被销毁的 MonoBehaviour 成员）。Unity 在主线程触发。
        private static void OnUnityQuitting()
        {
            _quittingSeen = true;   // 先置位：即使 BeginShutdown 已被其它路径抢先返回，拆除标记也可靠。
            FlushOnExit("Application.quitting");   // 赶在 BeginShutdown 之前：那条路只管拆，不再写盘
            // v1.2.4 需求4：把更早的会话从日志里裁掉（只留最近 10 次游戏）。
            // 位置是刻意选的 —— 排在 FlushOnExit【之后】（设置先落盘，这一格不跟它抢同一批文件）、
            // BeginShutdown【之前】（那时还在正常状态，不是"进程已拆一半"）。
            // 方法内部全程 try/catch 且不抛不 Sleep：裁剪失败只是这一局不裁，绝不影响退出。
            ModLog.TrimSessionsOnExit();
            BeginShutdown("Application.quitting");
            UninstallGuard();
        }

        // 退出总收口（需求：修「退出到桌面卡死无响应」）。可在任意线程调用，只做线程安全且必需的事：
        // ① 停后台循环；② 关掉热路径（前缀立刻放行原文，拆除期不再入队、不再改 data）；
        // ③ 中止所有在飞的联网请求——否则后台线程正堵在原生 socket 读上时，Unity 的 Thread.Abort 无法投递，
        //    退出就得干等默认 100s/300s 超时，表现即「游戏直接卡死无响应」；④ 把攒下的译文追加落盘。
        internal static void BeginShutdown(string reason)
        {
            if (_shutdown) return;
            _shutdown = true;
            try
            {
                ModLog.Info($"[生命周期] 检测到退出（{reason}）：停止后台循环、热路径放行原文、中止在飞请求。");
                Patches.HookEnabled = false;
                Patches.MarkerEnabled = false;
                Http.AbortActive();
                TranslationCache.Flush(true);
            }
            catch (Exception ex) { ModLog.Error("[生命周期] 退出收口异常: " + ex.Message); }
            // 需求3（退出后恢复原文）天然满足：下次启动本模组不加载→不打补丁→文字即原文。
        }

        // 注销「补丁守护」updater。只能主线程调用（Application.quitting / OnApplicationQuit 均在主线程）。
        // 注意：这里【不再】调 Patches.Remove()——进程即将结束，拆补丁无收益，
        // 而在 UI/世界正在销毁时反射执行主线程动作反而是卡死的典型来源（v0.27 的疑点）。
        private static void UninstallGuard()
        {
            try
            {
                if (_updaterGuid.HasValue)
                {
                    MainThreadDispatcher.UnregisterUpdater(_updaterGuid.Value);
                    _updaterGuid = null;
                }
            }
            catch (Exception ex) { ModLog.Error("[生命周期] 注销守护异常: " + ex.Message); }
        }

        // 是否已进入退出/拆除阶段。任一为真即停手：
        // ① 我们自己的 _shutdown；② Application.quitting 已触发过（Unity 官方退出通知，主线程）；
        // ③ GameManager.instance 已为 null（游戏单例已被销毁，日志实证拆除期会先全局 UnpatchAll）。
        // 注：不用 Unity 的 Application.isQuitting —— 本游戏裁剪过 UnityEngine.CoreModule，编译期没有该属性（实测 CS0117）。
        internal static bool IsTearingDown()
        {
            if (_shutdown || _quittingSeen) return true;
            try { if (GameManager.instance == null) return true; } catch { }
            return false;
        }
    }

    // ===== Harmony 补丁：Hook 所有 Cohtml UI 文字的唯一出口 UILocalizationManager.Translate。=====
    // 参考 BetterChineseNames（同款 patch）与 I18NEverywhere（patch 内层 TryGetValue）。
    internal static class Patches
    {
        // v1.1.6：占位 id 换成自己的反域名 id。HarmonyId 只在本局内标识"哪些补丁是我们挂的"
        //（IsApplied / UnpatchAll 的 owner 判据），不跨会话持久化，所以换号无兼容问题；
        // 留 "com.yourname" 这种占位名，将来跟别的模组撞号时无法定责。
        private const string HarmonyId = "com.yuexian.cs2autotranslator";
        private static Harmony _harmony;
        private static MethodInfo _translateMethod;
        private static float _lastCheck;
        private static bool _firstEnqueueLogged;

        // 待翻译静态标记。用「…」是因为它在全部 12 种目标语言的字体里都一定有字形（不会变成豆腐块□）。
        // 由后台翻译循环按「引擎是否已配置」开关：没填 key 时不显示标记，免得永远挂着省略号。
        internal const string PendingMarker = "…";
        internal static volatile bool MarkerEnabled;

        // 需求2：用户点「清除缓存」后置 true → 暂停按需翻译，界面保持原文（否则清完立刻又被重新填满，清除无意义）。
        // 由「保存设置并翻译」按钮或切换总开关解除。与总开关 HookEnabled 正交：即便 Enabled=true 也可被暂停。
        internal static volatile bool Paused;

        // ===== 热路径镜像字段（需求 2/8）=====
        // 把 TranslatorSetting 的开关镜像成静态字段，避免 Prefix 每条都读 Instance 的属性链（性能，需求10）。
        // 由 SyncSettings() 在启动与每次保存时刷新。
        internal static volatile bool HookEnabled;              // 总开关：false 时 Prefix 全放行原文（需求8）
        internal static volatile bool ScopeModName, ScopeModOptions, ScopeAssetNames, ScopeAssetDesc, ScopeWorldLabels, ScopeGameCore;
        internal static volatile string TargetLocale = "zh-HANS";
        internal static volatile string ActiveLocale = "";        // 游戏当前显示语言 id（如 en-US / zh-HANS），供同语言跳过判定用；由 SyncSettings/守护循环刷新。
        // v1.1.6：本地化是否就绪（主线程探针抄到「字典有内容」）。Bootstrap 跑在后台线程，
        // 只等这个托管字段，不在后台直接读 GameManager/localizationManager（跨线程硬读是崩溃源）。
        internal static volatile bool LocalizationReady;
        internal static volatile string EngineCacheName = "google";

        // 从 TranslatorSetting.Instance 把开关/目标语言/引擎名镜像到上面的静态字段。主线程或后台线程皆可调用。
        // v1.1.6：不再在这里读 ActiveLocale —— 此前这行在后台线程（Bootstrap 调它）直接读
        // GameManager/localizationManager（跨线程硬读是崩溃源）。ActiveLocale 由主线程负责：
        // 引导时靠 ProbeLocalizationOnMainThread、运行期靠守护 updater 每 2 秒刷新。
        public static void SyncSettings()
        {
            try
            {
                TranslatorSetting s = TranslatorSetting.Instance;
                if (s == null) return;
                HookEnabled = s.Enabled;
                ScopeModName = s.ScopeModName;
                ScopeModOptions = s.ScopeModOptions;
                ScopeAssetNames = s.ScopeAssetNames;
                ScopeAssetDesc = s.ScopeAssetDescriptions;
                ScopeWorldLabels = s.ScopeWorldLabels;
                ScopeGameCore = s.ScopeGameCore;
                TargetLocale = string.IsNullOrEmpty(s.TargetLocale) ? "zh-HANS" : s.TargetLocale;
                EngineCacheName = EngineKit.CacheName(s.TranslationEngine.ToString());
            }
            catch (Exception ex) { ModLog.Error("[Hook] SyncSettings 异常: " + ex.Message); }
        }

        // 某范围类目当前是否勾选（读镜像字段，热路径零反射）。
        internal static bool ScopeEnabled(Scope.Category c)
        {
            switch (c)
            {
                case Scope.Category.ModName: return ScopeModName;
                case Scope.Category.ModOptions: return ScopeModOptions;
                case Scope.Category.AssetName: return ScopeAssetNames;
                case Scope.Category.AssetDesc: return ScopeAssetDesc;
                case Scope.Category.WorldLabel: return ScopeWorldLabels;
                default: return ScopeGameCore;
            }
        }

        private static MethodInfo _refreshMi;
        private static MethodInfo _optionsRefreshMi;

        // 只刷新 Cohtml 可见文字：调用 LocalizationBindings 的私有 OnActiveDictionaryChanged，
        // 它仅触发 Cohtml 的 l10n.activeDictionaryChanged 事件让界面重新调用 Translate，
        // 【不会】发全局 onActiveDictionaryChanged，故不惊动 I18NEverywhere 等订阅者（兼容要求）。
        // 需求1：单靠上面的事件不会重渲染选项页 React 树（这正是「开关后选项页文字不变」的根因），
        // 故再反射调用 OptionsUISystem.RefreshPage()（私有，只重序列化当前可见选项页，不切屏，非选项页调用也无害）。
        // 必须在主线程调用。
        public static void RefreshVisibleText()
        {
            try
            {
                object bindings = GameManager.instance?.userInterface?.localizationBindings;
                if (bindings != null)
                {
                    if (_refreshMi == null)
                        _refreshMi = bindings.GetType().GetMethod("OnActiveDictionaryChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                    _refreshMi?.Invoke(bindings, null);
                }
            }
            catch { }
            RefreshOptionsPage();
            // v1.0.5 需求1：整条刷新链【管不到下拉框】—— RefreshPage() 只重跑可见性/标题/说明，
            // 候选项数组是建页时的快照，前端又按 id 缓存过取词结果（详见 DropdownL10n 的类注释）。
            // 所以落地的译文必须由 DropdownL10n 自己写回控件；这里挂上同一个刷新时机。
            DropdownL10n.ScanIfOptionsSeen("有新译文落地");
        }

        // 反射重渲染当前可见选项页（需求1）。不直接 typeof(OptionsUISystem)（万一它是 internal 或命名空间不同会导致整个编译失败），
        // 改为按全名运行时解析类型；跨版本可编译，任何一步失败都静默跳过（只是不刷新选项页，不影响翻译主功能）。
        private static Type _optionsType;
        private static bool _optionsTypeSearched;

        // internal：v1.1.7 起「档位」那一行的显隐也要走它（设置类只记一个标记，由 DropdownL10n.Tick 带过来）。
        internal static void RefreshOptionsPage()
        {
            try
            {
                object sys = GetOptionsSystem();
                if (sys == null) return;
                if (_optionsRefreshMi == null)
                    _optionsRefreshMi = sys.GetType().GetMethod("RefreshPage", BindingFlags.Instance | BindingFlags.NonPublic);
                _optionsRefreshMi?.Invoke(sys, null);
            }
            catch { }
        }
        // 向 ECS 世界要 OptionsUISystem 的实例（只能在主线程调）。任何一步拿不到就返回 null，调用方静默跳过。
        // internal 而不是 private：v1.0.5 的下拉框扫描器（DropdownL10n）走同一棵树，不复用就得再维护一份
        // 「游戏版本改了怎么找这个系统」的逻辑 —— 那种平行表正是本文件注释里反复抱怨的东西。
        internal static object GetOptionsSystem()
        {
            try
            {
                Type optType = GetOptionsType();
                if (optType == null) return null;

                // v1.1.3 性能：这两枚 MemberInfo 每轮扫描都要用一次，而"一轮"在选项页开着时是每 1.2 秒
                // （外加每条新译文落地）。Type 的静态属性/实例方法在游戏运行期不会变 ⇒ 反射一次记下来。
                // 拿不到就继续按老路返回 null，行为与旧写法一致（只是少了一遍 GetProperty/GetMethod）。
                if (_worldInjectPi == null)
                    _worldInjectPi = typeof(Unity.Entities.World).GetProperty(
                        "DefaultGameObjectInjectionWorld", BindingFlags.Public | BindingFlags.Static);
                if (_worldInjectPi == null) return null;
                object world = _worldInjectPi.GetValue(null);
                if (world == null) return null;

                if (_worldGetSysMi == null)
                    _worldGetSysMi = typeof(Unity.Entities.World).GetMethod("GetExistingSystemManaged",
                        BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
                return _worldGetSysMi?.Invoke(world, new object[] { optType });
            }
            catch { return null; }
        }

        private static System.Reflection.PropertyInfo _worldInjectPi;
        private static MethodInfo _worldGetSysMi;

        // 按全名查找 Game.UI.Menu.OptionsUISystem：先 assembly-qualified，再遍历已加载程序集兜底。只查一次并缓存。
        private static Type GetOptionsType()
        {
            if (_optionsTypeSearched) return _optionsType;
            _optionsTypeSearched = true;
            _optionsType = Type.GetType("Game.UI.Menu.OptionsUISystem, Game");
            if (_optionsType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { _optionsType = asm.GetType("Game.UI.Menu.OptionsUISystem"); if (_optionsType != null) break; }
                    catch { }
                }
            }
            return _optionsType;
        }

        // ===== 反馈7：把刚拉到的模型列表推进【已经建好的】下拉框控件 =====
        // 根因（Game.dll 元数据实测）：[SettingsUIDropdown] 的选项列表在【建页那一刻】就被 GetModelItems() 取走一次，
        // 存进 DropdownField<string>.items；而 RefreshPage() 只重跑 Page 的 UpdateVisibility / UpdateNameAndDescription /
        // UpdateWarning，从不重取 items。所以「获取模型列表」把结果写进 TranslatorSetting 之后，控件里依然只有建页时
        // 那一两项，玩家看到的就是一个点不开的下拉框。
        // 对策：直接把新的 items 塞进那个控件，再把它记住的版本号抬一格 —— 控件自己的 Update() 每帧比对
        // m_ItemsVersion 与 itemsVersion()，发现变了就把新列表重新序列化给前端。找不到控件只写日志，不影响翻译。
        private static int _modelItemsVersion;     // 我们自己维护的版本号，只在推送新列表时 +1

        // 只能在主线程调用（要碰 ECS 世界与 UI 控件）。
        public static void PushModelItems()
        {
            TranslatorSetting s = TranslatorSetting.Instance;
            if (s == null) return;
            try
            {
                // 每次都重新定位，不缓存控件实例：选项页若被重建，旧实例还活着但已经不在页树上，
                // 往它身上塞 items 玩家永远看不到 —— 而整趟遍历只是走一遍页树，开销可以忽略。
                object widget = FindModelDropdown();
                if (widget == null)
                {
                    ModLog.Warn("[模型列表] 没找到模型下拉框控件：列表已记住，重开一次选项页或重启游戏即可显示（定位诊断见上一行日志）。");
                    return;
                }

                Type wt = widget.GetType();
                // 版本号从控件【当前记住的值】+1 起步，保证 Update() 一定看得见变化：
                // 它原本的 itemsVersion 可能是 null（版本恒为 0），也可能是框架给的另一个函数，直接写 1 有撞车的可能。
                FieldInfo verField = wt.GetField("m_ItemsVersion", BindingFlags.Instance | BindingFlags.NonPublic);
                int stored = 0;
                try { if (verField != null) stored = (int)(verField.GetValue(widget) ?? 0); } catch { }
                _modelItemsVersion = stored + 1;

                var items = s.GetModelItems();
                wt.GetProperty("items")?.SetValue(widget, items, null);
                PropertyInfo vp = wt.GetProperty("itemsVersion");
                if (vp != null && vp.CanWrite) vp.SetValue(widget, (Func<int>)(() => _modelItemsVersion), null);
                ModLog.Info("[模型列表] 已把 " + items.Length + " 项塞进下拉框控件（版本号 " + stored + " → " + _modelItemsVersion + "）。");
            }
            catch (Exception ex) { ModLog.Warn("[模型列表] 推送下拉框失败（列表仍在内存里，重开游戏可见）: " + ex.Message); }
        }

        // 在选项页树里找那个模型下拉框：pages → sections → options → widget。
        // 两层判据缺一不可 —— 选项页里同时挂着游戏本体和其它模组的下拉框：
        //   ① 结构：页 id 或板块 id 以本模组的「翻译引擎」结尾（框架可能加前缀，所以不用 ==）。
        //   ② 内容：排除「目标语言」那个 —— 它的项全是 LanguageCatalog 里的语言代码，模型名一个都不是。
        // 第二条才是关键：控件里的 items 是建页时的快照，玩家中途换过引擎之后，快照里的模型名跟当前
        // DefaultModel 已经对不上了 —— 拿「是否包含当前默认模型」当判据的话，第二次点获取就找不到控件了。
        private static object FindModelDropdown()
        {
            object sys = GetOptionsSystem();
            if (sys == null) { ModLog.Warn("[模型列表] 拿不到 OptionsUISystem，无法定位下拉框控件。"); return null; }

            IDictionary pages = ReadObj(sys, "pages") as IDictionary;
            if (pages == null) { ModLog.Warn("[模型列表] OptionsUISystem.pages 读不到，游戏版本可能改了。"); return null; }

            StringBuilder seen = new StringBuilder();
            object found = null;
            foreach (DictionaryEntry pe in pages)
            {
                object page = pe.Value;
                if (page == null) continue;
                string pageId = ReadStr(page, "id");
                IList sections = ReadObj(page, "sections") as IList;
                if (sections == null) continue;
                foreach (object section in sections)
                {
                    if (section == null) continue;
                    string sectionId = ReadStr(section, "id");
                    if (!IdMatches(pageId, TranslatorSetting.kTabEngine) && !IdMatches(sectionId, TranslatorSetting.kGroupEngine)) continue;
                    IList options = ReadObj(section, "options") as IList;
                    if (options == null) continue;
                    foreach (object option in options)
                    {
                        object widget = option == null ? null : ReadObj(option, "widget");
                        if (!IsStringDropdown(widget)) continue;
                        object items = widget.GetType().GetProperty("items")?.GetValue(widget, null);
                        string tag = pageId + "/" + sectionId;
                        if (IsLocaleDropdown(items)) { seen.Append(" [语言下拉框 ").Append(tag).Append(']'); continue; }
                        seen.Append(" [候选 ").Append(tag).Append(' ').Append(CountOf(items)).Append(" 项]");
                        if (found == null) found = widget;
                    }
                }
            }
            ModLog.Info("[模型列表] 下拉框定位：" + (seen.Length == 0
                ? "「翻译引擎」页里一个 string 下拉框都没看到（页/板块 id 可能带了别的前缀，把这行日志发回来即可对症）。"
                : seen.ToString().Trim() + (found == null ? " → 全被排除了。" : " → 用第一个候选。")));
            return found;
        }

        private static string ReadStr(object o, string prop) => ReadObj(o, prop) as string;

        // 反射读一个属性值。NonPublic 必须带上：OptionsUISystem.pages / sortedPages 经 Game.dll 元数据实测是
        // 【internal】属性（getter 非 public），只按 Public 查就永远返回 null —— 这正是反馈2「模型列表推进不去」
        // 的根因：日志里那句「OptionsUISystem.pages 读不到」不是游戏改版，是我们自己把可见性写窄了。
        // 页/板块/选项那一层的成员（id、sections、options、widget）都是 public，唯独入口这两个是 internal。
        // internal：DropdownL10n（v1.0.5 需求1）遍历同一棵树，共用这一个「带 NonPublic 的属性读法」。
        internal static object ReadObj(object o, string prop)
        {
            if (o == null || string.IsNullOrEmpty(prop)) return null;
            Type t = o.GetType();
            // v1.1.3 性能：属性信息按 (类型, 属性名) 记一次。这个函数在选项页扫描里是【每个候选项容器每轮
            // 都要跑好几次】（实机凭据：101 个下拉框 / 4996 项候选，pages→sections→options→widget 四层
            // 每轮各读一次），旧写法每次都重新 GetProperty —— 与 AccessAnarchy 那条"无变化仍重跑"同形。
            // 类型在运行期不会改属性表，所以记一次就是永远对；找不到也记 null 免得每轮再试。
            Dictionary<string, PropertyInfo> map;
            lock (s_propCache)
                if (!s_propCache.TryGetValue(t, out map))
                    map = s_propCache[t] = new Dictionary<string, PropertyInfo>();

            PropertyInfo pi;
            if (!map.TryGetValue(prop, out pi))
            {
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                try { pi = t.GetProperty(prop, F); } catch { pi = null; }
                lock (s_propCache) map[prop] = pi;
            }
            try { return pi?.GetValue(o, null); }
            catch { return null; }
        }

        // (类型 → (属性名 → PropertyInfo))。外层用普通字典 + 一把锁：读远多于写，写只在第一次见到某个类型时发生。
        private static readonly Dictionary<Type, Dictionary<string, PropertyInfo>> s_propCache =
            new Dictionary<Type, Dictionary<string, PropertyInfo>>();

        // 框架可能给页/板块 id 加前缀（例如模组名），所以按后缀比对，大小写不敏感。
        private static bool IdMatches(string id, string want)
            => !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(want)
               && id.EndsWith(want, StringComparison.OrdinalIgnoreCase);

        // 按类型【名字】判定，不直接 typeof(DropdownField<>)：那个类型不在本模组的引用里，
        // 万一游戏改版把它挪走或改成 internal，这里最多是找不到控件，而不是整个模组编译不过。
        private static bool IsStringDropdown(object widget)
        {
            if (widget == null) return false;
            Type t = widget.GetType();
            if (!t.IsGenericType || t.Name != "DropdownField`1") return false;
            return t.GetGenericArguments()[0] == typeof(string) && t.GetProperty("items") != null;
        }

        private static int CountOf(object items)
        {
            IEnumerable e = items as IEnumerable;
            if (e == null) return 0;
            int n = 0;
            foreach (object unused in e) n++;
            return n;
        }

        // 「目标语言」下拉框的项全是 LanguageCatalog 里的语言代码，模型名一个都不会是。
        // 半数以上命中就认定是语言下拉框（留一半余量：玩家当前语言若不在目录里，框架会额外补一项）。
        private static bool IsLocaleDropdown(object items)
        {
            IEnumerable e = items as IEnumerable;
            if (e == null) return false;
            int n = 0, hits = 0;
            foreach (object it in e)
            {
                if (it == null) continue;
                n++;
                string v = null;
                try { v = it.GetType().GetField("value")?.GetValue(it) as string; } catch { }
                if (string.IsNullOrEmpty(v)) continue;
                foreach (var lang in LanguageCatalog.All) if (lang.code == v) { hits++; break; }
            }
            return n > 0 && hits * 2 >= n;
        }

        private static MethodInfo GetTranslateMethod()
        {
            if (_translateMethod == null)
                _translateMethod = typeof(UILocalizationManager).GetMethod(
                    "Translate", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(string), typeof(TranslationData) }, null);
            return _translateMethod;
        }

        // 实验性「游戏画面图层」钩子目标：Game.UI.NameSystem.GetRenderedLabelName(Entity)（需求2）。
        private static MethodInfo _nameSystemMethod;
        private static MethodInfo GetNameSystemMethod()
        {
            if (_nameSystemMethod == null)
            {
                Type ns = typeof(Game.UI.NameSystem);
                _nameSystemMethod = ns.GetMethod("GetRenderedLabelName", BindingFlags.Instance | BindingFlags.Public, null,
                    new[] { typeof(Unity.Entities.Entity) }, null);
            }
            return _nameSystemMethod;
        }

        // 我们的前缀是否仍挂在 Translate 上（全局 UnpatchAll 会把它拆掉，据此判断是否需重打）。
        private static bool IsApplied(MethodInfo orig)
        {
            try
            {
                var info = Harmony.GetPatchInfo(orig);
                return info != null && info.Owners.Contains(HarmonyId);
            }
            catch { return false; }
        }

        public static void Apply()
        {
            if (Mod.IsTearingDown()) return;   // v0.28：拆除阶段绝不打补丁（第二道防线，所有调用路径都拦）。
            try
            {
                MethodInfo orig = GetTranslateMethod();
                if (orig == null) { ModLog.Error("[Hook] 找不到 UILocalizationManager.Translate，无法 Hook。"); return; }
                // v0.30：只在这里探一次并记日志（Apply 会被 EnsureApplied 反复调用，别刷屏）。
                // 为什么要记：ModManager 按简单名绑定、不认版本号 ⇒ 一个会话只有一份 Harmony 真正生效，
                // 谁先被加载谁替所有人定下 API。本模组编译引用 2.2.2.0、部署时把 0Harmony.dll 换成 2.3.3（见 csproj），
                // 所以「这局生效的是哪一份、来自哪个目录」只能从运行时问出来。跨模组 Harmony 冲突时这一行就是定责证据。
                if (_harmony == null)
                {
                    _harmony = new Harmony(HarmonyId);
                    try
                    {
                        var har = typeof(Harmony).Assembly.GetName();
                        var loc = typeof(Harmony).Assembly.Location;
                        ModLog.Info("[Hook] Harmony 运行时生效版本=" + har.Version + " 来源=" +
                                    (string.IsNullOrEmpty(loc) ? "(内存加载，无文件路径)" : loc) +
                                    "；本模组编译时引用 0Harmony 2.2.2.0（按简单名绑定，版本忽略）。");
                    }
                    catch (Exception ex) { ModLog.Info("[Hook] Harmony 版本探测失败（不影响打补丁）：" + ex.Message); }
                }
                if (!IsApplied(orig))
                {
                    MethodInfo prefix = typeof(Patches).GetMethod("Prefix", BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(orig, new HarmonyMethod(prefix));
                    ModLog.Info("[Hook] 已 patch UILocalizationManager.Translate（按需懒翻译已激活）。");
                }

                // 实验性：路名/地形贴字走 NameSystem.GetRenderedLabelName，绕过 Translate，需单独 Postfix。
                MethodInfo ns = GetNameSystemMethod();
                if (ns != null && !IsApplied(ns))
                {
                    MethodInfo postfix = typeof(Patches).GetMethod("WorldLabelPostfix", BindingFlags.Static | BindingFlags.Public);
                    _harmony.Patch(ns, null, new HarmonyMethod(postfix));
                    ModLog.Info("[Hook] 已 patch NameSystem.GetRenderedLabelName（游戏画面图层，实验性）。");
                }
            }
            catch (Exception ex) { ModLog.Error("[Hook] patch 失败: " + ex); }
        }

        // v1.1.3 卸载安全：模组被正式拆除（OnDispose）时【主动拆自己的补丁】。
        // 为什么必须有这一条：本模组从头到尾只打过两个补丁（UILocalizationManager.Translate、
        // NameSystem.GetRenderedLabelName），都是【游戏自己的方法】。游戏在模组卸载时会不会顺手把
        // 那个程序集连同跳板一起清掉，我们没有凭据（net48 的程序集实测不可卸载，所以跳板会一直留在
        // 游戏方法上指向我们的代码）—— 玩家"取消订阅 / 直接删文件夹"之后那局不加载我们了才干净，
        // 但【同一局里禁用再启用】这类路径如果游戏只是丢掉程序集引用，留着跳板就是无谓的风险。
        // 拆自己名下的补丁是 Harmony 官方契约里的礼貌动作，代价为零；拆不动也不许把拆除流程带崩。
        public static void UnpatchOwn(int patchCount)
        {
            try
            {
                if (_harmony == null) return;
                _harmony.UnpatchAll(HarmonyId);      // 只拆 owner == 我们 id 的补丁，绝不动别的模组
                ModLog.Info("[Hook] 卸载时已拆掉本模组的 Harmony 补丁（" + patchCount + " 处）。");
                _harmony = null;
            }
            catch (Exception ex) { ModLog.Warn("[Hook] 卸载时拆补丁失败（不影响退出）: " + ex.Message); }
        }

        // 由 MainThreadDispatcher 每帧调用（非 Harmony，故不受全局 UnpatchAll 影响）：节流检查，被拆了就重打。
        // 关键：返回 false 才会「持续注册」；返回 true 会被 UpdateUpdaters 注销（实测语义）。
        public static bool EnsureApplied()
        {
            try
            {
                // v0.28 退出闸门：日志实证过「游戏已经全局 UnpatchAll、正在销毁资源时，本守护又把补丁打回去并强刷整屏 UI」——
                // 这是退出卡死无响应的头号嫌疑。拆除阶段一律什么都不做（返回 false 仅表示「继续注册」，不消耗主线程）。
                if (Mod.IsTearingDown()) return false;

                float now = UnityEngine.Time.realtimeSinceStartup;
                if (now - _lastCheck >= 2f)
                {
                    _lastCheck = now;
                    // 顺带刷新游戏当前语言（可能不经保存就切换）：供同语言本地跳过判定，避免英文游戏+英文目标时白联网。
                    try { string al = GameManager.instance?.localizationManager?.activeLocaleId; if (!string.IsNullOrEmpty(al)) ActiveLocale = al; } catch { }
                    bool reApplied = false;
                    MethodInfo orig = GetTranslateMethod();
                    if (orig != null && !IsApplied(orig)) { Apply(); reApplied = true; }
                    MethodInfo ns = GetNameSystemMethod();
                    if (ns != null && !IsApplied(ns)) { Apply(); reApplied = true; }
                    if (reApplied)
                    {
                        ModLog.Info("[Hook] 检测到补丁被移除（多半是别的全局 UnpatchAll），已重新打上。");
                        // 重打后已渲染的原文需要重新走 Translate 才会变回译文（需求9c）。
                        Mod.MarkUiDirtyAndRefresh();
                    }
                }

                // 需求3：图层刷新（路名/区域名/贴地文字）。挂在同一个每帧主线程入口上，
                // 直接复用上面的退出闸门与 try/catch；档位关掉时它前几行就返回，不产生开销。
                WorldLabels.Tick();

                // v1.0.5 需求1：下拉框候选项。同样挂在这个每帧主线程入口上（自己带 1.2 秒节流与
                // 「选项界面正在渲染」的标记判定），玩家没打开选项页时每帧只多一次 bool 判断。
                DropdownL10n.Tick();

                // 需求6：快捷键轮询。同样挂在这个每帧主线程入口上（框架不会替你回调绑定事件），
                // 复用上面的退出闸门与 try/catch；玩家没绑键时每帧只多两次 null 判断。
                Mod.PollHotkeys();

                // v0.28：不再在主线程做周期性存盘。落盘改由后台翻译循环持续【增量追加】完成，
                // 退出时再由 Mod.BeginShutdown 兜一次——主线程彻底不碰缓存磁盘 I/O（旧版每 30s 全量重写一次，会随机拖慢主线程）。
            }
            catch { }
            return false;
        }

        // 热路径：必须极简 + 异常安全，任何意外一律放行原文（return true），绝不拖垮 UI。
        public static bool Prefix(string key, TranslationData data)
        {
            try
            {
                if (!HookEnabled) return true;        // 总开关关闭：放行原文（需求8）。
                if (Paused) return true;              // 需求2：刚清除缓存 → 暂停期保持原文，不重新入队翻译。
                if (key == null || data == null) return true;
                // v1.0.5 需求1 + v1.2.3 需求2：看见界面来取词，顺手记两笔账（两笔都在 NoteKeyRequested 里）——
                //   ① 这个词是 Options.OPTION_DESCRIPTION[…] ⇒ 记下"前端为这一行说明取过词"。
                //     前端只在有一行被移到眼前时才渲染那块信息栏、也只渲染那一行的说明（实机 index.js 与
                //     日志 5 条 OPTION_DESCRIPTION 首入队实证），所以这一笔就是托管层拿得到的 hover 凭据；
                //   ② 这个词以 Options. 开头 ⇒ 选项界面正在渲染，给下拉框扫描器置 Want。
                // ⚠ v1.2.3 起这一句【不再包在 Want==false 里】（v1.2.2 及以前是包的）：①要的是"每条说明 id
                // 都被记到"，而 Want 一旦置位就长期为真，沿用旧的外层判断会把九成的记录机会漏掉。
                // 成本：一次前缀比较；命中说明族才多一次加锁 + 一次集合查找，而那点频率等于玩家移动鼠标。
                DropdownL10n.NoteKeyRequested(key);

                // 不翻译本模组自己的选项文字 —— 但只在目标语言是那 12 种官方界面语言之一时（那时 L10n/SelfL10n
                // 已经按游戏语言出好了手写文案，再机翻只会把 "DeepL" 翻成"深度链接"这类品牌名）。
                // v1.0.5 需求2：目标语言在这 12 种之外时【不再拦】—— 全屏都成了小语种、只有本模组的设置页还是
                // 英语，玩家点名要这一格也翻。判据与下拉框那条链共用 DropdownKit.SkipSelfKey（两处必须同一条）。
                if (DropdownKit.SkipSelfKey(key, TargetLocale)) return true;

                // 范围过滤（需求2）：未勾选的类目直接放行原文，不翻不标记。
                // v1.1.3 性能：这里改用 Scope.ClassifyMemo —— 同一条判据，只是把 key→类目 记进小缓存。
                // 这一次钩子是【每一次界面取词】都跑（进一趟选项页就是成百上千次），而 Classify 里的
                // IsModIdentifier 要遍历几十条模组 id、ExtractIdentifier 每条都要 Substring 新建一个串。
                // 顺序【没有】动（先类目、后字典、再缓存命中），所以"关掉某档范围就不再显示旧译文"这条
                // 行为一字不变；缓存会在模组 id 表换了一份时整体作废，不会把新模组永久钉成游戏本体。
                Scope.Category cat = Scope.ClassifyMemo(key);
                if (!ScopeEnabled(cat)) return true;

                LocalizationDictionary dict = GameManager.instance?.localizationManager?.activeDictionary;
                if (dict == null) return true;

                // 先走一次原始字典解析：这一步会触发 I18NEverywhere 的 TryGetValue 补丁，
                // 所以 baseVal 已经是「人工翻译后」的值——我们只在它仍是外语时才机器翻译（人工优先）。
                // 注意（需求11）：玩家自由输入/重命名的文字没有 key、不进字典，TryGetValue 拿不到 → 永不被翻译。
                if (!dict.TryGetValue(key, out string baseVal) || string.IsNullOrEmpty(baseVal)) return true;
                if (baseVal.Length < 2) return true; // 单字符/空串不翻

                if (TransMap.TryGetTranslated(baseVal, out string tr) && !string.IsNullOrEmpty(tr) && tr != baseVal)
                { data.Set(tr); return false; }       // 命中机器译文，替换并跳过原方法。

                if (TransMap.IsResolved(baseVal)) return true; // 已知无需翻译，放行原文。

                // 本地预筛（需求1+4）：纯数字/符号、快捷键、已是目标语言 → 跳过，不联网、不显示「…」。
                if (Scope.ShouldSkipLocal(baseVal, TargetLocale, ActiveLocale)) { TransMap.ResolveAsSkip(baseVal); return true; }

                TransMap.NoteScope(baseVal, cat);              // v1.2.0 需求5：范围跟着文字入队，Loop 取出来决定提示词语境
                // v1.2.5 修复：选项界面的文字先【暂存】不直接入队 —— 让扫描器把同一个板块的文字收齐、
                // 合成一个请求（玩家要的"板块标题＋5 个设置项一次送"）。
                // 为什么不在这里猜板块：此刻手上只有 key，没有"这一项属于哪个板块"的可靠来源；
                // 扫描器那边正在走同一棵树、手里就有 section/options 与板块标题，让它来收才对得上。
                // 暂存不会丢：扫描器没来认领（选项面板没开、那一页没扫到）时，Loop 会按 StageTtlMs 到期回落成单条。
                if (key != null && key.StartsWith("Options.", StringComparison.Ordinal)) TransMap.Stage(baseVal, key);
                else TransMap.Enqueue(baseVal);      // 首次见到：入队后台翻译（入队的是干净原文，绝不含标记）。
                if (!_firstEnqueueLogged) { _firstEnqueueLogged = true; ModLog.Info($"[Hook] 前缀已生效，首次入队待翻译文本：'{Probe.Trunc(baseVal, 40)}'（key={Probe.Trunc(key, 40)}）。"); }
                // 静态待翻译标记：原文末尾加一个「…」表示「正在翻译」，翻好后连同标记一起原地变成译文。
                // v1.1.7 性能：串走 TransMap.WithPendingMarker 的小表，同一条未翻文字不再每次取词都重拼一遍。
                if (MarkerEnabled) { data.Set(TransMap.WithPendingMarker(baseVal, PendingMarker)); return false; }
                return true;
            }
            catch { return true; }
        }

        // 实验性「游戏画面图层」Postfix（需求2 + 需求11）：路名/区域名/贴地文字走 NameSystem，绕过 Translate。
        // 硬约束：玩家自定义名（CustomName）绝不翻译——这是需求11 在本钩子上唯一可能触碰自定义内容的地方。
        // 受游戏 m_CachedLabels 缓存影响，可能要等标签重渲染/重进游戏才刷新，故标注「实验性、不保证即时」。
        public static void WorldLabelPostfix(Game.UI.NameSystem __instance, Unity.Entities.Entity entity, ref string __result)
        {
            try
            {
                if (!HookEnabled || Paused || !ScopeWorldLabels) return;   // 总开关关 / 暂停 / 该范围未开 → 不动
                if (__instance == null || string.IsNullOrEmpty(__result)) return;
                // 需求11：玩家自定义名直接放行，永不翻译。
                if (__instance.TryGetCustomName(entity, out _)) return;

                string src = __result;
                if (src.Length < 2) return;

                if (TransMap.TryGetTranslated(src, out string tr) && !string.IsNullOrEmpty(tr) && tr != src)
                { __result = tr; return; }                              // 已翻好：直接替换
                if (TransMap.IsResolved(src)) return;                   // 已知无需翻译：放行
                if (Scope.ShouldSkipLocal(src, TargetLocale, ActiveLocale)) { TransMap.ResolveAsSkip(src); return; }

                // 需求3：登记成「图层文字」，这样译文落地时才会计入 WorldLabels 的刷新信号（菜单译文不会误触发）。
                TransMap.MarkWorldSource(src);
                TransMap.NoteScope(src, Scope.Category.WorldLabel);   // v1.2.0 需求5：路名/区名 → 提示词走"世界标签"语境
                TransMap.Enqueue(src);                                  // 入队后台翻译；先返回原文，翻好后由 WorldLabels 清缓存让游戏重新取一遍
            }
            catch { /* 实验性钩子：任何异常都放行原文，绝不影响世界渲染 */ }
        }
    }

    // ===== v1.0.5 需求1：下拉框候选项文字 =====
    // 玩家报的现象：选项页的标题/说明都翻好了，唯独下拉框里（含收着时显示的那一项）还是外语。
    // 根因与"延时动作"有关但比它更靠后（下面三条都有 decompiled 源码行号为证，判据本身在 DropdownKit.cs）：
    //   ① 候选项数组是【建页那一刻】从 itemsAccessor / enumMembers 取走的快照，之后 Update() 只在
    //      itemsVersion() 变了才重取，而绝大多数选项行没有 [SettingsUIValueVersion] ⇒ 版本永远是 0
    //      ⇒ 这一次快照就是最后一次（DropdownField.cs:63-86、EnumField.cs:44-66、AutomaticSettings.cs:510-518）。
    //   ② OptionsUISystem.RefreshPage() 只重跑 UpdateVisibility / UpdateNameAndDescription / UpdateWarning，
    //      从不重取 items ⇒ 本模组原来那条"有新译文就 RefreshPage"的通路对下拉框内容无效。
    //   ③ 前端 JS 只从 C# 拿到 {id, value, args} 三元组（LocalizedString.Write 原样序列化），
    //      取词是 cohtml 的 SWIG director 打进 UILocalizationManager.Translate 的；它按 id 缓存过一次，
    //      晚一秒落地的译文它看不见。
    // 对策（不依赖任何 JS 侧未证实的行为）：模组自己在 C# 侧把这一项的 displayName 换成
    // LocalizedString.Value(成品译文) —— id 为空 ⇒ 前端不再查字典、原样上屏 —— 再调公开的
    // Widget.SetPropertiesChanged() 逼这个控件下一帧重发 props（Widget.cs:76、161-187）。
    // 时机：Prefix 每看见一次 Options.* 取词就知道选项界面正在渲染，置 Want 标记；本类据此
    // ①在界面打开时把所有下拉框的候选项一次性入队（不等玩家点开），②之后每轮把落地的译文写回控件。
    // 只在主线程跑（要遍历 ECS 系统与控件），所有异常一律吞掉：最坏结果是"下拉框没翻"，不影响其它翻译。
    internal static class DropdownL10n
    {
        // 一个候选项对象 → 我们替它记的账。键是控件里的 item 实例本身，所以游戏随时换掉整批候选项
        // （重建页、换引擎重取列表）时，新对象自然查不到记录、按第一次见到处理 —— 不需要维护控件清单，
        // 也不会因为我们持有引用而拖住游戏对象不放（弱表）。
        sealed class Origin
        {
            public LocalizedString Original;   // 建页时那一份原样 LocalizedString，关翻译时逐字段还原
            public string Source;              // 待翻源文本（null = 暂时/永久不归我们管，看 Retry）
            public bool Retry;                 // Source 为 null 只是因为暂时性失败（字典没就绪/查不到）⇒ 下一轮再看一次
            public string Applied;             // 我们上一次写进控件的译文（null = 没写过）
            public string AppliedLocale;       // 那句译文是翻成哪种语言的
        }

        // 控件类型 → 反射元数据。下拉框控件的类型总数是个位数（DropdownField<string>/<int>、EnumField、
        // FlagsField + 其它模组的自定义控件），所以这张表不会长大，也不需要清理。
        sealed class Meta
        {
            public System.Reflection.PropertyInfo List;   // items / enumMembers
            public System.Reflection.PropertyInfo DisplayName;   // 元素类型上的 displayName
            public System.Reflection.MethodInfo SetChanged;      // Widget.SetPropertiesChanged()
            public string Name;                             // 只用于日志
        }

        static readonly Dictionary<Type, Meta> s_meta = new Dictionary<Type, Meta>();
        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, Origin> s_origins =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, Origin>();

        // 控件 → 「这一栏整体是什么用途」（v1.1.2）。判一次记住一次：判据要把整栏文字读一遍（十几次反射），
        // 每 1.2 秒对 101 个控件各来一遍就是白烧主线程。弱表 ⇒ 游戏重建控件时新对象自然重新判，不漏不误。
        // （包一层 class 是因为 ConditionalWeakTable 的 TValue 必须是引用类型，enum 直接放不进去。）
        sealed class ColumnVerdict
        {
            public DropdownKit.ColumnKind Kind;
            public ColumnVerdict(DropdownKit.ColumnKind kind) { Kind = kind; }
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, ColumnVerdict> s_columns =
            new System.Runtime.CompilerServices.ConditionalWeakTable<object, ColumnVerdict>();

        // ===== 选项行的标题与说明（v1.1.3，玩家点名 Real Weather Sync / Plop the Growables 整页英文）=====
        // 一个 NamedWidget 有【两处】文字：displayName（这一行的名字）与 description（下面那行小字说明）。
        // 每一处都要单独记住"模组原来是什么"，因为我们可能已经把控件里那句话换成中文了 ——
        // 没有这份快照就分不清"是模组自己改的"还是"是我们写的"，也就没法安全还原。
        // 弱表 ⇒ 游戏重建选项页时新对象自然重判、旧对象随 GC 走（与候选项那两张表同一个套路）。
        sealed class RowSlot
        {
            public bool Seen;                        // 见过才计数，免得每 1.2 秒把统计刷大
            public bool Tried;                       // 解析过一次源文本没有（永久拿不到的那种不再每轮白查字典）
            public bool HasAction;                   // 模组是用 action 动态给值的吗
            public Func<LocalizedString> Action;     // 【模组自己的】action（控件上那份可能已经被我们换成常量）
            public LocalizedString Original;         // 模组原来那句话（id 与英文兜底 value 都在里面）
            public string Source;                    // 解析出的待翻源文本；null = 不归我们管
            public bool Retry;                       // 只是暂时拿不到（字典没就绪/查不到）⇒ 下一轮再解析
            public string Applied;                   // 我们写进去的译文（null = 没写过）
            public string AppliedLocale;             // 那句译文是翻成哪种语言的
        }

        sealed class RowState
        {
            public RowSlot Name;
            public RowSlot Desc;
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NamedWidget, RowState> s_rows =
            new System.Runtime.CompilerServices.ConditionalWeakTable<NamedWidget, RowState>();

        internal static volatile bool Want;             // Prefix 置位：选项界面正在渲染，该扫一轮了
        static volatile bool s_EverOptions;             // 这一局里玩家是否曾经打开过选项页（没打开过就没必要走这棵树）
        static int _lastTick;
        static int _lastLogTick;
        static string _lastActivePage;            // 上一轮扫描时游戏记着的那一屏（页+标签页+在屏行数，v1.2.1 编码见 PageKeyOf）
        // 这一轮是不是【整棵树还原扫描】（v1.1.5，只由 RevertAll / RevertOutOfScope 在玩家点击或保存那一下置位）。
        // 扫描只在主线程跑（Tick / Patches.EnsureApplied / 按钮回调），所以一枚普通 bool 就够，
        // 不需要 Interlocked —— 但它必须在 Scan 退出时清掉，所以下面用 try/finally 成对写。
        static bool s_Sweep;
        // v1.2.1 需求3：这一轮的「在屏控件清单」（游戏算好的 m_WidgetBindings.children 收成集合）。
        // 与 s_Sweep 同一套生命周期：Scan 进门赋值、finally 还原外层值（嵌套调用不能互相踩）。
        // known=false 时集合可能是 null ⇒ IsOnScreen 一律放行，绝不出现在屏清单把整页拦死的情况。
        static HashSet<object> s_OnScreen;
        static bool s_OnScreenKnown;
        static bool s_FirstScanLogged;                  // 第一次看到控件：无条件打一行统计（实机验证的证据）
        static bool s_WalkWarned;                       // 控件遍历出异常只说一次，够定位就行，不刷日志

        const int ScanIntervalMs = 1200;   // 与 RefreshUi 的 1.5s 节流同量级：一轮扫描的实际收益就是几行文字
        const int LogIntervalMs = 8000;    // 统计行最多 8 秒一条，免得挂着省略号时每轮刷一行

        // ===== 「前端为哪条说明取过词」的小表（v1.2.3 需求2：说明只在被移到眼前之后才送翻）=====
        // 为什么住在 Mod.cs 而不是 DropdownKit.cs：那一份要进零游戏 DLL 的 T3 离线壳，不能有锁也不能有日志。
        // 判据本体（HoldDescription / IsDescriptionKey）在 DropdownKit，这一格只是它的【凭据来源】。
        // 为什么平时【只增不清】：它记的是"玩家在这一局里看过这一行说明"这件既成事实。
        // 平时不排空的后果是单向的 —— 说明被重新按住、等下一次 hover，玩家看不出区别；而"什么时候清"本身
        // 若按换页/换语言/暂停各算一处，又是一处新判据（§10 第 9 条要求判据只允许一处），所以平时不清。
        // ⚠ v1.2.4 玩家实机推翻了这个"只增"：点【清除缓存】之后，先前 hover 过的行照样自动翻，玩家原话
        //   "难道这个还有记忆功能？清除缓存后所有的翻译痕迹和缓存都要删除的"。这一格确实是翻译痕迹的一种
        //   （它决定"要不要送翻"），所以现在只有【清除缓存】这一处会排空它 —— 判据仍只有一处（ClearCacheNow），
        //   不是每轮都问、也不是按页/按语言各清一次，§10 第 9 条不破。
        // 只收说明族的 key：一行一条、一页几十行，量级封顶几千条（到顶见 DescRequestedCap）。
        static readonly HashSet<string> s_DescRequested = new HashSet<string>(StringComparer.Ordinal);
        static readonly object s_DescGate = new object();
        const int DescRequestedCap = 4096;
        static bool s_DescCapLogged;

        // Prefix 热路径调用：无反射无 IO（§10 第 9 条）。
        public static void NoteKeyRequested(string key)
        {
            if (DropdownKit.IsDescriptionKey(key)) NoteDescriptionRequested(key);
            if (Want) return;
            if (key != null && key.StartsWith("Options.", StringComparison.Ordinal)) { Want = true; s_EverOptions = true; }
        }

        // ===== v1.2.4 需求3 的一处【已撤回】设计，留档免得下一轮又想起来 =====
        // 曾经想在说明这一半加"板块级凭据"（碰过一行 ⇒ 整块说明放行），凭据表都写好了。玩家当场否掉：
        //   「我说的是设置项标题和设置项……这不包括设置项的说明，说明依然还是鼠标移上去显示后才翻译」。
        // 所以需求3 的"整板块送翻"只针对【标题 + 候选项】—— 它们本来就不看闸、按扫描顺序入队，
        // 由 TryBatch 的有界攒批（一个板块的标题会在同一轮里接连入队 ⇒ 自然进同一批）
        // 与 ReturnToHead 的保序共同满足。说明那一半的粒度【永远是一行】，这条已被退回三次。
        //
        // 记下"前端为这个说明 id 取过词"（= 玩家把它移到过眼前）。加锁只发生在说明族，
        // 频率等于玩家移动鼠标的次数，不在每一帧的取词热路径上。
        static void NoteDescriptionRequested(string key)
        {
            lock (s_DescGate)
            {
                if (s_DescRequested.Contains(key)) return;
                if (s_DescRequested.Count >= DescRequestedCap)
                {
                    if (!s_DescCapLogged)
                    {
                        s_DescCapLogged = true;
                        ModLog.Info($"[下拉框] 说明悬停凭据表到 {DescRequestedCap} 条上限，此后新出现的说明 id 不再记录（后果只是那些行的说明要多等一次悬停才送翻，不会翻错也不会崩）。");
                    }
                    return;
                }
                s_DescRequested.Add(key);
            }
        }

        // 扫描器问这一行说明的凭据在不在（唯一读取点，调用方是 HandleRowText）。
        // ⚠ v1.2.4 需求3 一度想把这一格放宽成"碰过一个板块 ⇒ 整块说明放行"，被玩家当场否掉：
        //   他的原话是「我说的是设置项标题和设置项，这些每个板块下面都可能有很多条设置项啊，
        //   同一个板块可以一起送去翻译……这不包括设置项的说明，说明依然还是鼠标移上去显示后才翻译」。
        //   所以说明这一半的粒度【就是一行】，一个字都不许放宽 —— 这条已经被退回三次了，没有第四次余地。
        //   "整板块一起送翻"那条需求落在【标题/候选项】那一路（它们本来就不看闸，按扫描顺序入队），
        //   由攒批（TryBatch 有界窗口）与保序（ReturnToHead）去满足，与这一格无关。
        public static bool DescriptionRequested(string id)
        {
            if (id == null) return false;
            lock (s_DescGate) return s_DescRequested.Contains(id);
        }

        // v1.2.4 需求1：「清除缓存」把这一格也清掉（玩家口径：清除缓存后所有的翻译痕迹都要没）。
        // 只由 ClearCacheNow 调用 —— 清掉之后每条说明都要重新 hover 一次才送翻，
        // 这正是玩家要的形状：点了清缓存、回到全是原文的状态，不再有"刚才看过所以现在自动翻"的记忆。
        // 与 RevertAll 同一趟清：两者都是"把这一局留下的痕迹归零"，不是两条独立判据。
        public static void ForgetHoveredDescriptions()
        {
            int n;
            lock (s_DescGate)
            {
                n = s_DescRequested.Count;
                s_DescRequested.Clear();
                s_DescCapLogged = false;   // 到顶那条日志跟着复位：清完之后是新的一局，到顶了要能再报一次
            }
            if (n > 0) ModLog.Info($"[下拉框] 清除缓存：一并抹掉 {n} 条说明悬停凭据（此前 hover 过的说明不再自动送翻，需重新移到眼前）。");
        }

        // 每帧主线程入口（Patches.EnsureApplied 里调，与 WorldLabels.Tick / PollHotkeys 同一挂点）。
        // v1.1.4：触发条件从"游戏在取 Options.* 的词"这一条变成两条 ——
        //   ① 照旧：Want（Prefix 看见 Options.* 来取词 ⇒ 选项界面正在渲染）；
        //   ② 新增：活动页【换过】。为什么必须有②：扫描面收窄到一页之后，"玩家切到另一个页"就成了
        //      唯一的重新触发点，而有些模组的页整页标题/说明都是裸文字兜底（一个 Options.* key 都不取，
        //      见 §10 第 79 条），那时①永远不会置位 ⇒ 切过去的页永远不翻。
        //      这一探测每 1.2 秒最多一次，且只读两枚缓存过的字段（不碰控件树），
        //      还在 s_EverOptions 为假时直接返回 ⇒ 从没开过选项页的局里代价为零（§10 第 81 条：
        //      周期热路径上只做与"有没有变化"有关的事）。
        public static void Tick()
        {
            // v1.1.7：「思考强度」从关拨到开时，下面的「档位」行要当场现身。显隐只有 RefreshPage 重算，
            // 而拨下拉框本身不触发它 —— 这里把设置类记下的标记带走（不碰译文表、不发请求、不清图层）。
            if (TranslatorSetting.ConsumeShapeChanged())
            {
                try { Patches.RefreshOptionsPage(); } catch { }
            }
            int now = Environment.TickCount;
            if (now - _lastTick < ScanIntervalMs) return;
            _lastTick = now;
            if (!Want)
            {
                if (!s_EverOptions) return;
                if (ActivePageNow() == _lastActivePage) return;      // 页没换 ⇒ 确实没活，什么都不做
            }
            DropdownKit.Tally t = Scan("选项界面渲染中");
            // 扫不动（还没进过世界 / 游戏改版拿不到系统）或这一轮没有任何欠账就别空转，等下一次取词请求再试。
            if (t == null || t.Idle) Want = false;
        }

        // 有新译文落地 / 改了设置 / 重打补丁之后由 Patches.RefreshVisibleText 顺带调一次。
        // 这是"译文写进控件"的主路径：后台线程翻好一条 → 1.5s 内刷一次界面 → 下拉框跟着一起换。
        public static void ScanIfOptionsSeen(string reason)
        {
            if (!s_EverOptions) return;      // 这局还没开过选项页：一棵没有控件的树不值得走
            Scan(reason);
        }

        // ===== v1.1.5：把我们写进控件的译文【全部】放回原文（不受活动页限制）=====
        // 为什么必须有这一条（玩家 2026-10-02 报的「清除了缓存后模组界面的文本仍然是日文，
        // 并在很短时间内变回了中文，这是不是说明缓存没清干净？」）：
        //   ① 缓存确实清干净了 —— 实机凭据是 20:22:44 那两行「已清空内存译文表并删除磁盘缓存文件」
        //      与紧跟的「预载 0 条历史译文进内存表」，内存表、待追加队列、磁盘文件三处都空；
        //   ② 玩家眼睛看见的日文不在任何缓存里，而是【我们写进控件对象】的那句
        //      （DropdownField 的 item.displayName / NamedWidget 的 displayName/description）。
        //      游戏把这些对象一直养在选项页树里，只有我们的扫描再去把它们改回来；
        //   ③ 而"改回来"这件事 v1.1.4 起只走【当前打开的那一页】—— 收窄送翻面的副作用正好咬在还原这一侧。
        //      实机凭据（同一局）：20:22:36 那一条是 AccessAnarchy 页「上屏=주차장…」（韩语），
        //      20:22:44 玩家清代之后日志里【一行 [下拉框] 都没有】，第一条要到 20:22:47 才出现，
        //      而那一条的活动页已经换成 ToolModeMemory（「退回原文 60」），AccessAnarchy 那一页
        //      从此落在「整页跳过 23 页」里 —— 玩家再点回去之前，那几处韩语会一直挂着。
        //      他报的"仍然是日文，很短时间后变回中文"就是这个：眼前这一页 1.2~3 秒后被补还原了，
        //      其它页根本没被还原。
        // 所以还原必须走整棵树，而且只在玩家明确按下那一下走（不是周期任务 —— §10 第 81 条那条规矩
        // 针对的是每帧/每 1.2 秒那种热路径；一次点击换一轮全树，代价与 v1.0.5 每 1.2 秒一轮比可以忽略）。
        // 走整棵树时【一条新活都不排】、也【不是我们写的地方一眼都不多看】（见 s_Sweep 那两处早退），
        // 所以这一轮的开销只与"我们写过多少处"有关，与树有多大无关。
        // 下面两个档位（玩家 2026-10-02 追问「把所有页上这一档的译文都切回去，性能影响大不大」之后加的第二个）：
        //   RevertAll        —— 玩家说"这些译文我全不要"：清代 / 关总开关 / 重新翻译。把 enabled 当假，
        //                       Decide 于是只可能给 Restore 或 Skip。
        //   RevertOutOfScope —— 玩家只关掉某一档「翻译范围」：enabled 照实读，只有【已出范围】的那几处退回原文，
        //                       玩家还想留着的那一档不无辜回退（否则下次重翻就是白烧额度）。
        // 两者共用同一轮扫描、同一条"不排新活"闸门（DropdownKit.MayEnqueue），差别只在 forceOff 那一格。
        static int _lastSweepTick;
        const int SweepDebounceMs = 1500;   // 与 ScanIntervalMs 同量级：一次点击串起 SaveNow→ClearCacheNow 两个入口，别扫两遍

        private static void Sweep(string reason, bool forceOff)
        {
            if (!s_EverOptions) return;                          // 这局没打开过选项页 ⇒ 一处都不是我们写的
            int now = Environment.TickCount;
            if (now - _lastSweepTick < SweepDebounceMs) return;   // 同一次点击触发的第二次还原没有意义（第一次已经走完）
            _lastSweepTick = now;
            Scan(reason, true, forceOff);
        }

        // ① "整体不要"：清除缓存 / 关总开关 / 重新翻译 —— 我们写过的每一处都放回原文，一条新活都不排。
        public static void RevertAll(string reason) { Sweep(reason, true); }

        // ② "只退被我关掉的那一档"：勾掉某一档「翻译范围」之后（范围开关一改就走 SaveNow，见 TranslatorSetting.OnLiveSettingChanged）。
        // 这一种【不能】把 enabled 当假：那会把玩家还想留着的那些译文一起退回原文、下一次还得重翻（白烧额度）。
        // 所以走真实开关值，只让 Decide 把【现在已归"范围外"而我们写过的那几处】退掉；
        // 排队那一格由 DropdownKit.MayEnqueue 单独挡死 ⇒ 整棵树这一次不会把 v1.1.4 的收窄顶穿。
        // 开销与①完全同级，而且更小：真实的写入只发生在"刚被关掉那一档"的几处上，
        // 其余每一处的代价是一次弱表查询 + 一个 Applied 判空（连反射读属性都不做）。
        public static void RevertOutOfScope(string reason) { Sweep(reason, false); }

        // ===== 「玩家此刻看见的是哪一屏」=====（v1.1.4 需求1 → v1.2.1 需求3 换成真判据）
        // 三样东西全是游戏自己记着的，我们只去读（反射按 §10 第 81 条解析一次并缓存）：
        //   ① m_ActivePageBinding.value    当前【页】的 id
        //      （decompiled OptionsUISystem.cs:584 建绑定；:900 `m_ActivePageBinding.Update(page?.id ?? "")`；
        //       :488 `pages` 是 Dictionary<string,Page>，键就是同一个 page.id ⇒ 可以直接和 pe.Key 比）
        //   ② m_ActiveSectionBinding.value 当前【标签页】的 id（:585 建绑定；:901 同一个函数里更新）
        //   ③ m_WidgetBindings.children    此刻真正序列化进页面的那一串控件：
        //      · 选中分区时 :902 `m_WidgetBindings.children = section.GetItems(m_IsAdvanced)`
        //        （GetItems :148-166 已经筛掉【折叠掉的分组】和【与当前简单/高级模式对不上的项】）
        //      · 搜索时 :884 `m_WidgetBindings.children = list`，list 由 :811-884 从各页命中项拼出来
        //      ⇒ 这一串就是"玩家眼前这一屏"。上下滚动不改它（滚动是 cohtml 侧的事），
        //        所以"滚动到才看见的那几行"照用户的口径算同一屏，一起翻。
        // 为什么 v1.2.0 那一版没做到（玩家原话「还是没变啊，每个模组选项界面的所有文字都给我送去翻译了」）：
        //   那一版只用了 ① + Widget.isVisible。而 `Widget.isVisible => !m_Hidden`，m_Hidden 只被
        //   hidden() 委托写（decompiled Game/UI/Widgets/Widget.cs），【跟"在不在屏幕上"没有关系】；
        //   其它标签页的控件建页时就全部存在、从没被 UpdateVisibility 碰过 ⇒ isVisible 是残留的 true。
        //   实机凭据就是玩家那一局的日志行：「选项行文字 85 处 … 玩家看不见的行跳过 2 处」—— 只拦住 2 处。
        // 兜底方向与 ShouldScanPage 一致：任何一步读不到 ⇒ 对应 known=false ⇒ 退回"多扫"，
        // 绝不能出现"玩家打开了页也不翻"（用户 2026-10-02：不能影响功能的正常使用）。
        private static FieldInfo _activePageFi;
        private static FieldInfo _activeSectionFi;
        private static FieldInfo _searchQueryFi;
        private static FieldInfo _widgetBindingsFi;
        private static PropertyInfo _bindingValuePi;
        private static bool _gateSearched;

        // 一屏的读数。字段全带 known：三样东西分三档退化，不能一起当"读到了"。
        private sealed class Gate
        {
            public string Page;             // ① 当前页 id
            public string Section;          // ② 当前标签页 id
            public bool Searching;          // 搜索框里有词（游戏靠这一格把各页命中并排显示）
            public bool Known;              // ① 读到了
            public bool SectionKnown;       // ② 读到了
            public HashSet<object> OnScreen;   // ③ 收成集合（按引用判等，见 DropdownKit.OnScreenSet）
            public bool OnScreenKnown;      // ③ 读到了、而且此刻有资格当判据
            public string Key;              // 换页/换标签页探测用的编码
        }

        private static Gate ReadGate(object sys)
        {
            var g = new Gate();
            if (sys == null) return g;
            try
            {
                if (!_gateSearched)
                {
                    _gateSearched = true;
                    const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
                    Type st = sys.GetType();
                    _activePageFi = st.GetField("m_ActivePageBinding", F);
                    _activeSectionFi = st.GetField("m_ActiveSectionBinding", F);
                    _searchQueryFi = st.GetField("m_SearchQuery", F);
                    _widgetBindingsFi = st.GetField("m_WidgetBindings", F);
                    object b = _activePageFi == null ? null : _activePageFi.GetValue(sys);
                    if (b != null) _bindingValuePi = b.GetType().GetProperty("value");
                    if (_activePageFi == null || _bindingValuePi == null)
                        ModLog.Warn("[选项页] 读不到游戏记的「当前打开的是哪一页」（字段名多半随版本改了）："
                                    + "扫描退回旧行为＝整棵树都扫。翻译功能不受影响，只是没打开的页也会送去翻译。");
                    if (_activeSectionFi == null || _widgetBindingsFi == null)
                        ModLog.Warn("[选项页] 读不到游戏记的「当前标签页」或「在屏控件清单」（字段名多半随版本改了）："
                                    + "懒翻退回上一版那一档＝只按 isVisible 拦，同页其它标签页的文字会一起送翻。");
                }
                g.Searching = _searchQueryFi != null && !string.IsNullOrEmpty(_searchQueryFi.GetValue(sys) as string);

                if (_activePageFi != null && _bindingValuePi != null)
                {
                    object pb = _activePageFi.GetValue(sys);
                    if (pb != null)
                    {
                        g.Page = _bindingValuePi.GetValue(pb, null) as string;
                        g.Known = true;
                    }
                }
                if (g.Known && _activeSectionFi != null && _bindingValuePi != null)
                {
                    object sb = _activeSectionFi.GetValue(sys);
                    if (sb != null)
                    {
                        g.Section = _bindingValuePi.GetValue(sb, null) as string;
                        g.SectionKnown = true;
                    }
                }
                object wb = _widgetBindingsFi == null ? null : _widgetBindingsFi.GetValue(sys);
                IList children = wb == null ? null : Patches.ReadObj(wb, "children") as IList;
                bool onScreenKnown;
                g.OnScreen = DropdownKit.OnScreenSet(children, g.Searching, out onScreenKnown);
                g.OnScreenKnown = onScreenKnown;
                g.Key = PageKeyOf(g);
            }
            catch (Exception ex)
            {
                // 读失败 ⇒ 三把闸全放行（宁多扫不漏扫），本轮按 v1.1.4 之前的形状走。
                g.Known = false; g.SectionKnown = false; g.OnScreenKnown = false;
                g.Page = null; g.Section = null; g.OnScreen = null; g.Key = null;
                ModLog.Warn("[选项页] 读活动页异常（本轮退回整棵树扫描）: " + ex.Message);
            }
            return g;
        }

        // 「现在打开的是哪一屏」的可比较编码。⚠ ActivePageNow()（Tick 的换页探测）与 Scan() 必须都走这一个函数，
        // 编码不一致会让"页换没换"每轮都判定成换了 ⇒ 扫描停不下来（正是本文件反复警告过的那种空转）。
        // v1.2.1 需求3：编码里必须有【标签页 id】和【在屏控件数】—— 只编页 id 的话，
        // 玩家在同一页里点另一个标签页时编码不变 ⇒ Tick 认为"没换页"、一轮都不扫，
        // 那个标签页就永远不翻（这正是懒翻收窄之后新引入的那类失效，必须在这里堵掉）。
        // 控件数负责第三档：玩家点开一个折叠分组，页和分区都没换，但屏上多了几行。
        static string PageKeyOf(Gate g)
        {
            if (!g.Known) return null;                                   // 读不到 ⇒ 不参与换页判定（Scan 自己会全扫）
            string p = string.IsNullOrEmpty(g.Page)
                       ? (g.Searching ? "(搜索中)" : "(无)")
                       : g.Page;
            if (g.SectionKnown) p += "|" + (g.Section ?? "");
            if (g.OnScreenKnown && g.OnScreen != null) p += "#" + g.OnScreen.Count.ToString(CultureInfo.InvariantCulture);
            return g.Searching ? p + "+搜索" : p;
        }

        // 每 1.2 秒最多一次（Tick 的换页探测）：向 ECS 要一次已有的系统实例 + 读几枚缓存过的字段，不碰控件树。
        // ⚠ 这里只读 children.Count（一个属性），不枚举清单 —— 枚举是 Scan 的活。
        static string ActivePageNow()
        {
            return ReadGate(Patches.GetOptionsSystem()).Key;
        }

        // ===== 一轮扫描（返回 null = 这轮根本没扫成，调用方据此决定要不要继续要下一轮） =====
        static DropdownKit.Tally Scan(string reason) { return Scan(reason, false, false); }

        // fullSweep=true = v1.1.5 的整棵树扫描（只由 RevertAll / RevertOutOfScope 触发）：
        //   ① 每一页都走（DropdownKit.ShouldVisitPage 那一格直接给 true）；
        //   ② forceOff=true ⇒ enabled 一律当成"翻译已经关了"⇒ Decide 只可能给 Restore / Skip；
        //      forceOff=false（只退"刚被关掉的那一档范围"）⇒ 用真实开关值，但 Enqueue 仍被
        //      DropdownKit.MayEnqueue 挡死，所以一条新活都不会排进队列；
        //   ③ s_Sweep 让候选项/选项行两处对"不是我们写的地方"一眼都不多看。
        // 传 (false, false) 就是 v1.1.4 那一版的行为，一个字没改。
        static DropdownKit.Tally Scan(string reason, bool fullSweep, bool forceOff)
        {
            var t = new DropdownKit.Tally();
            t.Sweep = fullSweep;
            bool ok = false;
            bool outerSweep = s_Sweep;
            HashSet<object> outerOnScreen = s_OnScreen;
            bool outerOnScreenKnown = s_OnScreenKnown;
            s_Sweep = fullSweep;
            try
            {
                if (Mod.IsTearingDown()) return null;
                object sys = Patches.GetOptionsSystem();
                if (sys == null) return null;
                IDictionary pages = Patches.ReadObj(sys, "pages") as IDictionary;
                if (pages == null) return null;

                ok = true;
                LocalizationDictionary dict = null;
                try { dict = GameManager.instance?.localizationManager?.activeDictionary; } catch { }
                bool dictReady = dict != null;          // 「没拿到字典」和「字典里没有这条」是两回事，分开报（DropdownKit.SourceMiss）
                // v1.1.5：还原扫描有两种 —— ①"整体不要"把 enabled 一律当假（把我们写过的全放回原文），
                // ②"只退刚关掉那一档范围"用真实开关值（玩家还想留着的译文不许退，退了就是白烧额度）。
                bool enabled = Patches.HookEnabled && !Patches.Paused && !(fullSweep && forceOff);
                string target = Patches.TargetLocale;
                string active = Patches.ActiveLocale;
                // 「这一轮要不要拦我们自己的文案」整轮只算一次（Normalize 要建串，400 项每条都算太浪费）：
                // 目标语言在官方 12 语之内 ⇒ 用手写表、拦；之外 ⇒ 本模组自己的下拉框候选项也照翻。
                // 这一格与 Patches.Prefix 里的 DropdownKit.SkipSelfKey 是同一条判据（那里逐条算，这里整轮算一次）。
                bool guardSelf = LanguageCatalog.IsOfficialUiLocale(target);

                // ===== v1.1.4 需求1：这一轮只处理玩家【真正打开的那一页】 =====
                // 判据不在我们这边发明 —— 抄游戏自己的 OnUpdate（decompiled OptionsUISystem.cs:1011）：
                //   `if (!string.IsNullOrEmpty(m_SearchQuery) || m_ActivePageBinding.value == value.id)`
                //   才对那一页重跑 UpdateNameAndDescription / UpdateVisibility。
                // 为什么以前不这么干：v1.0.5 起这条链一直把整棵树扫完，理由是"控件建页时就全部存在，
                // 顺手一起翻省事"。省事的是我们，代价由玩家的额度与耐心付：2026-10-02 16:00:01 那一行
                // 是「页 56 / 分区 103 / 选项行文字 2084 处 / 新入队 46+294」，1.4 秒后撞上 429、
                // 旧退避让 5 条 worker 一起歇 20 秒 ⇒ 玩家看到的是"开了翻译半天没动静，过一会才开始"。
                Gate gate = ReadGate(sys);
                // 日志那一格要能直接回答"为什么这一页没翻"：读不到（游戏改版）与"游戏还没选中任何页"
                // （面板从没开过）是两种完全不同的处置，别让它们都印成空串。
                t.ActivePage = !gate.Known ? "(读不到→按旧行为全扫)"
                              : gate.Searching ? (string.IsNullOrEmpty(gate.Page) ? "(搜索中)" : gate.Page + "+搜索")
                              : string.IsNullOrEmpty(gate.Page) ? "(游戏还没选中任何页)" : gate.Page;
                t.Budget = DropdownKit.ScanEnqueueBudget;
                // 记下"这一轮看的是哪一屏"，供 Tick 的换页探测比较（同一个编码函数，见 PageKeyOf）。
                _lastActivePage = gate.Key;
                // v1.2.1 需求3：这一轮的在屏清单交给 WalkWidget 那把闸。
                // ⚠ 还原扫描（s_Sweep）【不看】它：玩家点「清除缓存」时期望所有页都退回原文，
                //   没在屏的分区里同样挂着我们写进去的译文（v1.1.5 的整树还原就是为这个存在的）。
                s_OnScreen = fullSweep ? null : gate.OnScreen;
                s_OnScreenKnown = !fullSweep && gate.OnScreenKnown;
                // 日志那一格照实报读到的数量（还原扫描那一轮也报 —— 它说的是"游戏此刻的清单有多大"，
                // 与"这一轮有没有用它当闸"是两件事，后者由行首的 [全树还原] 已经说清了）。
                t.OnScreen = gate.OnScreenKnown && gate.OnScreen != null ? gate.OnScreen.Count : -1;

                foreach (DictionaryEntry pe in pages)
                {
                    object page = pe.Value;
                    if (page == null) continue;
                    string pageKey = pe.Key == null ? null : pe.Key.ToString();
                    // v1.2.5 修复：这一页的 id 要从 `Options.SECTION[<页id>]` 的方括号里取 —— 板块标题
                    // （Options.GROUP[…]）认领时要用它比对"这个标题是不是这一页的"。取不到就 null ⇒ 一条标题都不认领
                    //（自校验：宁可这一块没有标题，也绝不猜一条不存在的 key）。
                    string pageIdForUnit = null;
                    if (!string.IsNullOrEmpty(pageKey))
                    {
                        int lb = pageKey.IndexOf('[');
                        int rb = pageKey.LastIndexOf(']');
                        if (lb >= 0 && rb > lb + 1) pageIdForUnit = pageKey.Substring(lb + 1, rb - lb - 1);
                    }
                    if (!DropdownKit.ShouldVisitPage(fullSweep, pageKey, gate.Page, gate.Searching, gate.Known))
                    {
                        t.SkippedPages++;          // 整页没打开 ⇒ 一处都不看、一条都不排，也根本不进这一页的树
                        continue;
                    }
                    t.Pages++;
                    IList sections = Patches.ReadObj(page, "sections") as IList;
                    if (sections == null) continue;
                    // 样例日志里要能看出"这一项在哪个页"（pe.Key 是页的本地化 key，如 Options.SECTION[Graphics]）：
                    // 只有分类计数时"字典查不到 106"没法定位，配上页名才知道是游戏自己的页还是某个模组的页。
                    string pageTag = "页:" + (pageKey == null ? "?" : pageKey);
                    // v1.1.1：这一页是官方的还是某个模组的？判据只看 key 形状 —— 官方页是裸词
                    // （实机出现过的就 General / Graphics / Interface），模组的页是「模组名.分区.…」点分串
                    // （ToolModeMemory.ToolModeMemory.ToolModeMemoryMod、MapExt2.MapExtPDX.Mod.EconomyEX）。
                    // 页归属只用来给【key 形状判不出类目】的候选项定范围（Scope.ClassifyItem），
                    // 官方页里的东西一律照旧归游戏本体。
                    bool pageIsMod = pe.Key != null && pe.Key.ToString().IndexOf('.') >= 0;
                    int secNo = 0;
                    foreach (object section in sections)
                    {
                        // ===== v1.2.1 需求3：懒翻第二道闸 —— 只走玩家点着的那个标签页 =====
                        // 官方把「同一个页里的各个标签页」建成 page.sections 里的多个 section，
                        // 判据照抄游戏自己那一句（decompiled OptionsUISystem.cs:901）：
                        //   SelectSection(page, section) 里 `m_ActiveSectionBinding.Update(section?.id ?? "")`
                        // —— 绑定里那个 id 就是当前标签页，别的分区里的东西此刻根本不在屏幕上。
                        // 搜索时不能这么退：命中项散在各页各分区（:811-884），那一档由下面的在屏清单筛。
                        // ⚠ 这里 v1.2.0 用的是 `section.isVisible`，换掉了：那一格只反映模组的 hidden()
                        //   委托，跟"是不是当前标签页"无关（凭据：玩家那一局日志「选项行文字 85 处 /
                        //   玩家看不见的行跳过 2 处」，两道 isVisible 闸合计只拦住 2 处）。
                        // ⚠ 还原扫描（s_Sweep）不看这一格：整树还原要连看不见的分区一起退。
                        string secId = Patches.ReadObj(section, "id") as string;
                        if (!s_Sweep)
                        {
                            if (!DropdownKit.ShouldVisitSection(false, gate.Searching, gate.SectionKnown, gate.Section, secId))
                            { t.SkippedSections++; continue; }
                        }
                        IList options = section == null ? null : Patches.ReadObj(section, "options") as IList;
                        if (options == null) continue;
                        if (options.Count > 0) t.Sections++;
                        secNo++;
                        // v1.2.4：板块键优先用【游戏自己的分区 id】，序号只当兜底。
                        // 为什么不能只用序号：secNo 只在【没被跳过】的分区上自增，而跳过的分区随玩家点的
                        // 标签页变（ShouldVisitSection 就在上面拦）⇒ 同一个分区在 A 标签页下叫"区1"、
                        // 切到 B 标签页后可能叫"区2"。板块 hover 凭据要跨轮次复用，键飘了就会把
                        // "这个板块碰过"错认成别的板块碰过 ⇒ 那等于凭空放行一片没被看过的说明
                        //（§10 第 9 条的反面）。secId 是游戏自己写在 section 对象上的，切标签页也不变。
                        string where = pageTag + " 区" + (string.IsNullOrEmpty(secId) ? secNo.ToString() : secId);
                        // ===== v1.2.5 修复：一个板块收一次请求 =====
                        // 扫描器此刻正走在这一块自己的树上，手里就有它的全部文字（板块标题 + 选项行标题 +
                        // 每个下拉框的候选项），所以由它来收：BeginSection 之后的 Enqueue 只进收集器，
                        // 走完这一块再一次性投递成"一次请求"。
                        // 为什么不能靠攒批窗口（250ms）去接：扫描一轮 1.2 秒、界面是一行一行入队的，
                        // 实机凭据就是 2 条 / 2 条 / 5 条地发，每条 11~42 秒 —— 玩家说的"几乎每个设置项一个请求"。
                        // 预算沿用扫描那一格（DropdownKit.ScanEnqueueBudget - 本轮已入队），与逐条那道闸同一个口径。
                        bool unitOpen = !s_Sweep && options.Count > 0
                                        && TransMap.BeginSection(DropdownKit.ScanEnqueueBudget - t.NewQueues);
                        if (unitOpen) TransMap.ClaimHeadings(pageIdForUnit);   // 板块标题排在这一块最前面
                        try
                        {
                        foreach (object option in options)
                        {
                            // 读的是 Option.widget 这个自动属性（不是 SettingItemData.widget 那种懒建属性）。
                            // ⚠ "没建过的页才是 null"这句在本版已被实测推翻：官方 OptionsUISystem.RegisterSetting
                            //   在建页时就 BuildPage() → 对【每一个标签页】BuildTab() → 里面直接取 item.widget（懒建属性），
                            //   所以模组一注册选项，全部标签页的控件就已经存在了。
                            // ⚠ 但这**不等于**"其它标签页天然在扫描范围内"（v1.2.0 之前这段注释就是这么写的，
                            //   而它正是玩家那句"还是没变啊，每个模组选项界面的所有文字都给我送去翻译了"的根源）：
                            //   控件存在 ≠ 在屏幕上。此刻在屏幕上的那一串由游戏的在屏清单给（见上面 Gate 那段），
                            //   其它标签页要等玩家点过去、清单换了才进入扫描。
                            object widget = option == null ? null : Patches.ReadObj(option, "widget");
                            if (widget != null) WalkWidget(widget, dict, dictReady, guardSelf, enabled, target, active, t, where, pageTag, pageIsMod);
                        }
                        }
                        finally
                        {
                            // 一定要在 finally 里收尾：中途抛异常时，已经收进手上的那几条也得投递出去
                            //（投不出去就等于这一轮白扫，下一轮还要再等 1.2 秒）。
                            if (unitOpen)
                            {
                                int got = TransMap.EndSection();
                                if (got >= 2) ModLog.Info("[板块] " + where + " 收齐 " + got + " 条，一次请求送出（v1.2.5）。");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { ModLog.Warn("[下拉框] 扫描异常（不影响其它翻译）: " + ex.Message); }
            finally
            {
                s_Sweep = outerSweep;             // 上面有三条 return null 的早退路 ⇒ 复位必须放在 finally
                s_OnScreen = outerOnScreen;
                s_OnScreenKnown = outerOnScreenKnown;
            }

            LogScan(t, ok, reason);
            return ok ? t : null;
        }

        // 日志口径：① 这一局【第一次】真的看到下拉框控件时无论如何打一行 —— 那一行写着"扫到几个控件、
        // 多少项、其中几项翻了/几项为什么不翻"，实机验证只需要这一行就能判断成没成，玩家把它发回来就能对症。
        // ② 之后只在"有动静"时打，且最快 8 秒一条（初始那批译文是一条条落地的，不节流就会每 1.2 秒刷一行）。
        static void LogScan(DropdownKit.Tally r, bool ok, string reason)
        {
            if (!ok) return;
            // 一个候选项容器都没看到（还没进过选项页）不值得刷屏。⚠ v1.2.1 需求3 把这一格放宽了：
            // 懒翻生效时【眼前那一屏可能根本没有下拉框】（全是复选框/滑块那种页），
            // 旧写法于是整轮不打 `[下拉框]` 行 —— 玩家照 §F 第 48 条找"非当前标签页跳过 N 个分区"时
            // 一行都找不到，会把"闸在生效"误读成"闸没接上"。三把新闸只要有数可报就必须打。
            if (r.Widgets == 0 && !r.Sweep && r.Rows == 0 && r.OffScreen == 0 && r.SkippedSections == 0) return;

            bool first = !s_FirstScanLogged;
            // v1.1.5：moved 这一格以前只看候选项那三个数，于是【只动选项行】的那一轮在日志里完全不留痕。
            // 玩家报「清了缓存界面还是日文」时，20:22:44 那一刻日志里恰好就一行 [下拉框] 都没有 ——
            // 我们因此无法回答"还原到底跑没跑、跑了几个"，只能靠推理。补齐这一格的口径。
            // Sweep 那一轮无论如何都打：一次点击才有一轮，不会刷屏，而它正是这一版要的实机凭据。
            bool moved = r.Applied > 0 || r.Restored > 0 || r.Enqueued > 0
                         || r.RowApplied > 0 || r.RowRestored > 0 || r.RowEnqueued > 0;
            int now = Environment.TickCount;
            if (!first && !moved && !r.Sweep) return;
            if (!first && !r.Sweep && now - _lastLogTick < LogIntervalMs) return;
            s_FirstScanLogged = true;
            _lastLogTick = now;
            // 第一次那一行多带一段诊断：页/分区数（= 其它标签页有没有被扫到）+ 没取到源文本的六类原因。
            // 之后不再打，免得每次刷新界面都刷两行长文本。
            ModLog.Info("[下拉框] " + r.Line() + (first ? r.Diag() + " " : "")
                        + (string.IsNullOrEmpty(reason) ? "" : "原因：" + reason));
            // 第一次那一行后面再补一行样例（每类最多 3 条、共 24 条）：分类计数只说"有多少项卡在哪儿"，
            // 样例说"那些项长什么样"。2026-09-29 追 AccessAnarchy 的下拉框时，靠的就是真 key 的形状
            // （mode.access 这种小写点分 key，官方 22,163 条 key 里一条都没有）才定到位。
            // ⚠ 现在【每一轮打出统计行的时候】都带上样例，不再只打第一次：v1.1.0 只打第一次，于是
            // 「写入译文」这类【后来才发生】的动作在日志里永远没有样本，玩家说"没翻"时无从对质。
            if (!first)
            {
                string s2 = r.Samples();
                if (s2.Length > 0) ModLog.Info("[下拉框] " + s2);
            }
            else
            {
                string s = r.Samples();
                if (s.Length > 0) ModLog.Info("[下拉框] " + s);
                // 分页明细（只在第一次打）：哪个模组的哪一页有几项、各自卡在哪一格。
                // 玩家报的永远是"某某模组那个框"，按页归拢的这一行才能一行对上号。
                string pd = r.PagesDetail();
                if (pd.Length > 0) ModLog.Info("[下拉框] " + pd);
            }
        }

        // ===== 单个控件 =====
        static void WalkWidget(object widget, LocalizationDictionary dict, bool dictReady, bool guardSelf, bool enabled,
                               string target, string active, DropdownKit.Tally t, string where, string pageTag, bool pageIsMod)
        {
            try
            {
                // ===== v1.2.1 需求3：懒翻第三道闸 —— 这一行此刻在不在屏幕上 =====
                // 两道并列，判据各来自游戏的一处代码，缺一个都会漏：
                //   ① isVisible（游戏 Section.UpdateNameAndDescription 对每个 NamedWidget 先看这一格，
                //      decompiled OptionsUISystem.cs:233-245）—— 管模组的 hidden() 委托：
                //      被别的设置项带着隐藏的行，游戏自己不渲染，我们也不该翻。
                //      ⚠ 但它【管不到标签页】：`Widget.isVisible => !m_Hidden`，m_Hidden 只由 hidden() 写，
                //      非当前标签页的那些控件建页时就全部存在、m_Hidden 是从没刷新过的残留值。
                //   ② 在屏清单（游戏 SelectSection 里 `m_WidgetBindings.children = section.GetItems(...)`）——
                //      管标签页、管折叠分组、管简单/高级模式：这三种"此刻不在屏幕上"游戏都已经在清单里筛掉了。
                // 清单读不到（游戏改版）⇒ 放行，与 ShouldScanPage 的 activeKnown 同一个兜底方向。
                if (!s_Sweep)
                {
                    if (widget is IVisibleWidget vis && !DropdownKit.ShouldVisitWidget(false, vis.isVisible, true))
                    {
                        t.Invisible++;
                        return;
                    }
                    if (!DropdownKit.IsOnScreen(s_OnScreenKnown, s_OnScreen, widget))
                    {
                        t.OffScreen++;
                        return;
                    }
                }

                // ===== v1.1.3：先处理这一行的【标题与说明】=====
                // 为什么放在 MetaOf 之前：候选项那套只认得下拉框控件（Meta 拿不到就 return），
                // 而标题/说明挂在 NamedWidget 上 —— 复选框、滑块、按钮这些【没有候选项列表】的行
                // 同样是 NamedWidget。玩家报的"整页英文、连说明都不翻"就出在这一层。
                if (widget is NamedWidget nw)
                {
                    string rowAt = t.WantsSamples ? where + " " + widget.GetType().Name : null;
                    HandleRowText(nw, true, dict, dictReady, guardSelf, enabled, target, active, t, rowAt, pageIsMod);
                    // ===== v1.2.3 需求2：说明那一半【照常调】，按不按住在 HandleRowText 里判 =====
                    // 玩家原话（这件事第三次提）：「设置项的说明文本还是会一起翻译啊，根本不是鼠标移上去
                    // 显示后才开始翻译，再仔细修复好。」
                    // 前两版（v1.2.1「说明等标题落地」、v1.2.2「标题落地后的下一轮再排」）改的都只是【延多久】，
                    // 等的却是自己这条链上的下一个状态（标题翻完没、译文写上没）⇒ 玩家看到的仍是
                    // "整页说明跟着一屏翻完"，两轮都没修到点上。本版改的是【等什么】：
                    // 等一个真的只在 hover 那一刻才出现的信号。前端只在有一行被移到眼前时才渲染那块信息栏，
                    // 且只渲染【那一行】的说明（实机 index.js 的选项页 z4 / K4.infoDescription 逐字核过：
                    // `E && … infoDescription: T.props.description ? <yd value={T.props.description}/> : …`），
                    // 所以"这一行的说明 id 被前端取过词"就等于"玩家把它移到过眼前"。
                    // 凭据由 Prefix 记（DropdownL10n.NoteKeyRequested），判据是 DropdownKit.HoldDescription，
                    // 两处合起来才是完整的一条链；日志里对应「说明等悬停（前端取过词才送）N 处」那一格。
                    // ⚠ 闸必须放在 HandleRowText 里而不是这一句上：说明的 id 可能藏在 descriptionAction 后面
                    //（模组动态给值），而那个委托每 1.2 秒只能调一次 —— HandleRowText 首轮把它读进 s.Original
                    // 之后再看 id 才不会重复执行模组的代码。标题那一路没有闸，所以两句都无条件调。
                    HandleRowText(nw, false, dict, dictReady, guardSelf, enabled, target, active, t, rowAt, pageIsMod);
                }

                Meta m = MetaOf(widget.GetType());
                if (m == null) return;
                Array list = m.List.GetValue(widget, null) as Array;
                if (list == null || list.Length == 0) return;

                t.Widgets++;
                t.Items += list.Length;
                bool changed = false;
                // 样例里带上控件类型（EnumField / DropdownField`1 …）：同一页里两种控件的取值口径不一样，
                // "哪个值送去翻才会正确显示"这个问题只能靠它来回答。⚱ 只在还要抓样例时才拼这个串
                // （本函数每 1.2 秒对 71 个控件各跑一次，白建串就是白占用主线程）。
                string at = t.WantsSamples ? where + " " + widget.GetType().Name : null;
                // 分页明细那一行（DropdownKit.Tally.PageRow）：按【页】归拢，不看控件也不看分区，
                // 玩家报"某某模组的下拉框没翻"时，这一行直接给出那一页卡在哪一格。
                DropdownKit.Tally.PageRow row = t.Row(pageTag);

                // ===== v1.1.2：整栏用途先看一眼 =====
                // 「选择语言」和「数字档位」这两栏是【故意一条都不翻】的（玩家 2026-10-01 点名，实机凭据
                // 是那句把 Français 翻成"英语"的日志）。判据与反射分开：DropdownKit.ClassifyColumn 是纯函数，
                // 离线表 X 段直接钉；这里只做"读一栏文字"和"记账"。
                ColumnVerdict verdict;
                if (!s_columns.TryGetValue(widget, out verdict))
                {
                    verdict = new ColumnVerdict(DropdownKit.ClassifyColumn(ColumnTexts(list, m)));
                    s_columns.Add(widget, verdict);
                }
                if (DropdownKit.SkipWholeColumn(verdict.Kind))
                {
                    bool isLang = verdict.Kind == DropdownKit.ColumnKind.LanguagePicker;
                    if (isLang) t.LangWidgets++; else t.TimeWidgets++;
                    if (row != null) row.Items += list.Length;
                    if (at != null)
                        t.Sample("整栏不翻", at + " 用途=" + (isLang ? "选择语言" : "数值档位")
                                 + " 共" + list.Length + "项 例子=" + Probe.Trunc(JoinExamples(list, m, 3), 90));
                    return;     // 一条都不碰，也不给控件发属性变更（免得白刷一帧）
                }

                for (int i = 0; i < list.Length; i++)
                {
                    object item = list.GetValue(i);
                    if (item == null) continue;
                    if (StepOnItem(item, m, dict, dictReady, guardSelf, enabled, target, active, t, at, pageIsMod, row)) changed = true;
                }

                if (changed && m.SetChanged != null) m.SetChanged.Invoke(widget, null);
            }
            catch (Exception ex)
            {
                // 单个控件失败只影响它自己：游戏改版最可能只改一种控件，别让整轮扫描跟着停。
                // 只报第一次（这轮之后每一轮都会撞上同一个控件，重复报只是刷屏）。
                if (!s_WalkWarned)
                {
                    s_WalkWarned = true;
                    ModLog.Warn("[下拉框] 处理控件 " + widget.GetType().Name + " 异常（同类只报这一次，不影响其它翻译）: " + ex.Message);
                }
            }
        }

        // ===== 一处选项行文字（标题或说明）=====
        // 为什么必须有这一条（v1.1.3，玩家点名 Real Weather Sync 与 Plop the Growables「整页英文、连说明都不翻」）：
        // 这一层文字不走 UILocalizationManager.Translate —— 模组给的是
        // LocalizedString.IdWithFallback(key, "英文原句") 或 LocalizedString.Value("英文原句")
        // （一手凭据：decompiled Game/UI/Menu/AutomaticSettings.cs:348/365 官方自己就这么建，
        //  LocalizedString.cs:64-72 的 IdWithFallback 同时带 id 和 value），前端看见 value 非空就直接上屏。
        // 所以我们挂在 Translate 上的钩子对这类文字【一次都没被调用过】—— 实机凭据：10-02 那一局
        // 同一页里标题有 7 条 [懒翻译]、dll 里那些说明句一条都没进过日志。
        // 判据与候选项那一套完全共用（ResolveRowSource / Decide / IsSafeToDisplay + Scope 的类目与本地预筛），
        // 只有【写哪里】不同：这里写 NamedWidget，而且模组可能是用 action 动态给值的 ——
        // 那种情况换掉 action（模组原来那个存在 RowSlot 里），还原时放回去，
        // 于是"跟着档位变的标题"不会因为我们翻过一次就永久冻死。
        static void HandleRowText(NamedWidget w, bool isName, LocalizationDictionary dict, bool dictReady,
                                  bool guardSelf, bool enabled, string target, string active,
                                  DropdownKit.Tally t, string at, bool pageIsMod)
        {
            RowState st;
            bool have = s_rows.TryGetValue(w, out st);
            // v1.1.5 还原扫描：这一行我们一处都没写过 ⇒ 连 RowSlot 都不建、属性都不读，直接返回。
            // 全树要过的槽位是 2084 处（v1.1.4 实机那一行），其中我们真写过的通常只有几处到几十处 ⇒
            // 这一轮的开销只与"我们写过多少处"有关，与树有多大无关（§10 第 81 条那条规矩的正解）。
            if (s_Sweep && (!have || !RowTouched(st, true) && !RowTouched(st, false))) return;
            if (!have) { st = new RowState(); s_rows.Add(w, st); }
            if (isName) { if (st.Name == null) st.Name = new RowSlot(); }
            else { if (st.Desc == null) st.Desc = new RowSlot(); }
            RowSlot s = isName ? st.Name : st.Desc;
            if (s_Sweep && s.Applied == null) return;        // 标题写过、说明没写过那种：另一半也别多看

            // ---- 第一次见到这一处：抓模组的原样 ----
            if (!s.Seen)
            {
                s.Seen = true;
                Func<LocalizedString> act = isName ? w.displayNameAction : w.descriptionAction;
                s.HasAction = act != null;
                s.Action = act;
                LocalizedString orig = s.HasAction ? CallAction(act) : (isName ? w.displayName : w.description);
                if (orig.isEmpty) return;         // 这一行根本没有这句话（多数控件没有说明）：不计数、不翻
                s.Original = orig;
                t.Rows++;
            }
            if (s.Original.isEmpty) return;

            // ---- v1.2.3 需求2：说明那一半的悬停闸（判据在 DropdownKit.HoldDescription，只此一处）----
            // 这一行的说明 id 前端还没取过词 ⇒ 玩家还没把它移到眼前，按住不送（t.DescHeld 记一笔）。
            // ⚠ 这一问必须【每轮都问】，不能拿本轮的答案记账了事：凭据只在玩家 hover 那一刻才会出现，
            //   而玩家可能这一局从头到尾都不去看某些行 —— 一旦"问过一轮之后放行"，整页说明还是会在
            //   1.2 秒后照常排队，等于回到玩家已经退回两次的形状（v1.2.1/v1.2.2 都栽在这一步）。
            //   代价可控：查表只发生在"带说明且前端还没取过词"的行上，一轮一次、每行一次 lock+Contains
            //   （说明族才进表，量级=页内说明行数；§10 第 81 条查的是周期热路径上只做与变化有关的事，
            //   这里做的正是唯一有关的那件事）。DescHeld 因此与 v1.2.1/v1.2.2 同一口径：按轮计，
            //   玩家把鼠标移上去之后这一格掉下来 —— 日志一眼可验。
            // 还原扫描（s_Sweep）不看闸：那一轮要把所有页写过的译文退回去，而且它一条新活都不排。
            // 没有 id 的说明（模组硬塞的裸文字，前端 <yd> 直接上屏、从不调取词钩子）也不看闸：
            // 等下去就是永远不翻 —— 实机 358 条说明里 6 条是这种，全是版本字符串一类的死文字。
            //
            // ⚠ v1.2.4 需求3 曾试过把这一格放宽成两级（行 + 板块），玩家当场否掉：
            //   「我说的是设置项标题和设置项……这不包括设置项的说明，说明依然还是鼠标移上去显示后才翻译」。
            //   所以这一闸的粒度【永远是一行】，凭据只来自"前端为这一行说明的 id 取过词"。
            //   需求3 要的"整板块一起送翻"落在标题/候选项那一路（它们不看闸，按扫描顺序入队），
            //   由 TryBatch 的有界攒批 + ReturnToHead 保序满足 —— 与说明这一半无关。
            if (DropdownKit.HoldDescription(s_Sweep, s.Original.id != null, DescriptionRequested(s.Original.id)))
            {
                t.DescHeld++;
                return;
            }

            // ---- 没有 action 的行：模组/别的模组可能自己把这句话换掉了 ----
            // 只有【我们没写过】时才承认"控件里现在这句就是新的原文"。我们写过的时候控件里挂的就是我们的译文，
            // 拿它当原文会把译文再翻一遍（本类所有动作的前提都是"只有我们改过的才归我们还原"，同 StepOnItem）。
            if (!s.HasAction && s.Applied == null)
            {
                LocalizedString now = isName ? w.displayName : w.description;
                if (!now.Equals(s.Original)) { s.Original = now; s.Source = null; s.Tried = false; s.Retry = true; }
            }

            // ---- 解析待翻源文本（暂时性失败留到下一轮，永久拿不到的不再白查字典）----
            if (s.Source == null && (!s.Tried || s.Retry))
            {
                s.Tried = true;
                DropdownKit.SourceMiss miss;
                string dictValue = Lookup(dict, s.Original.id);
                s.Source = DropdownKit.ResolveRowSource(s.Original.id, s.Original.value, dictValue,
                                                        dictReady, guardSelf, out miss);
                s.Retry = miss == DropdownKit.SourceMiss.DictNotReady || miss == DropdownKit.SourceMiss.NotInDict;
                if (miss == DropdownKit.SourceMiss.FallbackValue) t.RowFallback++;
                if (at != null && (s.Source != null || miss == DropdownKit.SourceMiss.NotInDict
                                               || miss == DropdownKit.SourceMiss.NoId))
                    t.Sample("选项行" + miss, at + (isName ? " 标题" : " 说明")
                             + " id=" + Probe.Trunc(s.Original.id ?? "(无)", 40)
                             + " 兜底=" + Probe.Trunc(s.Original.value ?? "(无)", 30)
                             + " 成品=" + Probe.Trunc(s.Source ?? "(不翻)", 30));
            }
            if (s.Source == null) return;

            string tr;
            bool has = TransMap.TryGetTranslated(s.Source, out tr) && !string.IsNullOrEmpty(tr) && tr != s.Source;
            bool resolved = TransMap.IsResolved(s.Source);
            // 范围：先按这一行自己的本地化 key 判类目，key 空（纯兜底值那种）由【页归属】兜底 ——
            // 与候选项共用 Scope.ClassifyItem 那一条，关掉「模组选项及说明」时这里照样退回原文。
            bool inScope = Patches.ScopeEnabled(Scope.ClassifyItem(s.Original.id, pageIsMod));
            int act2 = DropdownKit.Decide(s.Source, has ? tr : null, resolved, s.Applied, enabled, inScope);

            // 换了目标语言：先退回模组原文，再按新语言重新排队（与候选项同一条链）
            if (DropdownKit.IsStale(s.AppliedLocale, target))
            {
                RestoreRow(w, isName, s);
                s.Applied = null;
                s.AppliedLocale = null;
                t.RowRestored++;
                act2 = DropdownKit.Decide(s.Source, has ? tr : null, resolved, null, enabled, inScope);
            }

            switch (act2)
            {
                case DropdownKit.Apply:
                    if (!DropdownKit.IsSafeToDisplay(tr)) { t.Unsafe++; return; }
                    WriteRowValue(w, isName, s, tr);
                    s.Applied = tr;
                    s.AppliedLocale = target;
                    t.RowApplied++;
                    if (at != null)
                        t.Sample("选项行写入", at + (isName ? " 标题" : " 说明")
                                 + " id=" + Probe.Trunc(s.Original.id ?? "(无)", 36)
                                 + " 源=" + Probe.Trunc(s.Source, 22) + " → 上屏=" + Probe.Trunc(tr, 22));
                    return;

                case DropdownKit.Restore:
                    RestoreRow(w, isName, s);
                    s.Applied = null;
                    s.AppliedLocale = null;
                    t.RowRestored++;
                    return;

                case DropdownKit.Enqueue:
                    if (resolved) return;
                    // 与 Prefix / 候选项同一套本地预筛：纯数字、键位串、已是目标语言 ⇒ 记成无需翻译，不联网
                    if (Scope.ShouldSkipLocal(s.Source, target, active)) { TransMap.ResolveAsSkip(s.Source); t.RowSkipped++; return; }
                    // v1.1.5：整树扫描那一轮一条新活都不排（判据与候选项那一格共用 DropdownKit.MayEnqueue）。
                    if (!DropdownKit.MayEnqueue(s_Sweep)) { t.Deferred++; return; }
                    // v1.1.4 需求1：与候选项共用同一本预算（限流算的是总请求数，分两本等于预算翻倍）。
                    // 源文本已经解析好并存在 RowSlot 里，所以下一轮直接走到这一格排队，不重查字典。
                    if (!DropdownKit.BudgetLeft(t.NewQueues)) { t.Deferred++; return; }
                    TransMap.NoteScope(s.Source, Scope.ClassifyItem(s.Original.id, pageIsMod));   // v1.2.0 需求5：与上面 inScope 同一判决据，只是拿来给提示词分语境
                    // v1.2.5 修复：按返回值记账 —— 收板块期间"进了收集器"才算入队；已经在队里/已经翻好/额度用完
                    // 的那几条不能也 +1（否则日志说排了 24 条、其实一次请求只发了 5 条，越看越糊涂）。
                    if (TransMap.Enqueue(s.Source)) t.RowEnqueued++;
                    return;

                case DropdownKit.Keep:
                    t.RowKept++;
                    return;

                case DropdownKit.Skip:
                    if (!inScope) t.RowOutOfScope++; else t.RowSkipped++;
                    return;
            }
        }

        // 这一行的这一半【是不是我们写过的】（v1.1.5 还原扫描用的那把便宜闸门：没写过就一眼都不多看）。
        static bool RowTouched(RowState st, bool isName)
        {
            RowSlot s = isName ? st.Name : st.Desc;
            return s != null && s.Applied != null;
        }

        // 把译文写进 NamedWidget：有 action 就换 action（模组那个存在 RowSlot 里，随时放得回去），
        // 没有 action 才直接写属性 —— ⚠ 写属性这个 setter 会把 displayNameAction 置 null
        // （NamedWidget.cs:22-38），所以绝不能在有 action 的行上走这条路，否则动态标题就永久冻住了。
        static void WriteRowValue(NamedWidget w, bool isName, RowSlot s, string text)
        {
            LocalizedString v = LocalizedString.Value(text);
            if (s.HasAction && s.Action != null)
            {
                if (isName) w.displayNameAction = () => v; else w.descriptionAction = () => v;
                w.UpdateNameAndDescription(true);      // 让游戏重算并广播 props（公开方法，decompiled NamedWidget.cs:57）
            }
            else
            {
                if (isName) w.displayName = v; else w.description = v;
                w.SetPropertiesChanged();
            }
        }

        // 还原成模组原来那句话（关翻译 / 关范围 / 换目标语言 / 译文不合格时走这里）
        static void RestoreRow(NamedWidget w, bool isName, RowSlot s)
        {
            if (s.HasAction && s.Action != null)
            {
                if (isName) w.displayNameAction = s.Action; else w.descriptionAction = s.Action;
                w.UpdateNameAndDescription(true);
            }
            else
            {
                if (isName) w.displayName = s.Original; else w.description = s.Original;
                w.SetPropertiesChanged();
            }
        }

        // 模组的 action 是别人写的代码：出错只当这句没有，绝不让选项页扫描跟着炸。
        static LocalizedString CallAction(Func<LocalizedString> f)
        {
            if (f == null) return default(LocalizedString);
            try { return f(); } catch { return default(LocalizedString); }
        }

        // 返回 true = 这一项被改写过（调用方据此决定要不要让控件重发 props）。
        static bool StepOnItem(object item, Meta m, LocalizationDictionary dict, bool dictReady, bool guardSelf, bool enabled,
                               string target, string active, DropdownKit.Tally t, string at, bool pageIsMod,
                               DropdownKit.Tally.PageRow row)
        {
            bool changed = false;
            object boxed = m.DisplayName.GetValue(item, null);
            if (!(boxed is LocalizedString cur))
            {
                t.NotItem++;
                if (at != null) t.Sample("元素形状不符", at + " 元素=" + (boxed == null ? "null" : boxed.GetType().Name));
                return false;   // 元素形状和游戏对不上（改版），报出来而不是静默吞掉
            }

            Origin o;
            bool firstSight = !s_origins.TryGetValue(item, out o);
            // v1.1.5 还原扫描：这一项不是我们写进去的（没见过 / 写过但已被游戏或别的模组换掉）⇒ 一眼都不多看。
            // 全树那一轮要过的候选项是几百到上千项，而"我们写过的"只有几项到几十项 ——
            // 少了这道闸，还原扫描的开销就与控件树大小成正比（§10 第 81 条）。
            if (s_Sweep && (firstSight || o.Applied == null)) return false;
            if (firstSight)
            {
                o = new Origin { Original = cur };
                s_origins.Add(item, o);
                if (row != null) row.Items++;   // 分页明细只数【第一次见到】的项：扫描每 1.2 秒跑一轮，
                                                 // 重复计数会让"这一页几项"随时间无限膨胀，那行日志就没法读了。
            }
            else if (cur.id != null && cur.id != o.Original.id)
            {
                // 游戏自己把这一项的文字换掉了（换了档位的候选列表、或别的模组动了它）：记录重抓。
                o.Original = cur;
                o.Source = null;
                o.Retry = true;
                o.Applied = null;
                o.AppliedLocale = null;
            }
            else if (cur.id == null && o.Applied != null && cur.value != o.Applied)
            {
                // 控件里挂着一句【不是我们写的】裸文字：可能是游戏直接赋了成品值。
                // 记不清就不要再往里写，避免跟别的模组抢同一行文字（本类所有动作的前提是"只有我们改过的才归我们还原"）。
                o.Applied = null;
                o.AppliedLocale = null;
            }

            // 解析待翻源文本。⚠ 只有【可能翻盘的那几种失败】才留到下一轮重试：
            //   · "字典没拿到"（GameManager 还没就绪／主菜单里那一轮）和"字典查不到"（词典是分批装进来的）
            //     是暂时性的 ⇒ Retry=true；
            //   · "没有 key"（游戏故意显示裸文字：语言列表的母语名、我们的模型 slug）、"是我们自己的文案"、
            //     "命中值只有一个字符" 是永久性的 ⇒ 不再看它，免得每轮白查几百次字典。
            // 这一格在 2026-09-27 真咬过一口：第一次扫描时字典还没就绪，448 项被一次性判死，
            // 后面即使就绪也再没回来过（那局 10:11 那一行的 55 项全来自游戏重建的新对象，不是重试来的）。
            if (o.Source == null && (firstSight || o.Retry))
            {
                DropdownKit.SourceMiss miss;
                string dictValue = Lookup(dict, cur.id);
                o.Source = DropdownKit.ResolveSource(cur.id, dictValue, dictReady, guardSelf, out miss);
                o.Retry = miss == DropdownKit.SourceMiss.DictNotReady || miss == DropdownKit.SourceMiss.NotInDict;
                if (firstSight)
                {
                    t.Count(miss);
                    if (row != null)
                    {
                        switch (miss)
                        {
                            case DropdownKit.SourceMiss.Ok: row.Dict++; break;
                            case DropdownKit.SourceMiss.LiteralId: row.Literal++; break;
                            case DropdownKit.SourceMiss.NotInDict: row.NotInDict++; break;
                            case DropdownKit.SourceMiss.NoId: row.NoId++; break;
                            case DropdownKit.SourceMiss.SelfKey: row.SelfKey++; break;
                            case DropdownKit.SourceMiss.TooShort: row.TooShort++; break;
                        }
                    }
                    // 每类抓 3 条真 key（DropdownKit.Tally.Sample 限量）：光有分类数判不了案，
                    // 看到 "key=mode.access 值=Access zones only" 这种形状才知道该改哪一层。
                    // id 是 LocalizedString.id、值是当前语言字典里的那条、成品=我们真正会写回控件的那句。
                    // ⚠ LiteralId 那一桶里 字典=(查不到) 而 成品=id 本身 —— 这正是 v1.1.1 放开的那条出口。
                    if (at != null)
                        t.Sample(miss.ToString(), at + " id=" + Probe.Trunc(cur.id ?? "(无)", 40)
                                 + " 字典=" + Probe.Trunc(dictValue ?? "(查不到)", 30)
                                 + " 成品=" + Probe.Trunc(o.Source ?? "(不翻)", 30));
                }
            }

            if (o.Source == null) return false;     // 不归我们管：自己的文案 / 裸文字项 / 字典里查不到的键名形状

            string tr;
            bool has = TransMap.TryGetTranslated(o.Source, out tr) && !string.IsNullOrEmpty(tr) && tr != o.Source;
            bool resolved = TransMap.IsResolved(o.Source);
            // 范围开关：先按这一项自己的本地化 key 分类，key 形状判不出来（模组的裸文字 id）时由【页归属】兜底
            // —— 详见 Scope.ClassifyItem 那段：这是 v1.1.1 与"裸文字送翻"必须同时落地的另一半，
            // 少了它，这些项解开了一道闸又撞上下一道（玩家普遍没勾「游戏本体」，本机实测 ScopeGameCore=false）。
            bool inScope = Patches.ScopeEnabled(Scope.ClassifyItem(o.Original.id, pageIsMod));
            int act = DropdownKit.Decide(o.Source, has ? tr : null, resolved, o.Applied, enabled, inScope);

            // 换了目标语言：先把旧语言的译文退回原文，再按新语言重新排队（DropdownKit 不看语言，所以这一步在这做）。
            if (DropdownKit.IsStale(o.AppliedLocale, target))
            {
                Write(m, item, o.Original);
                o.Applied = null;
                o.AppliedLocale = null;
                t.Restored++;
                changed = true;
                act = DropdownKit.Decide(o.Source, has ? tr : null, resolved, null, enabled, inScope);
            }

            switch (act)
            {
                case DropdownKit.Apply:
                    // 空串/整串空白/超长（引擎把整段说明当一句回回来的跑飞结果）绝不上屏 ——
                    // 往 non-nullable string writer 里塞 null 就是 §10 第 15c 条那次整帧崩溃的形状。
                    if (!DropdownKit.IsSafeToDisplay(tr)) { t.Unsafe++; return changed; }
                    Write(m, item, LocalizedString.Value(tr));
                    o.Applied = tr;
                    o.AppliedLocale = target;
                    t.Applied++;
                    if (row != null) row.Applied++;
                    // 「写入译文」这一桶样例是 v1.1.1 的实机验收凭据：日志说写了、写的是哪一句、玩家眼睛里的
                    // 下拉框是不是那一句 —— 三件事在同一行里对得上，才能回答"前端有没有按新 props 重绘"
                    // 这个离线钉不住的问题（交接文档 §7 T4）。
                    if (at != null)
                        t.Sample("写入译文", at + " id=" + Probe.Trunc(o.Original.id ?? "(无)", 36)
                                 + " 源=" + Probe.Trunc(o.Source, 24) + " → 上屏=" + Probe.Trunc(tr, 24));
                    return true;

                case DropdownKit.Restore:
                    Write(m, item, o.Original);
                    o.Applied = null;
                    o.AppliedLocale = null;
                    t.Restored++;
                    return true;

                case DropdownKit.Enqueue:
                    if (resolved) return changed;
                    // 与 Prefix 完全相同的本地预筛：纯数字/键位串/已是目标语言 → 记成"无需翻译"，不联网、不占队列。
                    if (Scope.ShouldSkipLocal(o.Source, target, active)) { TransMap.ResolveAsSkip(o.Source); t.Skipped++; return changed; }
                    // v1.1.5：整树扫描那一轮【一条新活都不排】（两种扫描都要守这一条：
                    // "整体不要"那种 enabled 已经是假、走不到这里；"只退关掉那一档范围"那种走真实 enabled、
                    // 真会走到这里 —— 不挡就把 55 个没打开的页一次塞进队列，v1.1.4 的收窄当场作废）。
                    // 记进 Deferred 而不是静默丢弃：正常扫描下一轮会照排（那一轮才受 24 条预算管）。
                    if (!DropdownKit.MayEnqueue(s_Sweep)) { t.Deferred++; return changed; }
                    // v1.1.4 需求1：本轮的送翻预算用完了 ⇒ 这一项【什么都不动】地留给下一轮。
                    // 关键在于"不动"：不 mark 已处理、不写控件、不进队列，所以 1.2 秒后同一条判据会原样再走一遍。
                    // Deferred 让 Tally.Idle 为假 ⇒ Want 不被清 ⇒ 这一页一定会被排完（不会"只翻了前 24 条"）。
                    if (!DropdownKit.BudgetLeft(t.NewQueues)) { t.Deferred++; return changed; }
                    t.Pending++;
                    TransMap.NoteScope(o.Source, Scope.ClassifyItem(o.Original.id, pageIsMod));   // v1.2.0 需求5：与上面 inScope 同一判决据，只是拿来给提示词分语境
                    // v1.2.5 修复：按返回值记账（同选项行那一格）。
                    if (TransMap.Enqueue(o.Source))
                    {
                        t.Enqueued++;
                        if (row != null) row.Enqueued++;
                    }
                    return changed;

                case DropdownKit.Keep:
                    t.Kept++;
                    return changed;

                case DropdownKit.Skip:      // 判据说"这一项别动"，但【为什么】要分开报：
                    //   没勾范围开关 ⇒ 是玩家的取舍（范围外）；已经知道不需要翻（本地预筛命中/三次判死）⇒ 跳过。
                    // 2026-09-29 那一局 405 项里凭空少了 150 项，就是因为这一格以前什么都不计，
                    // 七个动作全 0 又"看着正常"，白绕了一局才定位到「游戏本体」没勾。
                    if (!enabled || !inScope) t.OutOfScope++;
                    else t.Skipped++;
                    if (!enabled || !inScope) { if (row != null) row.OutOfScope++; }
                    if (at != null && (!enabled || !inScope))
                        t.Sample("范围外", at + " id=" + Probe.Trunc(o.Original.id ?? "(无)", 40)
                                 + " 源=" + Probe.Trunc(o.Source, 30)
                                 + "（总开关=" + (enabled ? "开" : "关") + " 这一类范围=" + (inScope ? "勾了" : "没勾") + "）");
                    return changed;
            }
            return false;   // 走到这里 = 游戏改版多出一个我们没认得的处置码：什么都不做，别猜
        }

        static void Write(Meta m, object item, LocalizedString value)
        {
            m.DisplayName.SetValue(item, value, null);
        }

        // 读一栏候选项的文字，供整栏用途判定（v1.1.2）。上限 32 项 —— 语言列表最长也就 40 来项，
        // 判定用的是比例，取前 32 项足够稳，又不会让某个巨大的模组列表把主线程拖住。
        // 取词口径与 StepOnItem 完全一致：id 优先（模组把显示文字塞进 id 的那类），id 空才看 value。
        static List<string> ColumnTexts(Array list, Meta m)
        {
            var names = new List<string>(Math.Min(list.Length, 32));
            int cap = Math.Min(list.Length, 32);
            for (int i = 0; i < cap; i++)
            {
                object item = list.GetValue(i);
                if (item == null) { names.Add(null); continue; }
                object boxed = m.DisplayName.GetValue(item, null);
                if (!(boxed is LocalizedString ls)) { names.Add(null); continue; }
                names.Add(!string.IsNullOrEmpty(ls.id) ? ls.id : ls.value);
            }
            return names;
        }

        // 日志样例里那一栏的前几条文字（只给样例用，读三条就够看清"这栏是干什么的"）。
        static string JoinExamples(Array list, Meta m, int count)
        {
            var sb = new StringBuilder();
            int cap = Math.Min(list.Length, count);
            for (int i = 0; i < cap; i++)
            {
                object item = list.GetValue(i);
                if (item == null) continue;
                object boxed = m.DisplayName.GetValue(item, null);
                if (!(boxed is LocalizedString ls)) continue;
                string s = !string.IsNullOrEmpty(ls.id) ? ls.id : ls.value;
                if (string.IsNullOrEmpty(s)) continue;
                if (sb.Length > 0) sb.Append(" / ");
                sb.Append(s);
            }
            return sb.ToString();
        }

        // 查当前语言字典。I18NEverywhere 这类模组 patch 了 TryGetValue，所以这里拿到的人工译文优先，
        // 与 Prefix 同一份取值口径。⚠ 返回 null（查不到）现在【不再等于"这一项不翻"】：
        // 游戏在字典查不到时把 key 原样上屏（decompiled UILocalizationManager.cs:27），所以那串 id 本身就是
        // 玩家看见的文字。要不要拿它当源文本由 DropdownKit.LooksLikeDisplayText 按形状判（键名形状仍不翻）。
        static string Lookup(LocalizationDictionary dict, string id)
        {
            if (dict == null || string.IsNullOrEmpty(id)) return null;
            try { return dict.TryGetValue(id, out string v) ? v : null; }
            catch { return null; }
        }

        // ===== 反射元数据（按控件类型缓存） =====
        static Meta MetaOf(Type t)
        {
            if (s_meta.TryGetValue(t, out Meta m)) return m;
            m = Build(t);
            s_meta[t] = m;          // null 也记下来：不是下拉框的控件占一个槽，避免每轮重新反射一遍
            return m;
        }

        static Meta Build(Type t)
        {
            var mi = new Meta();
            mi.SetChanged = t.GetMethod("SetPropertiesChanged",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (string propName in DropdownKit.ListProperties())
            {
                System.Reflection.PropertyInfo list = t.GetProperty(propName, BindingFlags.Instance | BindingFlags.Public);
                if (list == null || !list.CanRead || !list.PropertyType.IsArray) continue;

                Type elem = list.PropertyType.GetElementType();
                if (elem == null) continue;

                System.Reflection.PropertyInfo dn = elem.GetProperty("displayName", BindingFlags.Instance | BindingFlags.Public);
                if (dn == null || !dn.CanWrite || dn.PropertyType != typeof(LocalizedString)) continue;
                if (!DropdownKit.ElementLooksLikeAnItem(dn.Name, dn.PropertyType.Name)) continue;

                mi.List = list;
                mi.DisplayName = dn;
                mi.Name = t.Name + "." + propName;
                return mi;
            }
            return null;
        }
    }

    // ===== 运行时译文表：以「源文本」为键（天然去重、跨 key 复用、I18N 改值会重翻新值）。=====
    internal static class TransMap
    {
        private static readonly ConcurrentDictionary<string, string> Translated = new ConcurrentDictionary<string, string>();
        private static readonly ConcurrentDictionary<string, byte> Resolved = new ConcurrentDictionary<string, byte>(); // 已知无需翻译
        private static readonly ConcurrentDictionary<string, byte> Queued = new ConcurrentDictionary<string, byte>();   // 入队去重
        // 反馈5：两条队列，不是一条。进大城市存档时路名/区名动辄几千条，全和玩家刚点开的那块面板挤在同一条
        // FIFO 上，于是「点得快的话要好久才会翻译好」——面板那几条文字排在整座城市名字的后头。
        // 现在界面文字走前台，工作线程【只有前台空了】才去捞世界文字；两条队列共用同一个在飞计数。
        // v1.1.4 改了一句：退避【不再共用】—— 世界洪水撞出来的限流只冻世界那一档，
        // 前台照常走（判据见 Mod.TranslateLoop 那条链上的 Tier / PushCooldown / WaitForCooldown）。
        private static readonly BlockingCollection<string> Pending = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private static readonly BlockingCollection<string> PendingWorld = new BlockingCollection<string>(new ConcurrentQueue<string>());

        // ===== v1.2.5 修复：板块级请求单元（玩家原话见 BatchKit.ChunkUnit 顶上那段）=====
        // 为什么另开一张表，而不是"把几条塞进 Pending 就当一批"：Pending 里躺的是【单条】文字，
        // 取货方（TakeFrom）一次只摘一条，5 条线程各摘一条就各发一次请求 —— 这正是玩家看到的
        // "几乎每个设置项一个请求"。请求的【单位】必须比"一条"大，所以这里存的是【整块】：
        // 扫描器把一个板块收齐后投进 Units，Take 先看这张表，一次取走整块。
        // ⚠ 单元里的每一条仍然各自进 Queued（去重/在飞/译文表那几本账一条都不少），
        //   只是"一起出发"—— 所以翻好了、失败了、被清代了，走的还是原来那几条路径。
        private static readonly BlockingCollection<string[]> Units =
            new BlockingCollection<string[]>(new ConcurrentQueue<string[]>());

        // v1.2.5 修复：暂存。选项界面的文字（key 形如 Options.*）先不直接入队，等扫描器来认领：
        // 扫描器那一刻正走在同一棵树上，手里有板块标题、选项行、候选项，能凑成真正的"一个板块一次请求"。
        // 认领不到（玩家没打开选项面板、那一页没被扫到）⇒ 按 StageTtlMs 到期回落成单条，绝不丢。
        private struct StageEntry
        {
            public Scope.Category Scope;
            public string Key;
            public int At;
        }
        public const int StageTtlMs = 2500;    // 2 秒多一点：比一次扫描间隔(1.2s)长，又短到玩家察觉不到那点延迟
        private static readonly ConcurrentDictionary<string, StageEntry> Staged =
            new ConcurrentDictionary<string, StageEntry>(StringComparer.Ordinal);

        // 扫描器收一个板块期间用的收集器（只在扫描线程上活着，所以是 ThreadStatic）。
        // _collectBudget 是这一轮还剩几个入队名额（沿用扫描预算 ScanEnqueueBudget，别让一页把额度吃光）。
        [ThreadStatic] private static bool _collecting;
        [ThreadStatic] private static List<string> _collected;
        [ThreadStatic] private static Dictionary<string, Scope.Category> _collectedScopes;
        [ThreadStatic] private static HashSet<string> _collectedSeen;
        [ThreadStatic] private static int _collectBudget;

        // v1.2.4 需求3：攒批时「这一条不属于这一批」的退回，退到【队头】而不是队尾。
        // 为什么另开一张表而不是往 Pending 里塞：BlockingCollection 只有 Add（排到尾部），
        // 没有插队 API，硬插要自己实现 IProducerConsumerCollection —— 那是比本轮大得多的动作。
        // 这一张表就是那个"插队"的最小形状：TakeFrom 在【阻塞之前和之后】都先看它一眼，
        // 所以被退回的那条一定比后面排着的文字先被再次取走 ⇒ 玩家看到的顺序还是从上往下。
        // 与两条正式队列一一对应（前台/世界各一张），退回时投哪张与 Requeue 用的是同一个判据。
        // ⚠ 这里的东西【不占】Queued 之外的任何台账：一条文字从 Take 到 ReturnToHead 期间
        //   Queued 里一直有它 ⇒ 扫描器再 Enqueue 同一个 src 仍是空操作，不会出现两份。
        private static readonly ConcurrentQueue<string> HeadFront = new ConcurrentQueue<string>();
        private static readonly ConcurrentQueue<string> HeadWorld = new ConcurrentQueue<string>();

        // v1.2.0 需求5：每条入队文字记住自己的【翻译范围】类目，Take 时原样带出来给提示词用。
        // 为什么必须记：同一条源文本可能从多处入队（选项页一行说明、世界里的路名、资产描述），
        // 提示词要按范围给不同语境（设置界面强调准确、资产描述强调自然），而 Loop 拿到的只有字符串。
        // ⚠ 这张表是「入队那一刻的类目」快照，不是实时的：玩家改范围开关不会改已经排着的条目，
        // 这不影响正确性 —— 范围闸在入队前就判过（ScopeEnabled），能进来的都曾属于勾选中的类目；
        // 提示词只决定【怎么说】，不决定【翻不翻】。
        // ⚠ 与 Queued 同生命周期：Queued.TryAdd 成功才写这里（重复入队不改写首次的类目，
        // 与"同一条文字只翻一次"的缓存语义一致 —— 缓存也是按源文本共享的，见需求5 用户口径）。
        private static readonly ConcurrentDictionary<string, Scope.Category> Scopes =
            new ConcurrentDictionary<string, Scope.Category>();

        // 反馈7：「在飞」计数。Take() 已经把这条从队列里摘走了，请求却还要跑 0.1~8 秒，
        // 所以光看 Pending.Count==0 会在「最后一条还堵在网上」时误判排空，把首批刷新提前发出去。
        // 不变式：Take 取走一条 → +1；这条最终走到 SetTranslation / ResolveAsSkip / Requeue 之一 → −1。
        private static int _inFlight;

        // 在飞计数只减到 0 为止（v1.1.5）。为什么不能直接用 Interlocked.Decrement：
        // TransMap.Reset()（保存设置 / 清缓存 / 重新翻译都会走它）把这一格清零，而【清零那一刻已经出发】的
        // 请求回来时照样要还这一笔 ⇒ 旧写法会把计数压成负数，而 IsIdle 判的是 `== 0` ⇒
        // 从那一刻起本局再也不会出现"真的排空了"，世界文字那套"等队列空了再刷"的判据跟着一起失效。
        // 取舍：宁可少算（早刷一次界面，只是多一次 cohtml 回调），也不能算成负数（永远等不到排空，有害）。
        private static void DecrementInFlight()
        {
            for (int cur = Volatile.Read(ref _inFlight); cur > 0; cur = Volatile.Read(ref _inFlight))
                if (Interlocked.CompareExchange(ref _inFlight, cur - 1, cur) == cur) return;
        }

        // ===== v1.1.5：清代之后回来的那条结果 =====
        // 只做一件事：把在飞计数还给账本，然后【什么都不写】—— 不写 Translated、不写磁盘缓存
        //（那正是"清了又长回来"的那只手）、不退回队列（清代时 WorldSources 已被清空，Requeue 会把
        //  路名错投进前台那一队，把 v1.1.4 好不容易分开的两档又搅在一起），也不碰 Transient/Attempts
        //  那两本账（这条文本在新代里若还想要，扫描/Prefix 会自己重新 Enqueue）。
        public static void DropStale(string src)
        {
            Interlocked.Increment(ref _staleDropped);
            DecrementInFlight();
        }

        // 本局丢弃了多少条"出发在清代之前"的结果 —— 诊断行用它回答"缓存到底清干净了没有"。
        public static int StaleDropped => Volatile.Read(ref _staleDropped);

        private static int _staleDropped;

        // 反馈7：单条文本的失败次数与「本局判死」标记。
        // 过去失败只走全局退避 + 无限重排队：一条永久翻不成的文本（下架的模型、坏掉的占位符）
        // 会把整条队列拖在 30 秒一次的退避里，队列永远排不空，界面就一直不刷新 —— 正是玩家说的
        // 「不可能进入存档后等半个小时才刷新出来吧」。判死后本局保留原文，改设置或按重新翻译时 Reset 复活。
        public const int MaxAttempts = 3;
        private static readonly ConcurrentDictionary<string, int> Attempts = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, byte> GivenUp = new ConcurrentDictionary<string, byte>();

        public static bool TryGetTranslated(string src, out string tr) => Translated.TryGetValue(src, out tr);

        // ===== v1.1.7 性能：「原文 + …」那半句待翻标记 =====
        // Prefix 是【每一次界面取词】都要跑的钩子，而"给还没翻好的文字末尾挂个…"这一步每次都得新拼一个串
        //（string + string ⇒ 一个 StringBuilder + 一个新 string）。玩家点开选项页会触发整屏重译，
        // 一屏 100 条未翻文字就是 100 个新串；在选项页里来回走动时这件事反复发生，而同一条文字在翻好之前
        // 每次取词都要再拼一遍。拼出来的内容对同一个 src 是恒定的（PendingMarker 是编译期常量 "…"），
        // 所以照 Scope.ClassifyMemo 同一套路记进一张【有上限】的小表：命中即零分配，写满就停止写入
        //（照常拼串，只是不进表），不让它无界增长。
        // 强引用代价可控：最多 4096 个「原文+1 字符」的串，而这些原文本身已经作为 Translated/Resolved 的键活着；
        // TransMap.Reset()（保存设置/清缓存/重新翻译）连这张表一起清，不会跨局留住没人再看的文字。
        // ⚠ 这张表【不在纯文件里】（TransMap 住在 Mod.cs，与两条队列/在飞计数同生共死），所以它进不了 T3：
        //   它的正确性靠"内容是 src 与常量的纯拼接"这一条结构保证，而不是靠断言 —— 加断言得先把整类搬出去，
        //   那是比本轮更大的动作，本轮按 S3 的原始判据（约 8 行、纯缓存、行为不变）收口。
        private const int kMaxMarked = 4096;
        private static readonly ConcurrentDictionary<string, string> Marked = new ConcurrentDictionary<string, string>();

        public static string WithPendingMarker(string src, string marker)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(marker)) return src;
            string marked;
            if (Marked.TryGetValue(src, out marked)) return marked;
            marked = src + marker;
            if (Marked.Count < kMaxMarked) Marked.TryAdd(src, marked);
            return marked;
        }

        public static bool IsResolved(string src) => Resolved.ContainsKey(src) || Translated.ContainsKey(src) || GivenUp.ContainsKey(src);

        // 队列空且没有在飞的请求 = 真的闲下来了（反馈6 的首批刷新判据）。两条队列都算。
        // v1.2.4：两张「退回队头」的小表也算活 —— 那里躺着的是已经取走又退回的文字，
        // 漏算的话会出现"队列说空了、其实还有一条等着被翻"，界面就少刷一次。
        // v1.2.5 修复：还没被认领的暂存文字也算活（它们在等扫描器凑板块，凑不到就到期回落）。
        // 漏算的话会出现"队列说空了、其实还有几条等着发"，界面少刷一次、玩家以为没在翻。
        public static bool IsIdle => Pending.Count == 0 && PendingWorld.Count == 0
                                     && HeadFront.IsEmpty && HeadWorld.IsEmpty
                                     && Units.Count == 0 && Staged.IsEmpty
                                     && Volatile.Read(ref _inFlight) == 0;

        // v1.1.4：前台（界面文字）这一队有没有活。冷却分档靠它判 —— 世界文字那场洪水造成的限流
        // 不该把玩家眼前那一屏一起冻住（用户 2026-10-02 的原话：只有在打开新界面时才判断送翻，
        // 大量文本是背景活，不能让背景活停住前景活）。
        // v1.2.4：队头那张表一并算进来（同 IsIdle 的理由）。
        // v1.2.5 修复：手上的单元 / 暂存里等认领的板块文字同样是"眼前这一屏"的活 ——
        // 冷却分档靠这一格判，漏算就会让世界文字那场洪水在玩家正看着选项页时先跑。
        public static bool HasFrontWork => Pending.Count > 0 || !HeadFront.IsEmpty || Units.Count > 0;

        // v1.2.5 需求2：这条文字所在的那一队还有多少条在等（正式队列 + 被退回队头的那张表）。
        // 攒批的「够 12 条就让给别人」判据用它（BatchKit.ShouldLeaveForOthers）：
        // 有积压 ⇒ 让另外几条 worker 也能拿到活，首屏不必等一整批 40 条生成完。
        // 两个 Count 都是队列元数据（ConcurrentQueue.Count 只数段头尾的计数），每次攒批最多读 40 次。
        public static int BacklogSameTier(string src)
        {
            BlockingCollection<string> q = QueueFor(src);
            return q.Count + PushbackFor(q).Count;
        }

        // 这条源文本是不是【世界文字】（路名/区名/贴地文字）—— 与 QueueFor 用的是同一本台账，
        // 所以"走哪条队列"和"该等哪一档冷却"永远是同一个答案。
        public static bool IsWorldSource(string src) => !string.IsNullOrEmpty(src) && WorldSources.ContainsKey(src);

        // 走哪条队列由需求3 那本 WorldSources 台账决定 —— WorldLabelPostfix 在 Enqueue 的【前一行的】
        // MarkWorldSource(src) 刚登记过，所以入队时判据已经就绪，不必多传一个参数。
        private static BlockingCollection<string> QueueFor(string src)
            => WorldSources.ContainsKey(src) ? PendingWorld : Pending;

        // v1.2.5 修复：返回值改成"这一条有没有真的进队"。为什么现在需要它：
        // 扫描器收板块时，"进了板块"与"早就在队里 / 已经翻好 / 额度用光"是两回事，
        // 日志与 Tally 里的入队条数要按真值记账，不能再一律 +1。
        public static bool Enqueue(string src)
        {
            Scope.Category scope = _pendingScope;              // 入队方在调 Enqueue【之前】钉的类目；没钉就是 GameCore
            _pendingScope = Scope.Category.GameCore;
            return EnqueueWithScope(src, scope);
        }

        // v1.2.5 修复：暂存一条选项界面文字，等扫描器来认领（见 StageEntry 那段）。
        // 早退条件与 Enqueue 逐字相同 —— 暂存不是"绕过判据的捷径"，只是"晚一步投递"。
        public static void Stage(string src, string key)
        {
            Scope.Category scope = _pendingScope;
            _pendingScope = Scope.Category.GameCore;
            if (string.IsNullOrEmpty(src)) return;
            if (Translated.ContainsKey(src) || Resolved.ContainsKey(src) || GivenUp.ContainsKey(src)) return;
            if (Queued.ContainsKey(src)) return;               // 已经在队里/单元里：别在暂存里再躺一份
            Staged.TryAdd(src, new StageEntry { Scope = scope, Key = key, At = Environment.TickCount });
        }

        // 暂存到期回落：扫描器没来认领的那些按普通单条入队（就是 v1.2.5 之前的路径，只是晚 ≤2.5 秒）。
        public static void FlushStaged(int maxAgeMs)
        {
            if (Staged.IsEmpty) return;
            int now = Environment.TickCount;
            foreach (KeyValuePair<string, StageEntry> kv in Staged)
            {
                if (unchecked(now - kv.Value.At) < maxAgeMs) continue;
                StageEntry entry;
                if (!Staged.TryRemove(kv.Key, out entry)) continue;   // 已被扫描器认领
                EnqueueWithScope(kv.Key, entry.Scope);
            }
        }

        // ===== 扫描器侧：一个板块收一次 =====
        // BeginSection/EndSection 之间，本线程上的 Enqueue 只进收集器（不入队、不占在飞），
        // 这样"同一个板块的文字"才能凑成一块再投递；预算用完就只收不投（超出部分回落成单条）。
        public static bool BeginSection(int budget)
        {
            if (_collecting) return false;                     // 嵌套（不该发生）：外层说了算
            _collecting = true;
            if (_collected == null) _collected = new List<string>();
            if (_collectedScopes == null) _collectedScopes = new Dictionary<string, Scope.Category>(StringComparer.Ordinal);
            if (_collectedSeen == null) _collectedSeen = new HashSet<string>(StringComparer.Ordinal);
            _collected.Clear();
            _collectedScopes.Clear();
            _collectedSeen.Clear();
            _collectBudget = budget < 0 ? 0 : budget;
            return true;
        }

        // 把暂存里属于【这一页】的板块标题收进当前收集器（按入队时刻升序 ⇒ 与界面上从上到下一致）。
        // ⚠ 故意不拼 `Options.GROUP[<页>.<组>]` 这种精确 key：组序号有两列（Option.simpleGroupIndex /
        //   advancedGroupIndex），用哪一列要看 OptionsUISystem 的私有字段 m_IsAdvanced —— 拼错就是凭空
        //   查一条不存在的 key。这里只认"key 里含这一页 id"，取不到就一条不加（自校验，绝不猜）。
        public static int ClaimHeadings(string pageId)
        {
            if (!_collecting || string.IsNullOrEmpty(pageId) || Staged.IsEmpty) return 0;
            var picked = new List<KeyValuePair<int, string>>();
            foreach (KeyValuePair<string, StageEntry> kv in Staged)
            {
                string key = kv.Value.Key;
                if (key == null || !key.StartsWith("Options.GROUP[", StringComparison.Ordinal)) continue;
                if (key.IndexOf(pageId, StringComparison.Ordinal) < 0) continue;
                picked.Add(new KeyValuePair<int, string>(kv.Value.At, kv.Key));
            }
            if (picked.Count == 0) return 0;
            picked.Sort((a, b) => a.Key.CompareTo(b.Key));
            int n = 0;
            for (int i = 0; i < picked.Count; i++)
            {
                string text = picked[i].Value;
                StageEntry entry;
                if (!Staged.TryRemove(text, out entry)) continue;
                if (EnqueueWithScope(text, entry.Scope)) n++;
            }
            return n;
        }

        // 收尾：把收集到的文字按显示顺序切块（BatchKit.ChunkUnit，纯函数，B 段钉着）。
        // ≥2 条 ⇒ 一块，投进 Units（一次请求）；1 条 ⇒ 走原来的取货路径（不记在飞，等 TakeFrom 再记）。
        // 返回值是"真正进了队列的条数"，供扫描日志记账。
        public static int EndSection()
        {
            if (!_collecting) return 0;
            _collecting = false;
            List<string> list = _collected;
            Dictionary<string, Scope.Category> scopes = _collectedScopes;   // 投递时还要用：范围快照得跟着文字进 Scopes
            _collected = null;
            _collectedScopes = null;
            _collectedSeen = null;
            _collectBudget = 0;
            if (list == null || list.Count == 0) return 0;

            var units = new List<string[]>();
            var singles = new List<string>();
            BatchKit.ChunkUnit(list, units, singles);
            int kept = 0;
            for (int u = 0; u < units.Count; u++)
            {
                string[] chunk = units[u];
                var live = new List<string>();
                for (int i = 0; i < chunk.Length; i++)
                {
                    if (Admit(chunk[i], scopes)) live.Add(chunk[i]);
                }
                kept += live.Count;
                if (live.Count >= 2) { try { Units.Add(live.ToArray()); } catch { /* 已 CompleteAdding */ } }
                else if (live.Count == 1) AdmitSingle(live[0]);
            }
            for (int i = 0; i < singles.Count; i++)
            {
                if (Admit(singles[i], scopes)) { AdmitSingle(singles[i]); kept++; }
            }
            return kept;
        }

        // 收集器里这一条还能不能真的进队：早就在队里（上一轮排着的/别处入的）就跳过 —— 绝不重复投递。
        // 顺带把范围快照写进 Scopes（Take 之后 Loop 要用它选提示词语境）。
        private static bool Admit(string src, Dictionary<string, Scope.Category> scopes)
        {
            if (string.IsNullOrEmpty(src)) return false;
            if (Translated.ContainsKey(src) || Resolved.ContainsKey(src) || GivenUp.ContainsKey(src)) return false;
            if (!Queued.TryAdd(src, 0)) return false;
            Scope.Category c;
            if (scopes != null && scopes.TryGetValue(src, out c)) Scopes[src] = c;
            Staged.TryRemove(src, out _);
            return true;
        }

        // 单条投递：走 v1.2.5 之前的取货路径。
        // 投进【队头那张表】而不是队列尾：扫描是自上而下走的，这几条就是玩家眼前这一屏，
        // 排在整屏之后等于让眼前那几行等完整屏（v1.2.4 需求3 的同一条理由）。
        // ⚠ 不记在飞：它还没被 TakeFrom 取走，等真取走那一步再 +1（一笔借还，绝不双计）。
        private static void AdmitSingle(string src)
        {
            PushbackFor(QueueFor(src)).Enqueue(src);
        }

        private static bool EnqueueWithScope(string src, Scope.Category scope)
        {
            if (string.IsNullOrEmpty(src)) return false;
            if (Translated.ContainsKey(src) || Resolved.ContainsKey(src) || GivenUp.ContainsKey(src)) return false;
            if (_collecting)
            {
                // 收板块期间：只进收集器，不入队、不占在飞 —— 等 EndSection 一次投递。
                if (_collected == null || _collectBudget <= 0) return false;
                if (!_collectedSeen.Add(src)) return false;      // 同一板块里同一句话出现两次：只收一次
                _collected.Add(src);
                _collectedScopes[src] = scope;
                _collectBudget--;
                return true;
            }
            if (!Queued.TryAdd(src, 0)) return false;
            Scopes[src] = scope;
            try { QueueFor(src).Add(src); } catch { /* 已 CompleteAdding */ }
            return true;
        }

        // 入队前由调用方钉下「这条属于哪个翻译范围」（v1.2.0 需求5）。字段而不是参数：
        // Enqueue 有四个调用点、其中两个在闭包里，逐个传参要把 Scope 类目拖进每一层签名；
        // 而 Enqueue 自己会把这一格读掉并复位 ⇒ 两条 worker 线程交替入队也不会串味
        // （钉值与消费之间没有任何 await / 让出，同线程内紧接着执行）。
        // ⚠ 必须【紧贴】 Enqueue 调用之前赋值，中间不要插可能入队的调用。
        [ThreadStatic] private static Scope.Category _pendingScope;

        public static void NoteScope(string src, Scope.Category cat)
        {
            if (!string.IsNullOrEmpty(src)) _pendingScope = cat;
        }

        // 这条文字入队时属于哪个范围。查不到（老盘/预热入队的条目）按 GameCore。
        public static Scope.Category ScopeOf(string src)
        {
            Scope.Category c;
            return (!string.IsNullOrEmpty(src) && Scopes.TryGetValue(src, out c)) ? c : Scope.Category.GameCore;
        }

        // v1.2.5 修复：返回【请求单元】而不是一条。为什么改签名而不是另开一条取货函数：
        // 单条那一路要的东西一个不少（src / 范围 / 代际 / 世界档），只是条数变了；
        // 两种投递共用一个出口，才不会出现"单元那条路忘了还账"的半套实现。
        // 取货顺序：手上的单元 → 前台队列 → 世界队列（单元是"玩家眼前这一屏的一个板块"，必须先走）。
        // 在飞计数：单元一次记 N 笔（逐条借还，IsIdle 的不变式不变）。
        public static string[] Take(int msTimeout)
        {
            string[] unit;
            while (Units.TryTake(out unit))
            {
                if (unit == null || unit.Length == 0) continue;    // 防御：不该出现
                Interlocked.Add(ref _inFlight, unit.Length);
                return unit;
            }
            string s = TakeFrom(Pending, msTimeout);
            if (s == null) s = TakeFrom(PendingWorld, 0);
            return s == null ? null : new[] { s };
        }

        // 攒一批时继续捞用：只从【同一条队列】拿。混着攒的话，一条面板文字会顺手拽进 11 条路名，
        // 这一屏就得等整批生成完 —— 优先级就白分了。
        public static string TakeSameTier(string src, int msTimeout) => TakeFrom(QueueFor(src), msTimeout);

        // 攒批时不合群的条目退回队头（v1.2.4 需求3："按顺序翻译下来视觉上比较舒服"）。
        // 计数与 Requeue 同一套：取走时 +1（TakeFrom），退回时 −1（这里），下次再被取走又 +1 ——
        // 一笔借还，绝不双计（双计会让 IsIdle 永远说不，界面等不到"真的排空了"）。
        public static void ReturnToHead(string src)
        {
            if (string.IsNullOrEmpty(src)) return;
            DecrementInFlight();
            PushbackFor(QueueFor(src)).Enqueue(src);
        }

        // 退回表与正式队列一一对应：判据与 QueueFor 同一条（路名回世界那张、界面文字回前台那张）。
        private static ConcurrentQueue<string> PushbackFor(BlockingCollection<string> q)
            => ReferenceEquals(q, PendingWorld) ? HeadWorld : HeadFront;

        private static string TakeFrom(BlockingCollection<string> q, int msTimeout)
        {
            try
            {
                // 队头那张表优先：那里躺着的是刚被攒批退回的文字，它们必须比后面排着的先被再取一次。
                // 先看一眼再阻塞：阻塞期间新退回的东西不会唤醒这一枪，但下一枪（超时后重来）就拿到了，
                // 最坏多等一个超时，不会丢、也不会饿死。
                string head;
                if (PushbackFor(q).TryDequeue(out head))
                {
                    Interlocked.Increment(ref _inFlight);
                    return head;
                }
                if (!q.TryTake(out string s, msTimeout)) return null;
                Interlocked.Increment(ref _inFlight);
                return s;
            }
            catch { return null; }
        }

        // 反馈7：这条又失败了一次。未到上限退回队列（返回值就是第几次失败，调用方据此决定退避多久）；
        // 到上限就判死：不再入队、界面保留原文，返回 0，调用方应把全局退避清零 —— 别让一条坏文本
        // 把后面所有正常文本一起拖慢。maxAttempts 由调用方给：端点像是已经死了时压到 1，逐条只试一次就放过。
        // v1.1.7：判死那行日志从这里挪到了 Mod.LogGivenUp（要节流）—— 原来的行首是「[重试]」，
        // 一次坏配置会把整屏文字各判死一次，不节流就是几百行，把真正有用的那几行埋掉。
        public static int NoteFailure(string src, int maxAttempts = MaxAttempts)
        {
            int n = Attempts.AddOrUpdate(src, 1, (_, v) => v + 1);
            if (n < maxAttempts) { Requeue(src); return n; }
            GivenUp[src] = 0;
            Attempts.TryRemove(src, out _);
            Queued.TryRemove(src, out _);
            DecrementInFlight();
            return 0;
        }

        // v1.1.2：「这条文本自己翻不成」与「这台机器现在出不去」必须分开记账 —— 这是 ask「下拉框有
        // 6、7 个选项只翻了一半，而且永远不会自己补齐」的真凶。旧写法里 429/连接失败也走 NoteFailure，
        // 于是【一次限流风暴就能把一整屏文本各烧掉一次机会】：三条 worker 撞同一堵墙 ⇒ 每条 3 次用完 ⇒ 判死，
        // 之后不保存、不重翻就再也不试了（2026-10-01 15:08:36 那三条 429 就发生在这条路径上）。
        // 所以这里给"外部条件造成的失败"另开一条路：原样退回队列，【不碰 Attempts】，只数"被外部条件弹回几次"，
        // 超过 KMaxTransient 才降级成普通失败（免得一条真翻不成的文本因为一直撞上全局故障而无限循环）。
        // 返回值 = 这是第几次被弹回（0 = 已降级，调用方按普通失败处理）。
        private const int KMaxTransient = 12;
        private static readonly ConcurrentDictionary<string, int> Transient = new ConcurrentDictionary<string, int>();

        public static int NoteTransient(string src)
        {
            if (string.IsNullOrEmpty(src)) return 0;
            int n = Transient.AddOrUpdate(src, 1, (_, v) => v + 1);
            if (n <= KMaxTransient) { Requeue(src); return n; }
            Transient.TryRemove(src, out _);
            return 0;
        }

        // 成功过一次（或玩家改了设置）就把这本"弹回账"清干净：外部条件恢复后，这些文本应当重新享有完整预算。
        public static void ForgetTransient(string src)
        {
            if (!string.IsNullOrEmpty(src)) Transient.TryRemove(src, out _);
        }

        public static void ClearTransient() { Transient.Clear(); }

        // 有多少条文本正在被外部条件弹回（诊断行用它区分"还在排队"与"排队排队却因为出口不通原地打转"）。
        public static int TransientCount => Transient.Count;

        public static void Requeue(string src)
        {
            if (string.IsNullOrEmpty(src)) return;
            DecrementInFlight();
            try { QueueFor(src).Add(src); } catch { }     // 回哪条队列与入队时同一个判据，别把路名塞回前台
        }

        public static void SetTranslation(string src, string tr)
        {
            Translated[src] = tr;
            Queued.TryRemove(src, out _);
            Resolved.TryRemove(src, out _);
            GivenUp.TryRemove(src, out _);
            Attempts.TryRemove(src, out _);
            Transient.TryRemove(src, out _);      // 成功过一次 ⇒ 外部条件的弹回账清零（v1.1.2）
            DecrementInFlight();
            CountIfWorld(src);
        }

        // v1.1.6：预载专用。与 SetTranslation/ResolveAsSkip 同写内存表，但【不碰在飞计数】
        // —— 预载的条目从来不是 Take() 出来的，没有「在飞」这一笔可还。旧写法直接复用
        // SetTranslation，靠 DecrementInFlight 的钳制碰巧抵消；显式分开后，种子缓存与
        // 在飞计数的耦合就没了（将来若把钳制改回 Interlocked.Decrement 也不会崩）。
        public static void SeedTranslation(string src, string tr)
        {
            Translated[src] = tr;
            Queued.TryRemove(src, out _);
            Resolved.TryRemove(src, out _);
            GivenUp.TryRemove(src, out _);
            Attempts.TryRemove(src, out _);
            Transient.TryRemove(src, out _);
            CountIfWorld(src);
        }

        public static void SeedSkip(string src)
        {
            Resolved[src] = 0;
            Queued.TryRemove(src, out _);
            Translated.TryRemove(src, out _);
            GivenUp.TryRemove(src, out _);
            Attempts.TryRemove(src, out _);
            Transient.TryRemove(src, out _);
        }

        // ===== 需求3：只属于「游戏画面图层」的新译文信号 =====
        // 为什么要单独统计：刷新图层要清渲染缓存、甚至重读语言字典，都不便宜。
        // 若按「任何译文落地」触发，翻菜单时也会每 10 秒白刷一次路面文字。所以由 WorldLabelPostfix 先登记
        // 哪些源文本属于图层，落地时只对这部分计数。
        private static readonly ConcurrentDictionary<string, byte> WorldSources = new ConcurrentDictionary<string, byte>();
        private static int _worldNew;

        public static void MarkWorldSource(string src)
        {
            if (!string.IsNullOrEmpty(src)) WorldSources[src] = 0;
        }

        private static void CountIfWorld(string src)
        {
            if (src != null && WorldSources.ContainsKey(src)) Interlocked.Increment(ref _worldNew);
        }

        public static int PeekWorldNew() => _worldNew;

        public static void ClearWorldNew() { Interlocked.Exchange(ref _worldNew, 0); }

        public static void ResolveAsSkip(string src)
        {
            Resolved[src] = 0;
            Queued.TryRemove(src, out _);
            Translated.TryRemove(src, out _);
            GivenUp.TryRemove(src, out _);
            Attempts.TryRemove(src, out _);
            Transient.TryRemove(src, out _);
            DecrementInFlight();
        }

        // 目标语言/引擎变更后清空内存译文，按新设置重新解析（磁盘缓存命中即时恢复）。
        public static void Reset()
        {
            Translated.Clear();
            Resolved.Clear();
            Queued.Clear();
            Scopes.Clear();             // v1.2.0 需求5：范围伴随表与 Queued 同生命周期，一起清（与 Attempts/GivenUp 同理）
            WorldSources.Clear();
            Attempts.Clear();
            // 反馈7：判死是「本局」的决定，不能跨设置变更留着 —— 玩家换引擎/换语言/按重新翻译就是想再试一次。
            GivenUp.Clear();
            Transient.Clear();          // v1.1.2：外部条件（限流/出口不通）的弹回账同理，换引擎/修好网络后重新起算
            Marked.Clear();             // v1.1.7：清缓存/换语言/重新翻译那一刻，别的表都不再引用这些原文了，
                                        // 这张「原文 → 原文+…」的拼接表要是留着，就等于替游戏养着最多 4096 条
                                        // 没人再看的文字。清掉不影响正确性（内容是纯拼接，重建一次就行）。
            Interlocked.Exchange(ref _inFlight, 0);
            ClearWorldNew();
            while (Pending.TryTake(out _)) { }
            while (PendingWorld.TryTake(out _)) { }
            // v1.2.4 需求3：退到队头的那些（不合批/不合范围被塞回来的）也必须一起倒干净 ——
            // 它们不在 Pending 里，漏了这一格的话「清除缓存」之后它们会带着旧代际重新冒出来，
            // 而且 _inFlight 已被归零、它们却还挂在队头 ⇒ 下一个 Take 会再记一次在飞，
            // 等于凭空多出一条"没人认领"的活。两行清空是这条不变式的必要条件。
            while (HeadFront.TryDequeue(out _)) { }
            while (HeadWorld.TryDequeue(out _)) { }
            // v1.2.5 修复：手上的单元与暂存里的选项文字同理 —— 它们不在那两条队列里，
            // 漏了这一格，清完缓存它们会带着旧代际冒出来（而且 _inFlight 已归零 ⇒ 凭空多出一笔没人认领的活）。
            while (Units.TryTake(out _)) { }
            Staged.Clear();
            _collecting = false;
            _collected = null;
            _collectedScopes = null;
            _collectedSeen = null;
            _collectBudget = 0;
        }
    }

    // ===== 需求3/4：游戏世界渲染文本（路名/区名/其它模组贴地文字）的刷新 =====
    // 症结在于这些文字【不是每帧查字典的】，游戏把它们缓存/烤住了。
    // Game.dll 元数据实测：缓存分成【三份】，各自挂在一个渲染系统上，字段名都叫 m_CachedLabels，
    // 类型都是 Dictionary<Entity, string>（所以 as IDictionary 本来就有效，不是类型转换问题）：
    //   · OverlayRenderSystem  → 悬停标签、贴地覆盖文字
    //   · AggregateMeshSystem  → 路名（连同 m_LabelData 一起烤进合并网格）
    //   · AreaBufferSystem     → 区名（区域标签顶点缓冲）
    // v0.31 只清了 OverlayRenderSystem 那一份，而它平时基本是空的 —— 于是每次都在 dict.Count == 0 那一行
    // 【静默返回】，整局游戏日志里一条「已清空标签缓存」都没有。玩家看到的就是「三档刷新方式没区别、
    // 译文翻好了也得鼠标划过去才变」。路名和区名在另外两份里，从来没被碰过。
    // 另外 Game.UI.NameSystem 自身【不缓存字符串】（只有 m_DeletedQuery/m_PrefabSystem/m_PrefabUISystem），
    // 所以清掉这三份之后游戏下一帧就会重新调 GetRenderedLabelName，我们的 Postfix 才有机会交回译文/原文。
    //
    // 三份杠杆，都跑在主线程、都限流、都整段 try/catch：失败最多是「不刷新」，绝不会拖垮或崩掉渲染。
    //   B 清空三份 m_CachedLabels：下一帧游戏按【当前相机视野】把该画的标签/网格重建一遍，重新取名字 ——
    //     既显示已有译文，又把这次新看到的名字入队翻译，等于让游戏自己完成「按视野预热入队」。
    //     这里刻意不自己写 EntityQuery：图层实体的组件组合跨游戏版本会变，猜错一次就是崩存档，
    //     而清缓存能让游戏用它自己的查询和自己的帧预算去做同一件事，稳得多。
    //   C LocalizationManager.ReloadActiveLocale()：公开 API，重读当前语言字典并发全局 onActiveDictionaryChanged，
    //     把烤进网格的路名/区域名重烤一遍。代价也实在：重读整张字典不便宜，而且这个全局事件别的翻译类模组
    //     （如 I18NEverywhere）也在听，可能互相打扰。
    //     反馈4：玩家实测「定时强刷每次都卡一下，体验差」，所以【不再定时做】，退成 D 打不到时的兜底。
    //   D 宿主自己的 OnDictionaryChanged()（Game.dll 元数据实测：AggregateMeshSystem 与 AreaBufferSystem 各有一个，
    //     都是非公开实例方法、无参）：这正是玩家在游戏里切换语言时走的那段代码 —— 直接按「字典变了」重烤
    //     路名/区名的合并网格与区域顶点缓冲，既不重读字典也不向别的模组广播。反馈6 连改三轮都没修好，
    //     就是因为一直只在 B（实测整局清不出东西）和 C（贵、而且未必真会重烤）之间打转。
    internal static class WorldLabels
    {
        private const int NewTextMinMs = 5000;        // 有新译文落地时的常规刷新：两次清缓存至少隔这么久（反馈6：10 秒太松，玩家眼里就是「翻完了没刷」）
        private const int EnterProbeMs = 2000;        // 「是否已进入存档」的轮询间隔（IMod 没有存档载入完成的回调）
        private const int InitialSettleMs = 3000;     // 进存档后至少静置这么久再判断队列是否排空（要给游戏几帧把名字入队）
        // 反馈7：玩家的耐心不超过 10 秒。旧值 180000 意味着只要有一条文本卡住/一直失败，
        // 进存档后三分钟里 ②每帧 return，③也被跳过，画面全程不动 —— 现在最多等 15 秒就先按已有译文刷一次，
        // 剩下的靠「新译文落地」继续刷新。
        private const int InitialMaxWaitMs = 15000;
        // 反馈6 的重做：删掉旧版「首批之后 40 秒盲打一次补刷」的档位。那一档要解决的确实是真问题
        // （烤进合并网格的路名/区名只有重读字典才会重烤），但它的触发条件 PeekWorldNew() > 0 恰好被
        // 首批那一次刷新里的 ClearWorldNew() 消费掉了，于是 40 秒到点时条件不成立、这一局再也不会有第二次
        // 字典重读 —— 玩家实测的「后台翻译完成但文本不刷新」就是这么来的。现在改成 C 档跟着事件走 + 单独限流。
        private const int ReloadMinMs = 20000;        // 重读整张语言字典的最快间隔（贵的那一下，还会惊动别的翻译模组）
        private const string kCacheField = "m_CachedLabels";

        // 三份缓存的宿主系统，一次全清。找不到的那一份只警告一次，其余照常工作。
        private static readonly string[] kCacheOwners =
        {
            "Game.Rendering.OverlayRenderSystem",
            "Game.Rendering.AggregateMeshSystem",
            "Game.Rendering.AreaBufferSystem",
        };

        private static int _lastClearTick;    // 上次清缓存的时刻（常规刷新的 5 秒限流窗口）
        private static int _initialPasses;    // 本局 ② 已刷过几次：只写进日志方便对账「一次进存档刷了几回」
        private static int _lastProbeTick;    // 上次探测「是否已进存档」的时刻
        private static bool _inSave;          // 已经处理过本局的「进入存档」；false = 还在主菜单/加载画面，或翻译刚被关掉
        private static bool _awaitInitial;    // 正在等首批世界文字翻完，翻完（或超时）强刷一次
        private static int _awaitSinceTick;   // 开始等首批的时刻
        private static int _lastReloadTick;   // 上次真正重读语言字典的时刻（0 = 这一局还没读过 ⇒ 第一次必打）
        private static bool _reloadPending;   // 清缓存时字典重读被限流挡下：欠一次重烤，④ 到点补

        // 一个宿主 = (系统类型, m_CachedLabels 字段, 可选的重烤入口)。只在主线程访问，不需要加锁。
        private sealed class Target
        {
            public string Owner;      // 类型全名，只用于日志
            public Type SysType;      // 拿它去 World 要系统实例
            public FieldInfo Field;   // m_CachedLabels
            public MethodInfo Bake;   // OnDictionaryChanged()：语言字典变了就重烤网格文字；OverlayRenderSystem 没有 = null
        }

        private static Target[] _targets;
        private static PropertyInfo _worldProp;
        private static MethodInfo _getSystemMethod;
        private static bool _worldSearched;

        // 每帧由 Patches.EnsureApplied 调用（那边已在主线程，且有退出闸门与 try/catch）。
        // 反馈4：只有一种刷新方式，所以这里不再读任何档位 —— 关掉范围/总开关时第一行就返回，热路径开销可忽略。
        public static void Tick()
        {
            // v1.1.6：待办的强制刷新（「保存设置」/「清除缓存」/「重新翻译」进来时记的账）。
            // 放在退出闸门【之前】：关掉翻译时（Paused=true）也欠着一次「画面回原文」，
            // 那一下不能因为开关关了就被吞掉。
            RunPendingForce();

            if (!Patches.HookEnabled || Patches.Paused || !Patches.ScopeWorldLabels)
            {
                // 关掉期间的进出存档都不算数：复位后玩家重新开启时，会当成「刚进存档」再走一遍首批刷新，
                // 否则在同一个存档里关掉再打开，新看到的路名要等鼠标划过才更新。
                _inSave = false;
                _awaitInitial = false;
                return;
            }

            int now = Environment.TickCount;                             // 会回绕，但 int 相减依然正确

            // ① 进入存档：立刻「清三份缓存 + 重读语言字典」，让游戏按当前视野把还没翻译过的路名/区名全部入队。
            if (now - _lastProbeTick >= EnterProbeMs)
            {
                _lastProbeTick = now;
                bool inSave = IsInSave();
                if (inSave && !_inSave)
                {
                    _inSave = true;
                    _awaitInitial = true;
                    _awaitSinceTick = now;
                    _initialPasses = 0;
                    _reloadPending = false;
                    ModLog.Info("[图层] 已进入存档：重读语言字典并清空世界文字缓存，把视野内还没翻译过的路名/区名全部送去翻译（此处会有一次卡顿）。");
                    RefreshWorld("进入存档");
                    return;                                              // 这一帧到此为止，等游戏下一帧重新取名字
                }
                if (!inSave) _inSave = false;                            // 退回主菜单/正在换存档：下一局重新走一遍
            }

            // ② 首批翻译完成：清缓存 + 重读字典【两下都打】，刚翻好的路名/区名立刻上屏（反馈6 的核心诉求）。
            //    先静置 InitialSettleMs 再判断，是因为清完缓存那一瞬间队列还是空的 —— 游戏要下一帧才会把名字入队，
            //    立刻判「排空」等于白刷一次。卡额度翻不完时最多等 InitialMaxWaitMs，超时也先按已有译文刷一次。
            //    反馈6：刷完之后只要队列还没闲下来就【重新武装】—— 慢引擎的译文会陆续落地，
            //    每排空一次再刷一次，玩家既不用鼠标划过、也不用等固定的几十秒。
            if (_awaitInitial)
            {
                bool drained = TransMap.IsIdle;
                int waited = now - _awaitSinceTick;
                if ((drained && waited >= InitialSettleMs) || waited >= InitialMaxWaitMs)
                {
                    _initialPasses++;
                    TransMap.ClearWorldNew();
                    RefreshWorld(drained
                        ? "世界文字已翻译完成（第 " + _initialPasses + " 次刷新）"
                        : "首批翻译等待超时（" + (InitialMaxWaitMs / 1000) + " 秒），先按已有译文刷一次，剩下的陆续再刷");
                    _awaitInitial = !drained;
                    _awaitSinceTick = now;
                }
                return;                                                  // 等首批期间不叠加常规刷新，免得重复清同一批缓存
            }

            // ③ 常规：之后每有新译文落地就刷一次。旧版这里只打了第 1 下（清缓存），第 2 下推给一个
            //    条件永远不成立的 40 秒补刷档位，所以烤进合并网格的路名/区名从来没被重烤过。
            if (TransMap.PeekWorldNew() > 0 && now - _lastClearTick >= NewTextMinMs)
            {
                TransMap.ClearWorldNew();                                // 先消费信号再清缓存：清失败也不该每帧重试
                RefreshWorld(null);
            }

            // ④ 补账：③ 那一次若正好被 20 秒窗口挡下字典重读，就记了一笔欠账；窗口一到马上补，
            //    否则「最后落地的那一批译文」永远停在原文上 —— 也正是反馈6 说的「翻完了没刷新」。
            if (_reloadPending && now - _lastReloadTick >= ReloadMinMs)
            {
                _reloadPending = false;
                ReloadLocale();
                _lastReloadTick = Environment.TickCount;
            }
        }

        // 一次完整的世界文字刷新 = 三份杠杆按「便宜优先」往下打：
        //   B 清三份 m_CachedLabels → 悬停标签那一路下一帧就会重新问 NameSystem（我们的 Postfix 才有机会交回译文）；
        //   D 逐个宿主调用游戏自己的 OnDictionaryChanged() → 游戏按「语言字典变了」这条既有路径把
        //     路名/区名重烤进合并网格与区域顶点缓冲。它正是切换游戏语言时走的那段代码，所以既不用重读字典、
        //     也不会向全局事件广播（别的翻译模组不会被惊动）。
        //   C 只在 D 一个都没打成时才用（游戏改版改了方法名、或系统还没创建）：ReloadActiveLocale 又贵又会广播。
        private static void RefreshWorld(string reason)
        {
            int now = Environment.TickCount;
            ClearLabelCaches(reason);
            if (BakeLabels() > 0) { _reloadPending = false; }
            else if (now - _lastReloadTick >= ReloadMinMs)
            {
                ReloadLocale();
                _lastReloadTick = now;
                _reloadPending = false;
            }
            else _reloadPending = true;                                  // 欠着的那一贴由 ④ 到点补
            _lastClearTick = now;                                        // 刚刷过，把节流窗口推平，免得下一帧又白刷一次
        }

        // D：让每个宿主自己重烤一遍网格文字，返回打成功几个。逐个 try —— 一个系统改版不该拖垮另外两个。
        private static int BakeLabels()
        {
            int ok = 0;
            foreach (Target t in GetTargets())
            {
                if (t.Bake == null) continue;
                try
                {
                    object sys = GetSystem(t);
                    if (sys == null) continue;                           // 这局还没创建该系统（例如还在主菜单）
                    t.Bake.Invoke(sys, null);
                    ok++;
                }
                catch (Exception ex) { ModLog.Warn("[图层] " + ShortName(t.Owner) + " 重烤失败（改用重读字典兜底）: " + ex.Message); }
            }
            return ok;
        }

        // 「是否已进入存档」的探针：那三个渲染系统只在存档场景里被创建，主菜单/加载画面上要不到实例。
        // 任一存在就算进了存档 —— 三份宿主分别管悬停标签、路名、区名，游戏改版时未必三个同时可用。
        // 每 2 秒才调一次，单次就是向 ECS 世界要一个已有的系统实例，开销可忽略。
        private static bool IsInSave()
        {
            foreach (Target t in GetTargets())
            {
                try { if (GetSystem(t) != null) return true; }
                catch { }
            }
            return false;
        }

        // 需求2：总开关关掉（或点「清除缓存」）之后，界面文字靠 RefreshUi 回原文，但世界里的路名/区名/悬停标签
        // 还卡在上面那三份缓存里 —— 而 Tick() 第一行就被 HookEnabled / Paused / ScopeWorldLabels 挡掉，
        // 永远不会再去清，玩家只能重进存档才看得到原文。所以这里开一条【不看任何开关】的强制通道：
        // 三份缓存全清 + 重读一次语言字典，下一帧游戏重新问 NameSystem，此时 Postfix 放行原文，画面就恢复了。
        // 只由主线程的按钮回调（SaveNow / ClearCacheNow / RetranslateNow）调用。
        // v1.1.6：加 400ms 节流（见 ForceRefresh 内的说明）。
        private const int ForceMinMs = 400;
        private static bool _forcePending;
        private static string _forceReason;
        private static int _lastForceTick;

        public static void ForceRefresh(string reason)
        {
            // v1.1.6：这里被「保存设置」（快捷键 0.5 秒可连按）、「清除缓存」与「重新翻译」
            // 在主线程调用，每一次都清三份渲染缓存 + 重烤 + 可能重读整张语言字典。
            // 连按时那是连续几下重活。改成「先记账、窗口到了就做」：压得够近的两次
            // 合并成一次——【延后，绝不丢】（关掉翻译时玩家指望画面立刻回原文，那一下
            // 不能吞），由 Tick() 在退出闸门【之前】补做。
            _forcePending = true;
            _forceReason = reason;
            RunPendingForce();
        }

        private static void RunPendingForce()
        {
            if (!_forcePending) return;
            int now = Environment.TickCount;
            if (now - _lastForceTick < ForceMinMs) return;   // 压得够近：留给 Tick() 到点做
            _forcePending = false;
            string reason = _forceReason; _forceReason = null;
            _lastForceTick = now;
            RefreshWorld(reason);
            // 上面这两步会让游戏把视野内的世界文字重新入队，等于又产生了一批待翻文本：若此刻确实在存档里，
            // 就重新武装「首批」等待，让 Tick() 在这批翻完后自动再强刷一次（反馈6 的行为对这几个按钮同样成立）。
            if (IsInSave())
            {
                _inSave = true;
                _awaitInitial = true;
                _awaitSinceTick = Environment.TickCount;
                _initialPasses = 0;
            }
        }

        // B：把三份 m_CachedLabels 都清掉，返回一共清了多少条。
        // 只动这一个字段 —— OverlayRenderSystem.m_TextMeshes 是 TMP 网格的池子，清了会漏网格，不碰。
        // 反馈6：日志必须把【每个宿主的当下状态】一起打出来。上一版只说「缓存本来就是空的」，
        // 分不清是系统实例没要到手还是字典真的为空，结果连改三轮都找错地方。
        private static int ClearLabelCaches(string reason)
        {
            int total = 0;
            StringBuilder detail = new StringBuilder();
            StringBuilder status = new StringBuilder();
            foreach (Target t in GetTargets())
            {
                string who = ShortName(t.Owner);
                if (status.Length > 0) status.Append("、");
                try
                {
                    object sys = GetSystem(t);
                    if (sys == null) { status.Append(who).Append(" 未创建"); continue; }   // 例如还没进存档
                    IDictionary dict = t.Field.GetValue(sys) as IDictionary;
                    if (dict == null) { status.Append(who).Append(" 字段取不到"); continue; }
                    int n = dict.Count;
                    status.Append(who).Append(' ').Append(n).Append(" 条");
                    if (n == 0) continue;                                // 空的不计入，免得每次刷新一堆没用的行
                    dict.Clear();
                    total += n;
                    if (detail.Length > 0) detail.Append("、");
                    detail.Append(who).Append(' ').Append(n).Append(" 条");
                }
                catch (Exception ex)
                {
                    status.Append(who).Append(" 异常");
                    ModLog.Warn("[图层] 清 " + who + " 缓存失败（不影响游戏，只是不刷新）: " + ex.Message);
                }
            }

            // 汇总只打一行，且【无论清没清成都打】：这份回显本身就是证据。
            ModLog.Info("[图层] 三份标签缓存现状：" + status + "（共清 " + total + " 条）"
                        + (reason == null ? "，原因：有新译文落地" : "，原因：" + reason)
                        + (total > 0 ? "；下一帧游戏会按当前视野重建并重新取名字。" : "；没有可清的条目，重烤改由宿主的 OnDictionaryChanged 负责。"));
            return total;
        }

        // C：公开 API，重读当前语言字典 → 发 onActiveDictionaryChanged → 路名/区域名重烤进网格。
        private static void ReloadLocale()
        {
            try
            {
                var lm = GameManager.instance?.localizationManager;
                if (lm == null) return;
                lm.ReloadActiveLocale();
                ModLog.Info("[图层] 已重读游戏语言字典，烤进网格的路名/区域名会重新生成。");
            }
            catch (Exception ex) { ModLog.Warn("[图层] ReloadActiveLocale 失败: " + ex.Message); }
        }

        // 下面几个都只在主线程调用，所以缓存字段不需要加锁。
        // 懒解析三个宿主：类型 + m_CachedLabels 字段。找不到的那一个只警告一次，其余照常工作 ——
        // 游戏改版最可能只改其中一个系统，不该让另外两个跟着一起停摆。
        private static Target[] GetTargets()
        {
            if (_targets != null) return _targets;
            List<Target> list = new List<Target>(kCacheOwners.Length);
            foreach (string owner in kCacheOwners)
            {
                string who = ShortName(owner);
                Type t = FindType(owner);
                if (t == null)
                {
                    ModLog.Warn("[图层] 找不到 " + who + "，它管的那部分世界文字不会自动刷新（不影响其它翻译）。");
                    continue;
                }
                FieldInfo f = t.GetField(kCacheField, BindingFlags.Instance | BindingFlags.NonPublic);
                if (f == null)
                {
                    ModLog.Warn("[图层] " + who + " 里没有 " + kCacheField + " 字段，游戏版本可能改了；它管的那部分不会自动刷新。");
                    continue;
                }
                Target item = new Target();
                item.Owner = owner;
                item.SysType = t;
                item.Field = f;
                // 重烤入口是可选的：OverlayRenderSystem 每帧从 m_CachedLabels 现取现画，不需要这个方法。
                item.Bake = t.GetMethod("OnDictionaryChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                list.Add(item);
            }
            _targets = list.ToArray();
            return _targets;
        }

        // 向 ECS 世界要这个系统的实例。还没进存档时系统不存在，返回 null，调用方跳过即可。
        private static object GetSystem(Target t)
        {
            if (!_worldSearched)
            {
                _worldSearched = true;
                Type worldType = typeof(Unity.Entities.World);
                _worldProp = worldType.GetProperty("DefaultGameObjectInjectionWorld", BindingFlags.Public | BindingFlags.Static);
                _getSystemMethod = worldType.GetMethod("GetExistingSystemManaged", BindingFlags.Public | BindingFlags.Instance,
                                                       null, new[] { typeof(Type) }, null);
                if (_worldProp == null || _getSystemMethod == null)
                    ModLog.Warn("[图层] 拿不到 ECS 世界句柄，世界文字刷新自动停用（不影响其它翻译）。");
            }
            if (_worldProp == null || _getSystemMethod == null) return null;
            object world = _worldProp.GetValue(null);
            if (world == null) return null;
            return _getSystemMethod.Invoke(world, new object[] { t.SysType });
        }

        // 按全名找类型（不直接 typeof：万一命名空间变了会连编译都过不去）。
        // 不在这里打警告也不在这里缓存 —— 缓存由 GetTargets 负责，警告要带上具体宿主名才有用。
        private static Type FindType(string fullName)
        {
            Type t = Type.GetType(fullName + ", Game");
            if (t != null) return t;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try { t = asm.GetType(fullName); if (t != null) return t; }
                catch { }
            }
            return null;
        }

        // 日志里用短名：三个宿主都在 Game.Rendering 下，写全名一行放不下也看不出区别。
        private static string ShortName(string fullName)
        {
            int i = fullName.LastIndexOf('.');
            return i < 0 ? fullName : fullName.Substring(i + 1);
        }
    }

    // ===== 本地缓存：同一段文字只翻译一次并永久保存，重启不再消耗额度（回应用户对额度的担忧）。=====
    internal static class TranslationCache
    {
        private static readonly ConcurrentDictionary<string, string> Dict = new ConcurrentDictionary<string, string>();
        private static string _path;

        // v0.28：增量落盘。新译文先进内存表 + 排队，由后台线程攒批【追加】写入，不再由主线程全量重写整个文件。
        // 文件格式沿用「key=value 一行一条」，读取时后出现的行覆盖先出现的（Load 用字典赋值，天然满足），
        // 所以追加是安全的；攒够一批再整表重写压缩一次（Compact），去重并防止文件无限膨胀。
        private static readonly ConcurrentQueue<string> _pending = new ConcurrentQueue<string>();
        private static readonly object _fileLock = new object();
        // 写盘用【无 BOM】的 UTF8：Encoding.UTF8 会在文件头写 BOM，导致第一行 key 变成 「\uFEFFms|...」而永远匹配不上。
        private static readonly UTF8Encoding CacheEncoding = new UTF8Encoding(false);
        private static int _appendedLines;      // 上次整表重写以来追加过的行数（用于决定何时压缩）
        private static int _lastFlushTick;
        private const int CompactAfterAppends = 200;
        private const int FlushBatch = 8;        // 攒够 8 条就追加一次
        private const int FlushMaxIdleMs = 5000; // 最多攒 5 秒，免得最后几条迟迟不落盘

        public static void Load()
        {
            try
            {
                _path = Path.Combine(ModPaths.DataDir, "translation.cache");
                Dict.Clear();
                while (_pending.TryDequeue(out _)) { }
                _appendedLines = 0;
                if (!File.Exists(_path)) return;
                int bad = 0;
                foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    if (ParseLine(line, out string k, out string v)) Dict[NormalizeKey(k)] = v;
                    else if (line.Trim().Length > 0) bad++;
                }
                ModLog.Info($"[缓存] 载入 {Dict.Count} 条历史译文：{_path}" + (bad > 0 ? $"（跳过 {bad} 行无法解析）" : ""));
            }
            catch (Exception ex) { ModLog.Error("[缓存] 载入失败: " + ex.Message); }
        }

        public static string Get(string engine, string src, string dst)
        {
            if (string.IsNullOrEmpty(src)) return null;
            return Dict.TryGetValue(Key(engine, dst, src), out string v) ? FromB64(v) : null;
        }

        public static void Put(string engine, string src, string dst, string trans)
        {
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(trans)) return;
            string k = Key(engine, dst, src);
            string v = ToB64(trans);
            Dict[k] = v;
            _pending.Enqueue(k + "=" + v);   // 排队等后台线程追加写入（见 Flush）
        }

        // 把缓存里「指定引擎 + 指定目标语言」命中的历史译文预载进运行时 TransMap（需求9a）。
        // 返回预载条数。缓存 key 形如 engine|dst|b64(src)，value 是 b64(trans)。
        public static int SeedTransMap(string engine, string dst, int clearStamp)
        {
            if (string.IsNullOrEmpty(engine) || string.IsNullOrEmpty(dst)) return 0;
            string prefix = engine + "|" + dst + "|";
            int n = 0;
            foreach (var kv in Dict)
            {
                // v1.1.6：扫描期间玩家又点了保存/清缓存 ⇒ 这一代的结果已经作废，
                // 立刻收手，调用方按负数识别并丢弃（与 EngineKit.ResultStillWanted 同一判据），
                // 否则会把「旧目标语言」的译文写进新设置的译文表。
                if (clearStamp != ClearStamp) return -1;
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string src = FromB64(kv.Key.Substring(prefix.Length));
                string trans = FromB64(kv.Value);
                if (string.IsNullOrEmpty(src)) continue;
                if (string.IsNullOrEmpty(trans) || trans == src) TransMap.SeedSkip(src);
                else { TransMap.SeedTranslation(src, trans); n++; }
            }
            return n;
        }

        // 整表重写（压缩）：内存表一次性写盘，并清空待追加队列与计数。供 L10n（本模组界面自翻）等
        // 需要「立刻完整落盘」的调用者使用；平时由 Flush 做增量追加。
        public static void Save()
        {
            lock (_fileLock) SaveCore();
        }

        // 增量落盘（v0.28）：由后台翻译循环每轮调用；force=true 用于退出收口时立刻写干净。
        // 平时只在攒够 FlushBatch 条、或距上次 FlushMaxIdleMs 毫秒时才真开一次文件追加，
        // 追加攒够 CompactAfterAppends 行则改做整表重写压缩一次（去重 + 防止缓存文件无限膨胀）。
        public static void Flush(bool force = false)
        {
            if (_pending.IsEmpty)
            {
                if (force && _appendedLines >= CompactAfterAppends) lock (_fileLock) SaveCore();
                return;
            }
            if (!force)
            {
                if (_pending.Count < FlushBatch && Environment.TickCount - _lastFlushTick < FlushMaxIdleMs) return;
            }

            lock (_fileLock)
            {
                if (_appendedLines >= CompactAfterAppends) { SaveCore(); return; }

                var sb = new StringBuilder();
                int drained = 0;
                while (drained < FlushBatch * 8 && _pending.TryDequeue(out string line))
                {
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                    drained++;
                }
                if (drained == 0) return;
                _lastFlushTick = Environment.TickCount;
                if (string.IsNullOrEmpty(_path)) return;
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));
                    File.AppendAllText(_path, sb.ToString() + "\n", CacheEncoding);
                    _appendedLines += drained;
                }
                catch (Exception ex) { ModLog.Error("[缓存] 追加保存失败: " + ex.Message); }
            }
        }

        // 整表重写的实际实现：调用方必须已持有 _fileLock。
        private static void SaveCore()
        {
            try
            {
                if (string.IsNullOrEmpty(_path)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(_path));
                var sb = new StringBuilder();
                foreach (var kv in Dict) sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');
                File.WriteAllText(_path, sb.ToString(), CacheEncoding);
                while (_pending.TryDequeue(out _)) { }
                _appendedLines = 0;
                ModLog.Info($"[缓存] 已保存 {Dict.Count} 条译文。");
            }
            catch (Exception ex) { ModLog.Error("[缓存] 保存失败: " + ex.Message); }
        }

        // 需求10：完全清除翻译缓存（内存 + 磁盘文件），含 "self"（本模组界面机翻）命名空间一并清掉。
        public static void Clear()
        {
            // v1.1.5：第一件事就是抬代际戳 —— 必须在清内存表【之前】，这样"出发时抄的是旧戳"的那些在飞请求
            // 回来时一律判定为旧结果（判据见 EngineKit.ResultStillWanted / Mod.FinishOne）。
            // 晚一步抬的后果是有一条刚好卡在"表已清空、戳还没变"的缝里回写，把刚删的文件又建出来。
            Interlocked.Increment(ref _clearStamp);
            lock (_fileLock)
            {
                Dict.Clear();
                while (_pending.TryDequeue(out _)) { }   // 关键：排空待追加队列，否则 Clear 后旧条目又被后台 Flush 追加回来。
                _appendedLines = 0;
                try
                {
                    string path = string.IsNullOrEmpty(_path)
                        ? Path.Combine(ModPaths.DataDir, "translation.cache")
                        : _path;
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (Exception ex) { ModLog.Error("[缓存] 删除缓存文件失败: " + ex.Message); }
            }
            ModLog.Info("[缓存] 已清空内存译文表并删除磁盘缓存文件。");
        }

        // ===== 清代际戳（v1.1.5）=====
        // 「我出发之后玩家清过缓存没有」这一格只有翻译线程用得上，所以戳存在缓存自己这里：
        // 清一次抬一格（Clear），取一次是一条整数读（ClearStamp）。
        // 为什么不用时间戳：Environment.TickCount 会 24.8 天回绕，而且"同一毫秒里的两次动作"分不出先后；
        // 代际号只比相等与否，回绕要 21 亿次清代，本模组活不到那时候。
        private static int _clearStamp;

        public static int ClearStamp { get { return Volatile.Read(ref _clearStamp); } }

        // 用 base64 包裹原文/译文，避免分隔符、换行破坏行格式；引擎名做命名空间，换引擎不会串味。
        // v0.28 修正：base64 尾部补齐符 '=' 会和字段分隔符 '=' 撞车，旧版约 39% 的行被 IndexOf('=') 从中间截断、
        // 解码失败 → 缓存静默丢失并重复翻译（白烧额度）。故新写入一律【去掉补齐符】，key/value 内不再含 '='。
        private static string Key(string engine, string dst, string src) => engine + "|" + dst + "|" + ToB64(src);
        private static string ToB64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=');

        // 解码：长度 %4 余 2/3 时补回被去掉的 '='；旧文件里带补齐符的条目原样也能解。
        private static string FromB64(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            try
            {
                int rem = s.Length % 4;
                if (rem == 2) s += "==";
                else if (rem == 3) s += "=";
                else if (rem == 1) return null;
                return Encoding.UTF8.GetString(Convert.FromBase64String(s));
            }
            catch { return null; }
        }

        // 把一行「key=value」切成两段。兼容两种格式：
        //   新格式：key/value 都不含 '='，分隔符就是第一个 '='。
        //   旧格式：key 的 base64 可能以 '=' 或 '==' 结尾，于是行里出现连续 '=' 段；段内【最后一个】才是分隔符
        //           （value 是 base64，绝不会以 '=' 开头）。
        // 返回 false 表示该行无效，跳过。同时容忍文件首行的 UTF-8 BOM。
        internal static bool ParseLine(string line, out string key, out string value)
        {
            key = value = null;
            if (string.IsNullOrEmpty(line)) return false;
            if (line[0] == '\uFEFF') line = line.Substring(1);   // 旧版用带 BOM 的 UTF8 写盘，首行 key 会被污染
            int p = line.IndexOf('=');
            if (p <= 0) return false;
            int r = 0;
            while (p + r < line.Length && line[p + r] == '=') r++;
            int sep = p + r - 1;
            if (sep + 1 >= line.Length) return false;
            key = line.Substring(0, sep);
            value = line.Substring(sep + 1);
            return key.Length > 0 && value.Length > 0;
        }

        // 旧文件的 key 形如 ms|ja|QW5hcmNoeQ==（带 base64 补齐符），而新算出的 key 不带补齐符 → Get 会命中不到、白重翻。
        // 载入时统一「解码第三段 → 重新按新格式编码」；任何一步失败都原样返回，不影响其它条目。
        private static string NormalizeKey(string k)
        {
            try
            {
                int a = k.IndexOf('|');
                if (a <= 0) return k;
                int b = k.IndexOf('|', a + 1);
                if (b <= a) return k;
                string src = FromB64(k.Substring(b + 1));
                if (string.IsNullOrEmpty(src)) return k;
                return k.Substring(0, b + 1) + ToB64(src);
            }
            catch { return k; }
        }
    }


    // ===== 我们自己的配置文件 =====
    // 为什么不用游戏原生的 .coc 存盘：原生那套确实能存，但改用它会让老玩家的既有设置静默消失
    // （自管 JSON 已经写了很久），所以这里刻意保持自管。
    // 路径放在 <UserData>\ModsSettings\ 下——该目录游戏会写其它模组的文件，确认可写。
    internal static class SettingsStore
    {
        // 路径由 ModPaths 在主线程（OnLoad）算好：这里不能再直接读 Application.persistentDataPath，
        // 因为 Load() 是后台工作线程经 TranslatorSetting.Register() 调进来的。
        public static string FilePath => ModPaths.SettingsFile;

        // 载入有没有走完。实机丢配置的链条是：某一项读盘抛异常 → 内存里只剩【半套】配置（后半段还是默认值）
        // → 下一次写盘（玩家点保存、20 秒定频落盘、退出前补写，三条路都一样）把这半套落成文件
        // → 盘上另外半套被静默抹掉，且没有任何地方能捞回来。所以载入没走完时【禁止写盘】是这里唯一
        // 不自证的止血口：宁可这一局改动不落盘，也不能把玩家存过的东西写没。
        internal static bool LoadIncomplete;
        private static bool _refusalLogged;

        // 字符串项统一过一遍 Keys.Clean：输入法混进来的不可见字符会让 key 校验失败，而肉眼看两者一模一样。
        // 同时它也是【缺键安全】的读取口（Json.ReadString 取不到键返回 null，直接 .Length 就是 NRE）。
        private static string Str(string json, string key) => Keys.Clean(Json.ReadString(json, key)) ?? string.Empty;

        public static void Load(TranslatorSetting s)
        {
            try
            {
                // 先当它走不完：下面任何一行抛异常，这个值就留成 true，写盘那一侧会拒绝落盘。
                LoadIncomplete = true;
                if (!File.Exists(FilePath)) { LoadIncomplete = false; ModLog.Info("[配置] 未找到配置文件，使用默认值：" + FilePath); return; }
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                // 枚举按【名字】存，所以调整下拉框顺序（需求2）不会让老玩家的配置错位成别的引擎。
                if (Enum.TryParse<TranslatorSetting.Engine>(Json.ReadString(json, "TranslationEngine"), out var eng)) s.TranslationEngine = eng;
                if (Enum.TryParse<TranslatorSetting.AiVendor>(Json.ReadString(json, "CustomVendor"), out var vendor)) s.CustomVendor = vendor;
                string tl = Json.ReadString(json, "TargetLocale"); if (!string.IsNullOrEmpty(tl)) s.TargetLocale = tl;
                s.Enabled = Json.ReadBool(json, "Enabled", s.Enabled);
                s.ScopeModOptions = Json.ReadBool(json, "ScopeModOptions", s.ScopeModOptions);
                s.ScopeAssetNames = Json.ReadBool(json, "ScopeAssetNames", s.ScopeAssetNames);
                s.ScopeAssetDescriptions = Json.ReadBool(json, "ScopeAssetDescriptions", s.ScopeAssetDescriptions);
                s.ScopeGameCore = Json.ReadBool(json, "ScopeGameCore", s.ScopeGameCore);
                s.ScopeModName = Json.ReadBool(json, "ScopeModName", s.ScopeModName);
                s.ScopeWorldLabels = Json.ReadBool(json, "ScopeWorldLabels", s.ScopeWorldLabels);
                s.MyMemoryEmail = Str(json, "MyMemoryEmail");
                s.YandexKey = Str(json, "YandexKey");
                s.MicrosoftKey = Str(json, "MicrosoftKey");
                s.MicrosoftRegion = Str(json, "MicrosoftRegion");
                s.DeepLKey = Str(json, "DeepLKey");
                s.BaiduAppId = Str(json, "BaiduAppId");
                s.BaiduKey = Str(json, "BaiduKey");
                s.GeminiKey = Str(json, "GeminiKey");
                s.GroqKey = Str(json, "GroqKey");
                s.OpenRouterKey = Str(json, "OpenRouterKey");
                s.SiliconFlowKey = Str(json, "SiliconFlowKey");
                s.CloudflareAccountId = Str(json, "CloudflareAccountId");
                s.CloudflareToken = Str(json, "CloudflareToken");
                ReadVendorCredentials(json, s);
                string model = Json.ReadString(json, "AiModel"); if (!string.IsNullOrEmpty(model)) s.AiModel = model;
                // 反馈8：厂商选「自定义」时才用到的两项。必须排在 CustomVendor 之后读 ——
                // 那个 setter 会清掉 AiModel，顺序反了玩家存过的模型名就被抹掉了。
                if (Enum.TryParse<TranslatorSetting.AiProtocol>(Json.ReadString(json, "CustomProtocol"), out var proto)) s.CustomProtocol = proto;
                // 第八轮：思考强度每档引擎、每家厂商各一格，同样必须排在引擎与厂商之后读（老存档那条单键要按它归位）。
                ReadThinking(json, s);
                s.CustomModelName = Str(json, "CustomModelName");
                // 反馈1：按键绑定由框架控件持有，但【值】必须模组自己存 —— 每次启动重新注册 action 会把
                // 框架那份打回默认。存/取的字段名与界面上那两行无关，只是路径字符串。
                s.SavedToggleKey = Display.NormalizeBindingPath(Str(json, "ToggleKeyPath"));
                s.SavedRetranslateKey = Display.NormalizeBindingPath(Str(json, "RetranslateKeyPath"));
                ModLog.Info($"[配置] 已从自管 JSON 载入：启用={s.Enabled} 引擎={s.TranslationEngine} 目标语言={s.TargetLocale} " +
                            $"模型={s.EffectiveModel} 微软key长度={s.MicrosoftKey.Length} AI厂商={s.CustomVendor} " +
                            $"该厂商key长度={s.CustomKey.Length} 该厂商已用token={TranslatorSetting.CurrentVendorTokens} " +
                            $"9家合计={s.TrackedTokens} 开关键={ShowKey(s.SavedToggleKey)} 重翻键={ShowKey(s.SavedRetranslateKey)}。路径={FilePath}");
                LoadIncomplete = false;     // 走到这里才是【整份都读进来了】，写盘那一侧随之放行
            }
            catch (Exception ex)
            {
                ModLog.Error("[配置] 载入失败（用默认值）: " + ex.Message
                             + " —— 本局已禁止写盘，以免把盘上另外半套设置抹成默认值（改动不落盘，但存过的东西不会没）。");
            }
        }

        public static bool Save(TranslatorSetting s, bool quiet = false)
        {
            // 见 LoadIncomplete 上方那段：半套配置落盘 = 把玩家存过的另外半套写没，且捞不回来。
            if (LoadIncomplete)
            {
                if (!quiet || !_refusalLogged)
                {
                    _refusalLogged = true;
                    ModLog.Error("[配置] 本次载入未完成，已拒绝写盘（否则会把存过的设置抹成默认值）。带上一条「载入失败」的异常文本反馈即可定位。");
                }
                return false;
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                Mod.CaptureBindings(s);   // 反馈1/6：按键抄在写盘之前；空值只在「本局落过地」时才当成玩家解除
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"TranslationEngine\": \"").Append(Json.Escape(s.TranslationEngine.ToString())).Append("\",\n");
                sb.Append("  \"CustomVendor\": \"").Append(Json.Escape(s.CustomVendor.ToString())).Append("\",\n");
                sb.Append("  \"TargetLocale\": \"").Append(Json.Escape(s.TargetLocale)).Append("\",\n");
                sb.Append("  \"Enabled\": ").Append(s.Enabled ? "true" : "false").Append(",\n");
                sb.Append("  \"ToggleKeyPath\": \"").Append(Json.Escape(s.SavedToggleKey ?? "")).Append("\",\n");
                sb.Append("  \"RetranslateKeyPath\": \"").Append(Json.Escape(s.SavedRetranslateKey ?? "")).Append("\",\n");
                sb.Append("  \"ScopeModOptions\": ").Append(s.ScopeModOptions ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeAssetNames\": ").Append(s.ScopeAssetNames ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeAssetDescriptions\": ").Append(s.ScopeAssetDescriptions ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeGameCore\": ").Append(s.ScopeGameCore ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeModName\": ").Append(s.ScopeModName ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeWorldLabels\": ").Append(s.ScopeWorldLabels ? "true" : "false").Append(",\n");
                Str(sb, "MyMemoryEmail", s.MyMemoryEmail);
                Str(sb, "YandexKey", s.YandexKey);
                Str(sb, "MicrosoftKey", s.MicrosoftKey);
                Str(sb, "MicrosoftRegion", s.MicrosoftRegion);
                Str(sb, "DeepLKey", s.DeepLKey);
                Str(sb, "BaiduAppId", s.BaiduAppId);
                Str(sb, "BaiduKey", s.BaiduKey);
                Str(sb, "GeminiKey", s.GeminiKey);
                Str(sb, "GroqKey", s.GroqKey);
                Str(sb, "OpenRouterKey", s.OpenRouterKey);
                Str(sb, "SiliconFlowKey", s.SiliconFlowKey);
                Str(sb, "CloudflareAccountId", s.CloudflareAccountId);
                Str(sb, "CloudflareToken", s.CloudflareToken);
                Str(sb, "AiModel", s.AiModel);
                // 反馈8：厂商选「自定义」时才用到；照存不误，玩家来回切厂商不该丢掉填过的东西。
                Str(sb, "CustomProtocol", s.CustomProtocol.ToString());
                AppendThinking(sb, s);
                Str(sb, "CustomModelName", s.CustomModelName);
                AppendVendorCredentials(sb, s);
                sb.Append("}\n");
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
                bool ok = File.Exists(FilePath);
                if (!quiet) ModLog.Info(ok ? "[配置] 已写入自管 JSON。" : "[配置] 写入后仍未找到文件。");
                else if (!ok) ModLog.Warn("[配置] 写入后仍未找到文件。");
                return ok;
            }
            catch (Exception ex) { ModLog.Error("[配置] 保存失败: " + ex.Message); return false; }
        }

        // 每行都带尾逗号，最后一项由调用方自己收尾 —— 少一个「哪项是最后一项」的隐式约定，加字段时不会漏改。
        private static void Str(StringBuilder sb, string key, string value)
            => sb.Append("  \"").Append(key).Append("\": \"").Append(Json.Escape(value)).Append("\",\n");

        // 只为日志：空着与「有一个键」必须在日志里一眼分得开。
        private static string ShowKey(string path) => string.IsNullOrEmpty(path) ? "(未绑)" : path;

        // ===== 反馈3：AI 厂商的接口地址 / key / 已用 token 每家一格 =====
        // 以前三样都是全局唯一的一条，切厂商时看到的是上一家的数据，发请求还会拿 A 家的 key 去打 B 家的端点。
        // 落盘用扁平键（CustomKey_gpt 之类），读盘按同一个顺序枚举 CustomAi.Vendors —— 加厂商只改那张表。

        private static void AppendVendorCredentials(StringBuilder sb, TranslatorSetting s)
        {
            string[] v = CustomAi.Vendors;
            for (int i = 0; i < v.Length; i++)
            {
                Str(sb, "CustomBaseUrl_" + v[i], s.GetVendorBaseUrl(v[i]));
                Str(sb, "CustomKey_" + v[i], s.GetVendorKey(v[i]));
                // token 写成裸数字：读盘走 Json.ReadLong，加引号它就取不到值（会当缺省 0 处理）。
                sb.Append("  \"CustomTokens_").Append(v[i]).Append("\": ")
                  .Append(s.GetVendorTokens(v[i]).ToString(CultureInfo.InvariantCulture))
                  .Append(i == v.Length - 1 ? "\n" : ",\n");     // 本块是全文件最后一项，末行不能带逗号
            }
        }

        private static void ReadVendorCredentials(string json, TranslatorSetting s)
        {
            // 老包（v0.30 及更早）只有共用的一条。它属于哪一家无从判断，只能归给【当时选中的那一家】，
            // 其余格留空 —— 反过来做等于给没配过的厂商凭空塞进一份凭据。
            string legacyUrl = Str(json, "CustomBaseUrl");
            string legacyKey = Str(json, "CustomKey");
            long legacyTokens = Json.ReadLong(json, "CustomTokensUsed", 0);
            string current = CustomAi.Normalize(s.CustomVendor.ToString());
            foreach (string v in CustomAi.Vendors)
            {
                string url = Str(json, "CustomBaseUrl_" + v);
                string key = Str(json, "CustomKey_" + v);
                long n = Json.ReadLong(json, "CustomTokens_" + v, 0);
                if (url.Length == 0 && key.Length == 0 && n == 0 && v == current)
                {
                    url = legacyUrl; key = legacyKey; n = legacyTokens;
                }
                s.PutVendorBaseUrl(v, url);
                s.PutVendorKey(v, key);
                s.PutVendorTokens(v, n);
            }
        }

        // ===== 第八轮：思考强度每档引擎、每家厂商各一格 =====
        // 与上面那三格同一套路：格位在 EngineKit.ThinkingSlot，落盘用【带名字】的扁平键
        // （EngineThinking_Gemini / VendorThinking_gpt），不写下标 —— 枚举或下拉框顺序将来重排，
        // 老玩家存过的档位不会凭空挪到别家名下。

        private static void AppendThinking(StringBuilder sb, TranslatorSetting s)
        {
            foreach (string e in EngineKit.Order)
            {
                if (e == "Custom")
                {
                    foreach (string v in CustomAi.Vendors)
                    {
                        if (EngineKit.ThinkingSlot(e, v) < 0) continue;    // 第九轮：千问这一格没有可调档位，也不占存盘键
                        // v1.1.7：不发送就写空串。读回来空串同样判"不发送"，所以不必另立哨兵值，
                        // 也不必新增磁盘字段（老盘的 Off/Low/High 原样照读，见 PutThinkingWire）。
                        Str(sb, "VendorThinking_" + v, s.ThinkingWire(e, v));
                    }
                }
                else if (EngineKit.ShowField(e, "thinking")) Str(sb, "EngineThinking_" + e, s.GetThinking(e, null).ToString());
            }
        }

        private static void ReadThinking(string json, TranslatorSetting s)
        {
            // ⚠ 这一颗读盘每一格都走 Str()，别退回裸 Json.ReadString：键取不到时后者返回【null】，
            // 而下面判缺省用的就是 raw.Length —— 实机在 2026-09-12 连着三局抛 NRE，把整段 Load 打断在
            // 这一行，于是排在它后面的 CustomModelName 与两条按键路径全按默认值走，下一次写盘再把默认值
            // 落进文件：玩家看到的正是「模型栏填了、快捷键设了，重启游戏就没了」。
            // 为什么会缺键：AppendThinking 对 ThinkingSlot<0 的格（千问）【根本不写】，而这张表照旧遍历九家厂商。
            // 一句话：写盘的键集合是读盘键集合的真子集，读侧就必须把「缺键」当常态，不能当异常。
            // 老包只有一条共用的 CustomThinking，它当初属于【存盘那一刻选中的那档引擎 / 那家厂商】，
            // 所以按本次读到的引擎与厂商归位，其余格留 Off。反过来做等于给没配过的引擎凭空塞一档设置。
            string legacy = Str(json, "CustomThinking");
            string curEngine = s.TranslationEngine.ToString();
            string curVendor = CustomAi.Normalize(s.CustomVendor.ToString());
            foreach (string e in EngineKit.Order)
            {
                if (e == "Custom")
                {
                    foreach (string v in CustomAi.Vendors)
                    {
                        string raw = Str(json, "VendorThinking_" + v);
                        if (raw.Length == 0 && curEngine == "Custom" && v == curVendor) raw = legacy;
                        // v1.1.7：空串 = 不发送（新玩家默认就是这样）；非空 = 发送 + 那一档。
                        // 老盘写的 "Off" 走到这里会置成 发送 + Off，而 Off 在每个 *Field 里与 None 行为完全一致
                        // ⇒ 升级玩家的请求体一字不变，界面上也只是 Off 变成 None。
                        s.PutThinkingWire(e, v, raw);
                    }
                }
                else if (EngineKit.ShowField(e, "thinking"))
                {
                    string raw = Str(json, "EngineThinking_" + e);
                    if (raw.Length == 0 && e == curEngine) raw = legacy;
                    s.PutThinking(e, null, ParseThinking(raw));
                }
            }
        }

        // 认不出来（没存过、或老包写的是别的形状）一律 Off。老档的 Off/Low/Medium/High 四个词照读不误：
        // ParseLevel 大小写不敏感，而第九轮新增的 Minimal/Xhigh/Max 只在【这一家支持】的格子里才会被写进去。
        private static CustomAi.ThinkLevel ParseThinking(string raw) => CustomAi.ParseLevel(raw);
    }

    // ===== 翻译引擎抽象 =====
    internal interface ITranslationEngine
    {
        // 引擎名 = TranslatorSetting.Engine 的枚举名。EngineKit 的所有查表（解析/缓存名/显隐/注册页）都认这个字符串。
        string Name { get; }
        string DisplayName { get; }
        string CacheName { get; }
        bool IsConfigured(TranslatorSetting s);
        string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale);

        // 反馈7：能不能一次把多条文本塞进同一个请求。批量不新增接口方法——正文由 AiPrompt.BatchUser
        // 拼成编号块，仍然走上面这条 Translate 通道，只有本标志决定后台循环要不要攒着发。
        bool Batchable { get; }
    }

    // v1.2.0 需求6：AI 引擎额外提供一条「瘦身探针」入口，只给「测试翻译」按钮用。
    // 与 Translate 的区别只有一处：提示词从整套本地化规则缩成一句「翻译这句话，直接回复译文」
    // （EngineKit.AiPrompt.TestProbe）—— 探针要回答的是"通不通"，不是"翻得好不好"。
    // 机翻引擎不实现这个接口：它们的请求体里本来就没有提示词，瘦无可瘦。
    internal interface AiProbeEngine
    {
        string TestProbe(TranslatorSetting s, string text, string toLocale);
    }

    // v1.2.0 需求5：「这条文字属于哪个翻译范围 / 这一条请求是单条还是批量」的旁路那一格
    // 已搬到 EngineKit.cs（PromptScope）—— v1.2.1 起 AiPrompt.System 自己要看那一格决定末尾补不补
    // 编号协议，而 EngineKit.cs 是无游戏依赖、能被离线回归测试（T3）直接编译的那一批文件。

    internal static class TranslationEngines
    {
        // 引擎实例本身不缓存凭据（每次都从 TranslatorSetting 现读），所以每次 new 一个的代价可以忽略，
        // 换来的是「改完设置立刻按新引擎走」，不必操心实例失效。分支顺序与下拉框顺序一致，便于对照。
        public static ITranslationEngine Get(TranslatorSetting.Engine e)
        {
            switch (e)
            {
                case TranslatorSetting.Engine.Google: return new GoogleEngine();
                case TranslatorSetting.Engine.DuckDuckGo: return new DuckDuckGoEngine();
                case TranslatorSetting.Engine.MyMemory: return new MyMemoryEngine();
                case TranslatorSetting.Engine.Yandex: return new YandexEngine();
                case TranslatorSetting.Engine.Microsoft: return new MicrosoftEngine();
                case TranslatorSetting.Engine.DeepL: return new DeepLEngine();
                case TranslatorSetting.Engine.Baidu: return new BaiduEngine();
                case TranslatorSetting.Engine.Gemini: return new GeminiEngine();
                // 三家托管 AI 平台共用一个 OpenAI 兼容类，只有枚举 id（→ base url / 默认模型 / key 字段）不同。
                case TranslatorSetting.Engine.Groq:
                case TranslatorSetting.Engine.OpenRouter:
                case TranslatorSetting.Engine.SiliconFlow: return new OpenAiCompatEngine(e);
                case TranslatorSetting.Engine.Cloudflare: return new CloudflareEngine();
                case TranslatorSetting.Engine.Custom: return new CustomEngine();
                default: return new GoogleEngine();   // 兜底也挑免 key 的那家，绝不让玩家卡在「没引擎可用」
            }
        }

        // 当前引擎还缺哪一项必填（返回 null = 可用）。判定表在 EngineKit.MissingField，
        // 这里只负责把设置里的四样凭据读出来喂给它 —— 界面标签末尾的「*」（需求2）标的就是这些必填项。
        public static string MissingField(TranslatorSetting s)
        {
            if (s == null) return "key";
            // 反馈8：只有厂商选「自定义」时接口地址才是必填的 —— 那 8 家都有官方地址可兜底，留空不影响。
            bool freeform = s.TranslationEngine == TranslatorSetting.Engine.Custom
                            && s.CustomVendor == TranslatorSetting.AiVendor.custom;
            return EngineKit.MissingField(s.TranslationEngine.ToString(),
                !string.IsNullOrEmpty(Keys.Clean(s.GetKeyFor(s.TranslationEngine))),
                !string.IsNullOrEmpty(Keys.Clean(s.GetSecondFor(s.TranslationEngine))),
                !string.IsNullOrEmpty(s.EffectiveModel),
                !string.IsNullOrEmpty(Keys.Clean(s.CloudflareAccountId)),
                !freeform || !string.IsNullOrEmpty(Keys.Clean(s.CustomBaseUrl)));
        }
    }

    internal static class Http
    {
        // v0.28（修「退出到桌面卡死无响应」）：
        // 裸 WebClient 用的是 .NET 默认超时——Timeout 100 秒、ReadWriteTimeout 300 秒。后台翻译线程一旦堵在
        // 原生 socket 读上，Unity 退出时的 Thread.Abort 就无法投递（只能等 TCP 超时），游戏表现为点了退出却没反应。
        // 对策：① 普通请求收紧到 8/10 秒；② 登记所有存活客户端，退出时主动 Abort 在飞请求，让线程立刻回到托管点。
        // 反馈2/3/4：②才是退出卡死的真正保险，①不必一刀切 —— 对话式引擎（Groq/OpenRouter/硅基流动/Gemini/
        // Cloudflare/自定义）要等服务端把译文【生成】完，10 秒远远不够：实测一局里 41 次
        // 「The operation has timed out」全落在这一类请求上，玩家看到的就是「测试连通正常但界面不翻译」
        // 「换 DeepSeek 特别慢」「批量请求全废、退回一条一条翻」。所以超时改成按次可设。
        private const int DefaultRequestMs = 8000;     // 连接 + 发送 + 等响应头
        private const int DefaultReadMs = 10000;       // 单次读/写
        public const int LlmTimeoutMs = 60000;         // 对话式引擎：一条请求最多等这么久（退出时由 AbortActive 立即中断）

        private static readonly ConcurrentDictionary<TimeoutWebClient, byte> Live = new ConcurrentDictionary<TimeoutWebClient, byte>();

        // ===== 出口（v1.1.2 的核心修复：本模组连不上翻译服务、别的网络都正常）=====
        // 2026-10-01 那一局的失败顺序是铁证：15:32:52 先是 `SecureChannelFailure（远程主机关闭了传输流）`，
        // 15:33:31 起变成 `ConnectFailure（由于目标计算机积极拒绝，无法连接）`，而且【微软、谷歌两条通道同时】
        // 报同一句 —— 服务端不会两栋楼同时拒一个客户端，"积极拒绝"= TCP SYN 打到一个【没在监听的地址:端口】上。
        // 唯一能同时解释这两点的：请求根本没直连服务端，而是先交给系统代理（玩家装有 Clash Verge），
        // 那个代理端口中途停了/换了，于是每一次请求都在本机就被拒，换引擎、换 key 都没用。
        // ⚠ 但"要不要走系统代理"这件事本模组替玩家决定不了：谷歌网页通道与 DuckDuckGo 在墙内【必须】走代理，
        //    微软/Azure 直连反而更快。所以做法不是"从此不碰代理"，而是【失败时换一条出口再试一次】：
        //    默认档（跟随系统代理）连不上 ⇒ 立刻用直连重试；反过来也一样。换档成功后【记住这一档一段时间】
        //    （EgressStickyMs），免得每条请求都先撞一次坏出口；到期后自动回到默认档重新观察一次，
        //    这样玩家把代理重新打开时不需要重启游戏。
        private const int EgressStickyMs = 120000;     // 换档成功后记住 2 分钟

        internal enum Egress { System = 0, Direct = 1 }

        private static int _egress = (int)Egress.System;      // 当前档（0=跟随系统代理，1=强制直连）
        private static int _egressUntil;                       // 这一档记到什么时候为止（TickCount）
        private static int _switchLogThrottle;                 // 同类切换日志的节流戳

        // 当前出口档位的中文名（进日志、也进「测试翻译」那句失败原因，让玩家知道是哪一段路不通）。
        public static string EgressName
        {
            get
            {
                // 到期就回到默认档（只改静态字段，无锁：读到旧值最多多试一次，不会错）。
                if (Volatile.Read(ref _egress) != (int)Egress.System && IsTickExpired(_egressUntil))
                    Interlocked.Exchange(ref _egress, (int)Egress.System);
                int e = Volatile.Read(ref _egress);
                return e == (int)Egress.Direct ? "直连" : "系统代理";
            }
        }

        // 系统代理现在指向哪里（只取 host:port，绝不打印凭据；拿不到回 null）。日志里这一格是"到底有没有在走代理"
        // 的唯一凭据 —— 玩家说"我代理早关了"时，这句能直接对上号。
        public static string ProxyHint()
        {
            try
            {
                IWebProxy p = WebRequest.DefaultWebProxy;
                if (p == null) return "(无代理对象)";
                Uri u = p.GetProxy(new Uri("https://api.cognitive.microsofttranslator.com/"));
                if (u == null) return "(解析不出)";
                if (string.Equals(u.Host, "api.cognitive.microsofttranslator.com", StringComparison.OrdinalIgnoreCase))
                    return "(未走代理)";
                return u.Host + ":" + u.Port;
            }
            catch { return "(读不到)"; }
        }

        private static bool IsTickExpired(int until)
        {
            // Environment.TickCount 会回绕，用差值判最稳（这里只用来判 2 分钟，误差无所谓）。
            return unchecked(Environment.TickCount - until) >= 0;
        }

        // 连接层失败：还没拿到任何 HTTP 响应就死了（DNS、TCP 被拒、TLS 握手、读超时）。
        // 这一类的特征是【换一条文本、换一个引擎都不会好】，所以它不能消耗"这条文本的重试预算"（见 TransMap）。
        public static bool IsConnectionFailure(Exception ex)
        {
            WebException we = ex as WebException;
            if (we == null) return false;
            switch (we.Status)
            {
                case WebExceptionStatus.ConnectFailure:
                case WebExceptionStatus.NameResolutionFailure:
                case WebExceptionStatus.ReceiveFailure:
                case WebExceptionStatus.SendFailure:
                case WebExceptionStatus.RequestCanceled:
                case WebExceptionStatus.Timeout:
                case WebExceptionStatus.ProxyNameResolutionFailure:
                case WebExceptionStatus.TrustFailure:              // TLS 证书/握手失败（实机凭据：SecureChannelFailure 就落在这格）
                case WebExceptionStatus.ConnectionClosed:
                case WebExceptionStatus.KeepAliveFailure:
                case WebExceptionStatus.ServerProtocolViolation:
                    return true;
                default:
                    return false;                                   // 含 ProtocolError —— 那是服务端已经答过话（4xx/5xx），换出口没用
            }
        }

        // 换到另一档出口，并写一行日志（同类切换 30 秒内只写一次，免得每条请求刷一行）。
        private static void FlipEgress(Exception cause)
        {
            int next = Volatile.Read(ref _egress) == (int)Egress.System ? (int)Egress.Direct : (int)Egress.System;
            Interlocked.Exchange(ref _egress, next);
            Interlocked.Exchange(ref _egressUntil, Environment.TickCount + EgressStickyMs);
            int now = Environment.TickCount;
            if (unchecked(now - Volatile.Read(ref _switchLogThrottle)) >= 0)
            {
                Interlocked.Exchange(ref _switchLogThrottle, now + 30000);
                ModLog.Warn("[出口] 走「" + (next == (int)Egress.Direct ? "系统代理" : "直连") + "」连不上服务端："
                            + Probe.Trunc(cause.Message, 120)
                            + " ⇒ 改用「" + (next == (int)Egress.Direct ? "直连" : "系统代理") + "」重试（系统代理当前指向 "
                            + (ProxyHint() ?? "?") + "；这一档先记 2 分钟，到期自动回来重新试另一条，不用重启游戏）。");
            }
        }

        public static WebClient NewClient() => NewClient(0);

        // timeoutMs <= 0 用默认档位；AI 引擎传 LlmTimeoutMs。
        public static WebClient NewClient(int timeoutMs)
        {
            TuneServicePoints();
            var wc = new TimeoutWebClient { Encoding = Encoding.UTF8 };
            if (timeoutMs > 0) { wc.RequestMs = timeoutMs; wc.ReadMs = timeoutMs; }
            Live[wc] = 0;
            return wc;
        }

        // 按当前出口档装配代理：直连档显式置 null（WebClient 的默认行为是【跟着系统代理】，不置 null 就换不掉），
        // 默认档保持不动（跟 WebRequest.DefaultWebProxy 走，代理开关由 Windows 自己管）。
        private static void ApplyEgress(WebClient wc)
        {
            if (Volatile.Read(ref _egress) == (int)Egress.Direct)
            {
                try { wc.Proxy = null; } catch { }
            }
        }

        // 反馈5：每条 AI 请求【进生成之前】都有一段跟内容无关的固定开销，三段都是默认值坑：
        // · Expect100Continue（Mono/.NET Framework 默认 true）：带 body 的 POST 先发头部、等服务端回
        //   「100 Continue」才发正文，服务端不理这一步就得干等 Expect100ContinueTimeout（默认 350ms）。
        //   OpenAI 兼容端点普遍不理这一步 —— 于是每条请求白送 350ms，一批 12 条也只省一次，但请求数
        //   越多越明显（连通性探测、获取模型列表、单条重翻都是这种小请求）。
        // · UseNagleAlgorithm（默认 true）：把小报文攒着合并发，对「一个月只发几条、每条都要抢生成时间」
        //   的翻译请求只有坏处。
        // · DefaultConnectionLimit：Mono 上是 2。三条工作线程打同一个 openrouter.ai 时，第三条会被
        //   挡在 socket 层排队 —— 上一轮把线程加到 3 条却没改这里，等于只并发了 2 条。
        // ⚠ 这三项是【进程级】设置，别的模组也在用同一份，所以只做「关掉了额外等待」和「只抬不压」。
        private static bool _tuned;

        private static void TuneServicePoints()
        {
            if (_tuned) return;
            _tuned = true;
            try
            {
                // v1.1.2：TLS 档位从「每次请求都重写成只用 Tls12」改成【只往上加、不覆盖】。
                // 旧写法是进程级的：本模组一跑，别的模组与游戏自己的 HTTPS 就被限制成只能 Tls12，
                // 而这份文件第 3237 行早就写下过原则 ——「这三项是【进程级】设置，别的模组也在用同一份，
                // 所以只做『关掉了额外等待』和『只抬不压』」。TLS 这一项同理：某些端点（含 CDN 换栈后的
                // 翻译服务）只给 Tls1.3，被我们压成 Tls1.2 就会在握手阶段被掐（实机形状 = SecureChannelFailure）。
                SecurityProtocolType cur = ServicePointManager.SecurityProtocol;
                ServicePointManager.SecurityProtocol = cur | SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; }   // Tls13，运行时不认就当没这档
                catch { }
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.UseNagleAlgorithm = false;
                if (ServicePointManager.DefaultConnectionLimit < kConnectionLimit)
                    ServicePointManager.DefaultConnectionLimit = kConnectionLimit;
            }
            catch (Exception ex) { ModLog.Warn("[网络] 连接参数调整失败（不影响翻译，只是每条请求多等一点）: " + ex.Message); }
        }

        private const int kConnectionLimit = 32;      // 远大于 kWorkerCount：让工作线程数而不是 socket 数决定并发

        // 退出收口调用：中止全部在飞请求（幂等，可反复调）。
        public static void AbortActive()
        {
            foreach (TimeoutWebClient wc in Live.Keys)
            {
                try { wc.AbortRequest(); } catch { }
            }
        }

        // ===== 统一收发入口 =====
        // 14 个引擎都要发请求，把「建客户端 → 设头 → 发 → 释放」抄 14 遍必然抄漏超时或漏 Dispose，所以只留这两个口子。
        // headers 在真正发送前执行（可为 null）；失败一律抛异常，由引擎自己 catch 后记日志并返回 null。

        public static string Get(string url, Action<WebClient> headers) => Get(url, headers, 0);

        public static string Get(string url, Action<WebClient> headers, int timeoutMs)
        {
            return Send(headers, timeoutMs, wc => wc.DownloadString(url));
        }

        public static string Post(string url, string body, Action<WebClient> headers) => Post(url, body, headers, 0);

        public static string Post(string url, string body, Action<WebClient> headers, int timeoutMs)
        {
            return Send(headers, timeoutMs, wc =>
            {
                // 默认按 JSON 发；要发表单的引擎自己在 headers 回调里改成 x-www-form-urlencoded。
                if (string.IsNullOrEmpty(wc.Headers[HttpRequestHeader.ContentType]))
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                return wc.UploadString(url, "POST", body);
            });
        }

        // 统一的"发一次，连接层不通就换出口再发一次"。⚠ 只对【连接层失败】换档：
        // 401/403/429/400 都是服务端已经答过话了，换出口重发等于把同一次失败付两遍（额度、限流都是钱）。
        // 换档后成功 ⇒ 这一档记住 EgressStickyMs（见上面那段实机凭据）；两条路都不通 ⇒ 原样把最后一个异常抛给
        // 引擎，由引擎记它那条 `[引擎名] 请求失败: …`，判据层再按"连接层失败"处理（不烧这条文本的预算）。
        private static string Send(Action<WebClient> headers, int timeoutMs, Func<WebClient, string> act)
        {
            try
            {
                using (WebClient wc = NewClient(timeoutMs))
                {
                    ApplyEgress(wc);
                    if (headers != null) headers(wc);
                    return act(wc);
                }
            }
            catch (Exception first)
            {
                if (!IsConnectionFailure(first)) throw;
                FlipEgress(first);
                // 重试这一趟用的是【全新 WebClient】：Headers/Cookie/请求体都由同一个回调重新构造过，
                // 所以重发不会把上一次半途的头部或已发出的正文带过去。
                using (WebClient wc = NewClient(timeoutMs))
                {
                    ApplyEgress(wc);
                    if (headers != null) headers(wc);
                    return act(wc);
                }
            }
        }

        // 非 2xx 时服务端返回的 body 也带着失败原因（Cloudflare 的 errors[].message、Yandex 的 message、
        // MyMemory 的 responseDetails 都是），只看 ex.Message 会得到一句没信息量的 "The remote server returned an error: (400)"。
        public static string ErrorBody(Exception ex)
        {
            WebException we = ex as WebException;
            if (we == null || we.Response == null) return null;
            try
            {
                using (Stream rs = we.Response.GetResponseStream())
                using (var sr = new StreamReader(rs, Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch { return null; }
        }

        // 反馈1/2：同一次失败的 HTTP 状态码；拿不到响应（超时、DNS、连接被重置）时回 0。
        // 有了这个数字，「被限流（该退避重试）」和「账号没余额/模型没免费档（重试一百次也没用）」才分得开。
        public static int StatusOf(Exception ex)
        {
            HttpWebResponse resp = (ex as WebException)?.Response as HttpWebResponse;
            return resp != null ? (int)resp.StatusCode : 0;
        }

        private sealed class TimeoutWebClient : WebClient
        {
            public int RequestMs = DefaultRequestMs;
            public int ReadMs = DefaultReadMs;
            private volatile HttpWebRequest _current;

            protected override WebRequest GetWebRequest(Uri address)
            {
                HttpWebRequest req = base.GetWebRequest(address) as HttpWebRequest;
                if (req != null)
                {
                    req.Timeout = RequestMs;           // 连接 + 发送 + 等响应头
                    req.ReadWriteTimeout = ReadMs;     // 单次读/写
                    _current = req;
                }
                return req;
            }

            public void AbortRequest()
            {
                HttpWebRequest req = _current;
                if (req != null) { try { req.Abort(); } catch { } }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _current = null;
                    Live.TryRemove(this, out _);
                }
                base.Dispose(disposing);
            }
        }
    }

    // ===== 14 个翻译引擎（需求1）=====
    // 分工约定：引擎类只管「读凭据 → 拼请求 → 发 → 把响应交给 EngineKit 解析」，
    // 语言码映射、URL/Body 构造、JSON 解析全在 EngineKit.cs（纯 BCL，离线壳能断言，397 条里大半是它们）。
    // Translate 返回 null 一律表示「这次没拿到译文」：后台循环会退避重试，绝不把失败当成「无需翻译」。
    internal abstract class EngineBase : ITranslationEngine
    {
        // 伪装成浏览器：谷歌 gtx / DuckDuckGo / Yandex 这几个网页通道对 .NET 默认 UA 会直接拒。
        private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0 Safari/537.36";

        protected static readonly Action<WebClient> BrowserUa = wc => wc.Headers.Add(HttpRequestHeader.UserAgent, Ua);

        protected readonly TranslatorSetting.Engine Id;

        protected EngineBase(TranslatorSetting.Engine id) { Id = id; }

        // 引擎名 = 枚举名。EngineKit 的所有 switch（ParseResponse / CacheName / ShowField…）认的就是这个字符串，
        // 所以这里绝不允许手抄字面量：抄错一个字母，解析就静默走 default 返回 null。
        public string Name => Id.ToString();

        public string CacheName => EngineKit.CacheName(Name);

        // 显示名走本模组自己的多语言表（与下拉框同一批 enum.* 键），日志与测试结果里看到的是玩家语言的名字。
        public virtual string DisplayName
        {
            get
            {
                string loc = Patches.ActiveLocale;
                return SelfL10n.T(string.IsNullOrEmpty(loc) ? "en-US" : loc, "enum." + Name.ToLowerInvariant());
            }
        }

        public abstract bool IsConfigured(TranslatorSetting s);

        public abstract string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale);

        // 反馈7：能不能攒批只由 EngineKit 那张表说了算（对话式接口才行），14 个引擎类因此一个都不用改。
        public bool Batchable => EngineKit.Batchable(Name);

        // 源语言：调用方传的是 "auto"（自动检测）。但 MyMemory / DuckDuckGo / Cloudflare m2m100 三家不吃 auto，
        // 必须给具体语言 —— 用游戏当前显示语言。读的是 Patches.SyncSettings 镜像出来的静态字段，
        // 后台线程因此不必碰任何 Unity API（GameManager.instance 在后台线程上取会抛）。
        protected static string SourceLang(string fromLocale)
        {
            if (!string.IsNullOrEmpty(fromLocale) && !string.Equals(fromLocale, "auto", StringComparison.OrdinalIgnoreCase))
                return fromLocale;
            string act = Patches.ActiveLocale;
            return string.IsNullOrEmpty(act) ? "en-US" : act;
        }

        // 统一失败出口：把服务端 body 里的原因也捞出来（只看 ex.Message 只会得到一句 "(400) Bad Request"）。
        // v1.1.7：同一颗引擎的同一句失败在 60 秒内只认真记一条，其余计数后一并省略。
        // 为什么要节流：模型名不存在/余额不足这类配置错误，旧写法会让 5 条工作线程在一个节流周期里各刷一条
        // `[ERROR] [引擎] 请求失败`（实机一局 44 条 ERROR + 约 200 条 [重试]），真正有用的一行被埋掉。
        // 状态码与 body 仍会出现在第一条上，所以诊断信息一条都没少。
        protected string Fail(Exception ex)
        {
            string body = Http.ErrorBody(ex);
            _lastStatus = Http.StatusOf(ex);
            // v1.1.2：连"这次是不是根本没连上服务端"也记一笔，理由与 _lastStatus 完全同源 ——
            // 「被限流」「账号没余额」「本机出口不通」是三种不同的处置（前两种重试才有意义，第三种要换出口），
            // 而它们在 ex.Message 里长得很像，只有 WebException.Status 分得开。判据见 Http.IsConnectionFailure。
            _lastConnectFail = Http.IsConnectionFailure(ex);
            LogFailure(Name, _lastStatus, _lastConnectFail, ex, body);
            return null;
        }

        private const int FailThrottleMs = 60000;
        private static readonly object s_failLogLock = new object();
        private static string s_failLogKey = "";
        private static int s_failLogTick;
        private static int s_failSuppressed;

        private static void LogFailure(string engine, int status, bool connFail, Exception ex, string body)
        {
            // key 里带上状态码与连接层标志：「这一家 429」和「这一家 404」是两件事，不能互相省略。
            // v1.2.0 需求8：这里用【脱敏后】的消息做节流键 —— 否则"同一个错误但 URL 里的 key 不同"
            // （玩家手滑改了 key 又改回来）会被当成两条互不相干的失败各刷一次，节流就漏了。
            string key = engine + "|" + status + "|" + connFail + "|" + (ex == null ? "" : EngineKit.RedactUrl(ex.Message));
            lock (s_failLogLock)
            {
                int now = Environment.TickCount;
                if (key == s_failLogKey && unchecked(now - s_failLogTick) < FailThrottleMs)
                {
                    s_failSuppressed++;
                    return;
                }
                int skipped = s_failSuppressed;
                s_failSuppressed = 0;
                s_failLogKey = key;
                s_failLogTick = now;
                ModLog.Error("[" + engine + "] 请求失败"
                    + (connFail ? "（连接层就没通，当前出口＝" + Http.EgressName + "，系统代理＝" + (Http.ProxyHint() ?? "?") + "）" : "")
                    // v1.2.0 需求8：ex.Message 与 body 都脱敏后再进日志。WebException 的 Message 有时
                    // 带着完整 URL（含查询参数里的 key），而 body 是服务端原样回的内容 ——
                    // 宁可多脱一次，也不能让 key 有任一条路径躺进日志文件。
                    + ": " + EngineKit.RedactUrl(ex == null ? "?" : ex.Message)
                    + (string.IsNullOrEmpty(body) ? "" : " | 服务端：" + Probe.Trunc(EngineKit.RedactUrl(body), 300))
                    + (skipped > 0 ? "（同类失败已省略 " + skipped + " 条）" : ""));
            }
        }

        // 反馈1/2：最近一次失败的 HTTP 状态码（0 = 这次没拿到响应，纯网络问题/超时）。
        // [ThreadStatic] 是必须的：并发 worker 各发各的请求，一个全局字段会被别的线程刚写的值盖掉，
        // 于是「A 线程的 429」会被当成「B 线程那条文本」的失败原因，退避判据就全乱了。
        [ThreadStatic] private static int _lastStatus;
        [ThreadStatic] private static bool _lastConnectFail;

        // 取走并清掉「上一次失败是不是连接层」——与 ConsumeStatus 同一套用法：请求回来后【立刻】取，
        // 取完即清，否则上一轮的结论会挂在线程上被下一轮误用。
        public static bool ConsumeConnectFailure()
        {
            bool v = _lastConnectFail;
            _lastConnectFail = false;
            return v;
        }

        // 第九轮：思考字段该不该发、发成什么形状，只能按玩家选的那个模型名猜（各家文档给的档位集合互相冲突，
        // 而且模型改名、上新之后旧名字就不作数）。猜错就是一个 400，而这一句就永远翻不出来了。
        // 所以带思考字段的请求被服务端拒掉时，剥掉思考字段重发一次：buildBody(null, 0) 出来的请求体
        // 与「这一格压根不存在」逐字一致，最差也就是这一句按平台默认档翻完，而不是界面上一片空白。
        // v1.2.5 需求2：测试按钮点下去时，先把【这一次请求真正带着的参数】写成一行。
        // 玩家关心的是「我改了档位，请求体到底变没变」；这一行把模型、思考字段与两段提示词的长度
        // 一次写全（不写 key、不写完整正文）。与下面 [请求] 那一行配合，就是"设置 → 实际请求"的账。
        protected void NoteProbeParams(string where, string level, string thinking, string system, string text)
        {
            ModLog.Info("[测试] 请求参数：" + where
                        + " 思考=" + CustomAi.DescribeThinking(level, thinking)
                        + " system=" + (system ?? "").Length.ToString(CultureInfo.InvariantCulture) + "字"
                        + " 正文=" + (text ?? "").Length.ToString(CultureInfo.InvariantCulture) + "字");
        }

        // 只认 400/422：429 与 401/404 跟思考字段无关，重发只是多烧一次请求。
        //
        // v1.2.5 需求2：每一次请求都在日志里留一行【用时 + 这条请求实际带的思考字段】。
        // 玩家报「档位怎么调请求体都没变」时，这三件事以前只能靠抓包/网关日志才能分开：
        //   ① 发送开关是关的（玩家自己选的）⇒ 一个字段都不带；
        //   ② 开关是开的，但模型名认不出代际 ⇒ 某几家按文档判定「发了就 400」，于是不发；
        //   ③ 字段发出去了，只是网关不认、被 400 剥掉重发。
        // 现在这一行把三者写成三种不同的字面，配合每条请求的毫秒数，慢在哪一段也能直接看出来。
        // 放在 finally 里：失败与超时同样要留一行（玩家报"慢"往往就是那一次超时）。
        protected string PostLlm(string url, Func<string, int, string> buildBody, string thinking, int extraTokens,
                                 Action<WebClient> headers, string model = null)
        {
            int start = Environment.TickCount;
            string sent = thinking;          // 实际发出去的那一段；被 400 剥掉后置空
            bool stripped = false;
            try
            {
                if (string.IsNullOrEmpty(thinking))
                    return Http.Post(url, buildBody(null, 0), headers, Http.LlmTimeoutMs);
                try
                {
                    return Http.Post(url, buildBody(thinking, extraTokens), headers, Http.LlmTimeoutMs);
                }
                catch (Exception ex)
                {
                    int st = Http.StatusOf(ex);
                    if (st != 400 && st != 422) throw;
                    sent = null;
                    stripped = true;
                    ModLog.Info("[" + Name + "] 带思考字段的请求被拒 HTTP " + st + "，剥掉思考字段重发一次（这一句按平台默认档翻）。");
                    return Http.Post(url, buildBody(null, 0), headers, Http.LlmTimeoutMs);
                }
            }
            finally
            {
                int ms = unchecked(Environment.TickCount - start);
                string thought = string.IsNullOrEmpty(thinking)
                    ? "未发送"
                    : stripped ? "未发送（400/422 后剥掉）" : sent;
                ModLog.Info("[请求] " + Name + (string.IsNullOrEmpty(model) ? "" : "/" + model)
                            + " 用时=" + ms.ToString(CultureInfo.InvariantCulture) + "ms 思考=" + thought);
            }
        }

        // 取走并清零：调用方必须紧跟在一次请求之后读它（成功失败都要读），
        // 否则这一次的 429 会挂在线程上，成为下一次不相干失败的「原因」。
        public static int ConsumeStatus()
        {
            int s = _lastStatus;
            _lastStatus = 0;
            return s;
        }
    }

    internal sealed class MicrosoftEngine : EngineBase
    {
        public MicrosoftEngine() : base(TranslatorSetting.Engine.Microsoft) { }

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Keys.Clean(s?.MicrosoftKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Keys.Clean(s?.MicrosoftKey);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            string region = Keys.Clean(s?.MicrosoftRegion);
            try
            {
                // 不带 from → 微软自动检测源语言（满足「所有非目标语言→目标语言」）。
                string url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Uri.EscapeDataString(Lang.Microsoft(toLocale));
                string body = "[{\"Text\":\"" + Json.Escape(text) + "\"}]";
                string resp = Http.Post(url, body, wc =>
                {
                    wc.Headers.Add("Ocp-Apim-Subscription-Key", key);
                    // 区域级/多服务资源必须带这个头，否则 key 正确也 401；全球（Global）资源不需要。
                    if (!string.IsNullOrEmpty(region)) wc.Headers.Add("Ocp-Apim-Subscription-Region", region);
                });
                return EngineKit.ParseResponse(Name, null, resp);
            }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    internal sealed class DeepLEngine : EngineBase
    {
        public DeepLEngine() : base(TranslatorSetting.Engine.DeepL) { }

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Keys.Clean(s?.DeepLKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Keys.Clean(s?.DeepLKey);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            try
            {
                // 免费 key 以 ":fx" 结尾，走 api-free；否则走 api（Pro）。不带 source_lang → 自动检测。
                string host = key.EndsWith(":fx", StringComparison.Ordinal) ? "api-free.deepl.com" : "api.deepl.com";
                string form = "text=" + Uri.EscapeDataString(text) + "&target_lang=" + Uri.EscapeDataString(Lang.DeepL(toLocale));
                string resp = Http.Post("https://" + host + "/v2/translate", form, wc =>
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    wc.Headers.Add("Authorization", "DeepL-Auth-Key " + key);
                });
                return EngineKit.ParseResponse(Name, null, resp);
            }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    internal sealed class BaiduEngine : EngineBase
    {
        private static readonly System.Random Rnd = new System.Random();

        public BaiduEngine() : base(TranslatorSetting.Engine.Baidu) { }

        public override bool IsConfigured(TranslatorSetting s)
            => !string.IsNullOrEmpty(Keys.Clean(s?.BaiduAppId)) && !string.IsNullOrEmpty(Keys.Clean(s?.BaiduKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string appid = Keys.Clean(s?.BaiduAppId);
            string key = Keys.Clean(s?.BaiduKey);
            if (string.IsNullOrEmpty(appid) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            try
            {
                string salt;
                lock (Rnd) salt = Rnd.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
                string sign = Md5Hex(appid + text + salt + key);
                string form = "q=" + Uri.EscapeDataString(text)
                    + "&from=auto&to=" + Uri.EscapeDataString(Lang.Baidu(toLocale))
                    + "&appid=" + Uri.EscapeDataString(appid)
                    + "&salt=" + Uri.EscapeDataString(salt)
                    + "&sign=" + Uri.EscapeDataString(sign);
                string resp = Http.Post("https://fanyi-api.baidu.com/api/trans/vip/translate", form, wc =>
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded");
                return EngineKit.ParseResponse(Name, null, resp);
            }
            catch (Exception ex) { return Fail(ex); }
        }

        private static string Md5Hex(string s)
        {
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder();
                for (int i = 0; i < hash.Length; i++) sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }

    // 需求1：谷歌直连网页版翻译接口，免注册免 key，所以排在引擎下拉框第一位、也是本模组的默认引擎。
    // 现在有【两条通道】，任一能用就翻得出来，玩家不用管：
    //   通道① 网页版自用的 batchexecute（2026-09-25 加，第一顺位）；
    //   通道② translate_a 的 client×域名轮询（2026-09-08 起 gtx 已被拦，靠 dict-chrome-ex 顶着）。
    // 为什么要两条、实测数据是什么，全写在 FreeApi 那段注释里（含「不是玩家节点挂了」的取证过程），此处不重复。
    internal sealed class GoogleEngine : EngineBase
    {
        // v1.1.7：这套降级状态机（连败几次放弃通道①、攒够几条再探一次、粘住上一次成功的 client×域名）
        // 搬进了 EngineKit.cs 的 GoogleChannels —— 留在这里离线壳一条都测不到（§10-29），而它是 5 条工作线程
        // 共用的可变状态。这里只剩【发请求 + 写日志】，计数器一律经由那个类读写（内部是 Interlocked / Volatile）。
        private static readonly GoogleChannels s_gchannels = new GoogleChannels();

        public GoogleEngine() : base(TranslatorSetting.Engine.Google) { }

        public override bool IsConfigured(TranslatorSetting s) => true;   // 完全免注册免 key

        // 通道①：谷歌网页版翻译框自己发的那条 batchexecute 请求（表单 POST，无 cookie 即可）。
        // 成功返回译文并清零失败计数；失败返回 null，原因已经记进 failures 交给总日志。
        private string TryWeb(string text, string tl, List<string> failures)
        {
            try
            {
                string resp = Http.Post(FreeApi.GoogleWebUrl, FreeApi.GoogleWebBody(text, tl, "auto"), wc =>
                {
                    BrowserUa(wc);
                    // Http.Post 默认按 JSON 发，这条接口要的是表单，不改这一行谷歌回 400。
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded;charset=UTF-8";
                });
                string r = EngineKit.ParseResponse(Name, null, resp);
                if (!string.IsNullOrEmpty(r))
                {
                    // 连败数在这里清零，所以"恢复"那一行的次数只能从清零前的返回值拿 —— 回头再读就永远是 0。
                    int failsBefore = s_gchannels.NoteWebSuccess();
                    if (GoogleChannels.ShouldLogWebRecovery(failsBefore))
                        ModLog.Info("[Google] 网页通道恢复（此前连失败 " + failsBefore + " 次），已切回第一顺位。");
                    return r;
                }
                // 这次是真拿到了 200、只是解不出译文 —— 状态码记 0，别把上一次的异常状态码留着误导排查。
                int empty = s_gchannels.NoteWebFailure(0);
                failures.Add("web 返回空：" + Probe.Trunc(resp ?? "", 120));
                if (GoogleChannels.ShouldLogWebFailure(empty)) LogWebDown(empty);
            }
            catch (Exception ex)
            {
                int n = s_gchannels.NoteWebFailure(Http.StatusOf(ex));
                failures.Add("web → " + ex.Message);
                // 只在「第一次失败」和「刚放弃」这两档各记一条：状态码含义排查时要用，但每条文本刷一行会把日志埋了。
                if (GoogleChannels.ShouldLogWebFailure(n)) LogWebDown(n);
            }
            return null;
        }

        // 「谷歌在按出口 IP 拦你」那一行。状态码取自己那次失败记下的值（s_gchannels 里最近一笔），
        // 判据与阶梯都在 GoogleChannels，所以这一行什么时候出现由 T3 那批断言钉住。
        private static void LogWebDown(int failCount)
        {
            ModLog.Error("[Google] 网页接口不通（第 " + failCount + " 次，状态码 " + s_gchannels.LastWebStatus + "）。"
                         + "429 或 302 跳到 sorry 是谷歌按【出口 IP】拦的，与节点快慢、与模组设置无关；本次改走备用接口。");
        }

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string tl = Lang.Google(toLocale);
            // 每一次失败都留一条。原来只记最后一条，结果日志里只看得到 .cn 的 404，
            // 把真正说明问题的 429 全掩盖了 —— 排查时等于没有日志。
            var failures = new List<string>();

            if (s_gchannels.ShouldAttemptWeb())
            {
                string web = TryWeb(text, tl, failures);
                if (web != null) return web;
            }

            // 通道②：translate_a 老端点，两个域名 × 两个 client 轮询。
            int hosts = FreeApi.GoogleHosts.Length, clients = FreeApi.GoogleClients.Length;
            for (int attempt = 0; attempt < hosts * clients; attempt++)
            {
                int ci, hi;
                bool changed = s_gchannels.PickCombo(attempt, hosts, clients, out ci, out hi);
                string client = FreeApi.GoogleClients[ci];
                string host = FreeApi.GoogleHosts[hi];
                string tag = client + "@" + host;
                try
                {
                    string resp = Http.Get(FreeApi.GoogleUrl(host, client, text, tl), BrowserUa);
                    string r = EngineKit.ParseResponse(Name, null, resp);
                    if (!string.IsNullOrEmpty(r))
                    {
                        if (changed)
                        {
                            s_gchannels.AdoptCombo(ci, hi);
                            ModLog.Info("[Google] 已切到可用组合：" + tag);
                        }
                        return r;
                    }
                    failures.Add(tag + " 返回空：" + Probe.Trunc(resp ?? "", 120));
                }
                catch (Exception ex) { failures.Add(tag + " → " + ex.Message); }
            }
            int wf = s_gchannels.WebFails;
            ModLog.Error("[Google] 两条通道全部失败（网页接口 " + (wf > 0 ? "已连失败 " + wf + " 次" : "未走")
                         + "，备用接口 " + (hosts * clients) + " 个 client×域名组合）："
                         + Probe.Trunc(string.Join(" | ", failures.ToArray()), 900));
            return null;
        }
    }

    // ===== 免注册通道（需求1：下拉框前四位，开箱即用）=====

    // DuckDuckGo 是两步握手：先 GET /translate 从页面里抠出 vqd 令牌，再 POST /translation.js 真正翻译。
    // 令牌是会话级的，拿到就复用；返回空/400 时丢掉重取一次，两轮都失败才判负。
    //
    // 2026-09-27 修好的一次"假死"：这一档从前被我们标成"当前网络下不通"，实际是三处各自独立的服务端/客户端
    // 变化叠在一起（逐条实测与复现脚本见交接工作区 state\ddg-probe-2026-09-27\ 与 state\ddg-verify\）：
    //   · 令牌在页面里写作 vqd="4-…"，老的抠法只认 "vqd-4" 与 vqd=4- ⇒ 永远抠不到（FreeApi.DuckDuckGoVqd 已补）；
    //   · 翻译正文不在 POST /translate（那个确实 405 nginx），而在 /translation.js?vqd=…&query=translate&to=…；
    //   · 正文是【裸 UTF-8 原文】而不是 JSON，且响应里的键叫 translated 而不是 translation。
    //     （老代码发 {"from_lang"…} 那段 JSON 时，它真的把那段 JSON 当英文翻了一遍再回给你。）
    // 实测口径：连打 30 次全 200、约 0.5 秒一次，没有 429；正文上限 1000 字符（1001 → 400 invalid_text）；
    // from 参数【没有 "auto" 这个取值】，要给自动检测就得整个省掉（实测省掉时识别准确，日文原文回 detected_language:"ja"）；
    // 换掉 query= 的值令牌就作废（403）；Content-Type 四种都实测等价，这里写 text/plain 只为语义诚实。
    // ⚠ 与谷歌那条同一性质：这是搜索结果页前端自用的内部端点，不是公开 API ⇒ 哪天开始回 4xx，
    //   先怀疑改版、重抓一份样本，不要怀疑玩家的节点，也不要再往这条链上加第三个端点。
    internal sealed class DuckDuckGoEngine : EngineBase
    {
        private static string _vqd;

        public DuckDuckGoEngine() : base(TranslatorSetting.Engine.DuckDuckGo) { }

        public override bool IsConfigured(TranslatorSetting s) => true;   // 完全免注册免 key

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            // 超长（>1000 字符）服务端直接 400 invalid_text，问了也是白问：说清原因，别让它进"重试三轮"那条链。
            if (FreeApi.DuckDuckGoTooLong(text))
            {
                ModLog.Warn("[DuckDuckGo] 这段超过它 1000 字符的正文上限（本段 " + text.Length
                            + " 字符），已跳过。界面文案正常都在几十字，出现这条说明有超长的说明文字。");
                return null;
            }
            string to = Lang.DuckDuckGo(toLocale);
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    string vqd = _vqd;
                    if (string.IsNullOrEmpty(vqd)) { vqd = FetchVqd(); _vqd = vqd; }
                    if (string.IsNullOrEmpty(vqd)) return null;   // FetchVqd 里已记过日志
                    string resp = Http.Post(FreeApi.DuckDuckGoUrl(vqd, null, to), FreeApi.DuckDuckGoBody(text), wc =>
                    {
                        BrowserUa(wc);
                        wc.Headers[HttpRequestHeader.ContentType] = "text/plain; charset=utf-8";
                        wc.Headers.Add(HttpRequestHeader.Referer, FreeApi.DuckDuckGoTranslateUrl);
                    });
                    string r = EngineKit.ParseResponse(Name, null, resp);
                    if (!string.IsNullOrEmpty(r)) return r;
                    // 没译文：把服务端那句 error（invalid_to / invalid_text / …）记下来再换令牌重试，
                    // 否则日志里只有"两轮都没拿到译文"，下次真改版时对不上号。
                    ModLog.Info("[DuckDuckGo] 这一轮没有译文：" + (FreeApi.DuckDuckGoError(resp) ?? Probe.Trunc(resp ?? "", 120)));
                    _vqd = null;   // 令牌多半过期了，下一轮重取
                }
                ModLog.Error("[DuckDuckGo] 两轮都没拿到译文（令牌或页面结构可能已变）。");
                return null;
            }
            catch (Exception ex) { _vqd = null; return Fail(ex); }
        }

        // 令牌来自 /translate 那一页（GET /translate 会 302 跟到带 rpl 的页面，跟着跳才拿得到；
        // WebClient 默认就跟跳转，所以这里不需要自己处理 302）。
        private static string FetchVqd()
        {
            try
            {
                string v = FreeApi.DuckDuckGoVqd(Http.Get(FreeApi.DuckDuckGoTranslateUrl, BrowserUa));
                if (string.IsNullOrEmpty(v)) ModLog.Error("[DuckDuckGo] 页面里抓不到 vqd 令牌，接口可能已改版。");
                return v;
            }
            catch (Exception ex) { ModLog.Error("[DuckDuckGo] 取令牌失败: " + ex.Message); return null; }
        }
    }

    // MyMemory：邮箱【选填】，填了额度从 5000 字符/天提到 50000 —— 所以没填也算配置好。
    // 它不支持自动检测源语言，langpair 两端都必须给，源语言取游戏当前显示语言（写死 en 会让日文/俄文原文全翻错）。
    internal sealed class MyMemoryEngine : EngineBase
    {
        public MyMemoryEngine() : base(TranslatorSetting.Engine.MyMemory) { }

        public override bool IsConfigured(TranslatorSetting s) => true;

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                string url = FreeApi.MyMemoryUrl(text, Lang.MyMemory(SourceLang(fromLocale)), Lang.MyMemory(toLocale), Keys.Clean(s?.MyMemoryEmail));
                string resp = Http.Get(url, BrowserUa);
                string r = EngineKit.ParseResponse(Name, null, resp);
                if (!string.IsNullOrEmpty(r)) return r;
                // 超额/异常时它回一句英文说明而不是译文，MyMemoryParse 已挡掉；这里把原因记下来方便玩家反馈。
                ModLog.Error("[MyMemory] 无译文：" + (FreeApi.MyMemoryError(resp) ?? Probe.Trunc(resp ?? "", 200)));
                return null;
            }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    // Yandex 两条路（用户选的方案）：填了 API key 走官方接口，没填走免 key 网页通道。
    // 2026-09-08 实测：/api/v1/tr.json/uuid 已下线，直接 POST /translate 回 {"code":405,"message":"Session is invalid"}，
    // 所以网页通道的 sid/ucid 只能从首页 HTML 里抓。抓不到就明确失败，不瞎猜一个 sid。
    // 2026-09-27 复核：官方接口那格连不上不是因为 key 不对，而是【主机名整个没了】
    // （translate.api.yandex.net 已 NXDOMAIN）⇒ 换成活着的老牌 v1.5，见 FreeApi.YandexOfficialUrl；
    // 免 key 那条依旧死（translate.yandex.net/api/v1/tr.json/translate 无会话时回 403 + "Session is invalid"），
    // 所以网页通道今天仍然只能等它复活，别把它当可用方案宣传（界面文案 notes.yandex 已经改成"必须填 API key"）。
    internal sealed class YandexEngine : EngineBase
    {
        private static string _sid, _ucid;
        private static int _counter;

        public YandexEngine() : base(TranslatorSetting.Engine.Yandex) { }

        // 故意不看 key：留空时还有那条"万一复活"的网页通道可试（MissingField 已经把 key 列成必填，
        // 玩家点「测试」时会明确收到"还缺必填项：key"，所以这里放过不会让失败变得无声）。
        public override bool IsConfigured(TranslatorSetting s) => true;

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string key = Keys.Clean(s?.YandexKey);
            try
            {
                return string.IsNullOrEmpty(key) ? Web(text, toLocale) : Official(key, text, toLocale);
            }
            catch (Exception ex) { _sid = null; return Fail(ex); }
        }

        private string Official(string key, string text, string toLocale)
        {
            string resp = Http.Get(FreeApi.YandexOfficialUrl(key, text, Lang.Yandex(toLocale)), BrowserUa);
            string r = EngineKit.ParseResponse(Name, null, resp);
            if (string.IsNullOrEmpty(r)) ModLog.Error("[Yandex] 官方 API 无译文：" + (FreeApi.YandexError(resp) ?? Probe.Trunc(resp ?? "", 200)));
            return r;
        }

        private string Web(string text, string toLocale)
        {
            for (int round = 0; round < 2; round++)
            {
                if (string.IsNullOrEmpty(_sid) && !FetchSession()) return null;
                string resp = Http.Post(FreeApi.YandexWebUrl(_sid, Interlocked.Increment(ref _counter)),
                    FreeApi.YandexWebForm(text, Lang.Yandex(toLocale), _ucid), wc =>
                    {
                        wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                        BrowserUa(wc);
                        wc.Headers.Add(HttpRequestHeader.Referer, FreeApi.YandexHome);
                    });
                string r = EngineKit.ParseResponse(Name, null, resp);
                if (!string.IsNullOrEmpty(r)) return r;
                _sid = null;   // 会话失效，下一轮重抓
            }
            ModLog.Error("[Yandex] 免 key 网页通道两轮都失败（sid 过期或接口已改版）。");
            return null;
        }

        private static bool FetchSession()
        {
            try
            {
                string home = Http.Get(FreeApi.YandexHome, BrowserUa);
                _sid = FreeApi.YandexSid(home);
                _ucid = FreeApi.YandexUcid(home);
                if (string.IsNullOrEmpty(_sid)) { ModLog.Error("[Yandex] 首页里抓不到 sid，网页通道可能已改版。"); return false; }
                return true;
            }
            catch (Exception ex) { ModLog.Error("[Yandex] 抓首页失败: " + ex.Message); _sid = null; return false; }
        }
    }

    // ===== 需要注册、有免费额度的 AI 引擎（需求1）=====

    // Google Gemini。模型名写别名 gemini-flash-latest 而不是版本号 ——
    // 谷歌会把别名指向当前免费档的最新 Flash，写死版本号迟早 404（需求1「用最新的免费模型」）。
    internal sealed class GeminiEngine : EngineBase, AiProbeEngine
    {
        public GeminiEngine() : base(TranslatorSetting.Engine.Gemini) { }

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Keys.Clean(s?.GeminiKey));

        // v1.2.0 需求6：测试按钮的瘦身探针。与 Translate 的差别只在 system 一句话，
        // 思考字段照旧按当前档位发（否则"思考关着的时候通不通"这一格测不到）。
        public string TestProbe(TranslatorSetting s, string text, string toLocale)
        {
            string key = Keys.Clean(s?.GeminiKey);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            try
            {
                string model = GeminiApi.DefaultModel;
                int extra;
                string level = s.GetThinking(Name, null).ToString();
                string thinking = CustomAi.ThinkingField(Name, null, null, model, level, out extra);
                NoteProbeParams("Gemini/" + model, level, thinking, AiPrompt.ProbeSystemFor(toLocale), text);
                string resp = PostLlm(GeminiApi.TranslateUrl(null, model, key),
                    (frag, room) => GeminiApi.ProbeBody(text, toLocale, frag, room), thinking, extra, null, model);
                string r = EngineKit.ParseResponse(Name, null, resp);
                if (string.IsNullOrEmpty(r)) ModLog.Error("[Gemini] 测试探针无译文：" + Probe.Trunc(resp ?? "", 200));
                return r;
            }
            catch (Exception ex) { return Fail(ex); }
        }

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Keys.Clean(s?.GeminiKey);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            try
            {
                // Gemini 的鉴权走 URL 查询参数 ?key=，模型名也在 URL 里，不在 body 里。
                // 这一档引擎没有模型下拉框（ShowField 的 model 不含 Gemini），发出去的一律是这个别名。
                string model = GeminiApi.DefaultModel;
                // v1.2.0 需求5：提示词语境跟着这条文字的范围走（Loop 在 PromptScope 里钉好了）。
                Scope.Category cat = PromptScope.Get();
                int extra;
                string thinking = CustomAi.ThinkingField(Name, null, null, model,
                    s.GetThinking(Name, null).ToString(), out extra);
                string resp = PostLlm(GeminiApi.TranslateUrl(null, model, key),
                    (frag, room) => GeminiApi.Body(cat, text, toLocale, frag, room), thinking, extra, null, model);
                string r = EngineKit.ParseResponse(Name, null, resp);
                if (string.IsNullOrEmpty(r)) ModLog.Error("[Gemini] 无译文：" + Probe.Trunc(resp ?? "", 200));
                return r;
            }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    // Groq / OpenRouter / SiliconFlow：三家都是 OpenAI 兼容的 chat/completions，
    // 只有 base url 与默认模型不同（表在 EngineKit.HostedBase / HostedModel）——
    // 所以一个类按枚举 id 实例化三次，不抄三份代码（抄三份的代价是改一处忘两处）。
    internal sealed class OpenAiCompatEngine : EngineBase, AiProbeEngine
    {
        public OpenAiCompatEngine(TranslatorSetting.Engine id) : base(id) { }

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Keys.Clean(s?.GetKeyFor(Id)));

        // v1.2.0 需求6：测试按钮的瘦身探针（system 一句话，思考字段照旧发）。
        public string TestProbe(TranslatorSetting s, string text, string toLocale)
        {
            string key = Keys.Clean(s?.GetKeyFor(Id));
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            string model = s.EffectiveModel;
            try
            {
                int extra;
                string level = s.GetThinking(Name, null).ToString();
                string thinking = CustomAi.ThinkingField(Name, null, null, model, level, out extra);
                NoteProbeParams(Name + "/" + model, level, thinking, AiPrompt.ProbeSystemFor(toLocale), text);
                string resp = PostLlm(OpenAiChat.Url(EngineKit.HostedBase(Name), null),
                    (frag, room) => OpenAiChat.ProbeBody(model, text, toLocale, frag), thinking, extra,
                    wc => wc.Headers.Add("Authorization", "Bearer " + key), model);
                string r = EngineKit.ParseResponse(Name, null, resp);
                if (string.IsNullOrEmpty(r))
                    ModLog.Error("[" + Name + "] 测试探针无译文（模型「" + model + "」可能已下架）：" + Probe.Trunc(resp ?? "", 200));
                return r;
            }
            catch (Exception ex) { return Fail(ex); }
        }

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Keys.Clean(s?.GetKeyFor(Id));
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            string model = s.EffectiveModel;
            try
            {
                // v1.2.0 需求5：提示词语境跟着这条文字的范围走（Loop 在 PromptScope 里钉好了）。
                Scope.Category cat = PromptScope.Get();
                int extra;
                string thinking = CustomAi.ThinkingField(Name, null, null, model,
                    s.GetThinking(Name, null).ToString(), out extra);
                string resp = PostLlm(OpenAiChat.Url(EngineKit.HostedBase(Name), null),
                    (frag, room) => OpenAiChat.Body(cat, model, text, toLocale, frag), thinking, extra,
                    wc => wc.Headers.Add("Authorization", "Bearer " + key), model);    // 反馈2/3/4：服务端要生成完才回，8/10 秒的通用档位会把整批请求全判成超时
                string r = EngineKit.ParseResponse(Name, null, resp);
                // 免费模型名会被平台下架，这是这四家最常见的失败原因，所以把模型名写进日志。
                if (string.IsNullOrEmpty(r))
                    ModLog.Error("[" + Name + "] 模型「" + model + "」没回译文（模型名可能已下架，去平台确认后再改模型下拉框）：" + Probe.Trunc(resp ?? "", 200));
                return r;
            }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    // Cloudflare Workers AI 跑的是 @cf/meta/m2m100-1.2b —— 机器翻译模型，不是对话模型。
    // 它不吃「翻成某语言」的自然语言提示，source_lang / target_lang 都必须是它 allowed 列表里的语言码
    // （两位 ISO 码，不是 M2M-100 的 zho_Hans 那套 —— 填错会回 422，详见 Lang.Cloudflare 的注释），
    // 且不支持自动检测，所以源语言同样取游戏当前显示语言。
    internal sealed class CloudflareEngine : EngineBase
    {
        public CloudflareEngine() : base(TranslatorSetting.Engine.Cloudflare) { }

        public override bool IsConfigured(TranslatorSetting s)
            => !string.IsNullOrEmpty(Keys.Clean(s?.CloudflareAccountId)) && !string.IsNullOrEmpty(Keys.Clean(s?.CloudflareToken));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string account = Keys.Clean(s?.CloudflareAccountId);
            string token = Keys.Clean(s?.CloudflareToken);
            if (string.IsNullOrEmpty(account) || string.IsNullOrEmpty(token) || string.IsNullOrEmpty(text)) return null;
            string src = Lang.Cloudflare(SourceLang(fromLocale));
            string dst = Lang.Cloudflare(toLocale);
            if (string.IsNullOrEmpty(src) || string.IsNullOrEmpty(dst))
            {
                ModLog.Error("[Cloudflare] m2m100 不支持这个语言对（" + SourceLang(fromLocale) + " → " + toLocale + "），换引擎或换目标语言。");
                return null;
            }
            try
            {
                string resp = Http.Post(CloudflareAi.Url(account, null), CloudflareAi.Body(text, src, dst),
                    wc => wc.Headers.Add("Authorization", "Bearer " + token), Http.LlmTimeoutMs);
                string r = CloudflareAi.Parse(resp);
                if (string.IsNullOrEmpty(r)) ModLog.Error("[Cloudflare] 无译文：" + (CloudflareAi.Error(resp) ?? Probe.Trunc(resp ?? "", 200)));
                return r;
            }
            catch (Exception ex)
            {
                // 平台把失败原因放在 body 的 errors[].message 里，状态码上只有一句 "(400) Bad Request"，
                // 所以先解析 body，解析不出东西再退回通用失败出口。
                string body = Http.ErrorBody(ex);
                string why = string.IsNullOrEmpty(body) ? null : CloudflareAi.Error(body);
                if (!string.IsNullOrEmpty(why)) { ModLog.Error("[Cloudflare] " + why); return null; }
                return Fail(ex);
            }
        }
    }

    // 自定义 AI 模型（需求1/2）：8 家主流大模型的接口格式已内置，玩家只填自己的 key。
    // 也是唯一累加 token 用量的引擎 —— 需求2 那行「已消耗token数」只在它下面显示。
    internal sealed class CustomEngine : EngineBase, AiProbeEngine
    {
        public CustomEngine() : base(TranslatorSetting.Engine.Custom) { }

        public override bool IsConfigured(TranslatorSetting s)
            => s != null && CustomAi.IsVendor(s.CustomVendor.ToString()) && !string.IsNullOrEmpty(Keys.Clean(s.CustomKey));

        // v1.2.0 需求6：测试按钮的瘦身探针。厂商/协议/key 的取法与 Translate 逐字相同
        // （含「先定 vendor 再取当前格」那条防半新半旧组合的判据），只有提示词换瘦的。
        public string TestProbe(TranslatorSetting s, string text, string toLocale)
        {
            if (s == null || string.IsNullOrEmpty(text)) return null;
            string vendor = CustomAi.Normalize(s.CustomVendor.ToString());
            string key = Keys.Clean(s.GetVendorKey(vendor));
            if (string.IsNullOrEmpty(key)) return null;
            string baseUrl = Keys.Clean(s.GetVendorBaseUrl(vendor));
            string protocol = CustomAi.Protocol(vendor, s.CustomProtocol.ToString());
            string model = s.EffectiveModel;
            int extra;
            string level = s.ThinkingWire(nameof(TranslatorSetting.Engine.Custom), vendor);
            string thinking = CustomAi.ThinkingField(nameof(TranslatorSetting.Engine.Custom), vendor, protocol, model,
                level, out extra);
            NoteProbeParams("Custom/" + vendor + "/" + model + "/" + protocol, level, thinking,
                AiPrompt.ProbeSystemFor(toLocale), text);
            try
            {
                string url = CustomAi.TranslateUrl(protocol, vendor, baseUrl, model, key);
                string resp = PostLlm(url,
                    (frag, room) => CustomAi.ProbeBodyFor(protocol, model, text, toLocale, frag, room),
                    thinking, extra, wc =>
                    {
                        wc.Headers.Add(CustomAi.AuthHeaderNameFor(protocol), CustomAi.AuthHeaderValueFor(protocol, key));
                        if (protocol == CustomAi.ProtocolAnthropic) wc.Headers.Add("anthropic-version", AnthropicApi.Version);
                    }, model);
                string r = CustomAi.ParseFor(protocol, resp);
                // v1.2.3 需求1：测试这一颗探针也要计进「该API Key已消耗Token数」。玩家原话：
                //   「该API KEY消耗token数的数据不会实时刷新吗？我看了一下测试后数值完全没变，游戏重启后才变化。
                //    如果只是测试这个请求没记进去，那就加进去。」
                // 原因就这一条：v1.2.2 及以前只有下面 Translate 那条链 AddTokens，测试按钮走的这一条没记。
                // 计费口径与 Translate 逐字一致（平台报了 usage 用真值、没报按字数估），而且喂进去的提示词
                // 必须是【这一条真实发出的】那两句：v1.2.5 起是 AiPrompt.ProbeSystemFor(locale) + TestProbe(text)，
                // 不是 Translate 那条 AiPrompt.System/User（§10 第 9 条：估算与实发必须是同一个来源）。
                // ⚠ 只有自定义 AI 这一家会累加（它也是唯一显示「已消耗Token数」那几行的引擎）；
                //   失败（抛异常）不计，与 Translate 同一条规矩；无译文但有 usage 仍计 —— 平台已经把 token 扣了。
                TranslatorSetting.AddTokens(vendor, Tokens.Charge(
                    CustomAi.UsageFor(protocol, resp), AiPrompt.ProbeSystemFor(toLocale),
                    AiPrompt.TestProbe(text), r));
                if (string.IsNullOrEmpty(r))
                    ModLog.Error("[Custom/" + vendor + "/" + protocol + "] 测试探针无译文（模型「" + model + "」）：" + Probe.Trunc(resp ?? "", 200));
                return r;
            }
            catch (Exception ex) { return Fail(ex); }
        }

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (s == null || string.IsNullOrEmpty(text)) return null;
            // 反馈3：先把厂商定下来，再按【这一家】那一格取 key 与接口地址。两样都现读「当前格」的话，
            // 请求在飞的几百毫秒里玩家切了厂商就会凑出半新半旧的组合 —— A 家的 key 打上 B 家的端点，
            // 正是这一次要修的 bug（实测日志里出现过「用第三方 sk- 打 Google 接口拿 401」）。
            string vendor = CustomAi.Normalize(s.CustomVendor.ToString());
            string key = Keys.Clean(s.GetVendorKey(vendor));
            if (string.IsNullOrEmpty(key)) return null;
            string baseUrl = Keys.Clean(s.GetVendorBaseUrl(vendor));
            // 反馈8：厂商选「自定义」时，接口格式由玩家在选项里挑；其余 8 家的格式是固定的，Protocol() 会忽略
            // 玩家的选择 —— 选了 deepseek 又把格式改成 anthropic，只会得到一个必然失败的请求。
            string protocol = CustomAi.Protocol(vendor, s.CustomProtocol.ToString());
            // 第八轮：思考强度按厂商分格，所以跟着上面那颗 vendor 一起定下来。读「当前格」的话，
            // 请求在飞的几百毫秒里玩家切了厂商，这一档就会跑到别家那一格去。
            // 第九轮：Off 不再等于「一个字段都不发」——该发什么由 CustomAi.ThinkingField 按这一家 + 这个模型名决定。
            // v1.1.7：这一行在界面上拆成「发不发」+「发哪一档」两行，所以档位可能是空串（玩家选了不发送），
            // 空串进 ThinkingField  ⇒ 一个思考字段都不写，请求体与「这一格压根不存在」逐字一致。
            string model = s.EffectiveModel;
            // v1.2.0 需求5：提示词语境跟着这条文字的范围走（Loop 在 PromptScope 里钉好了）。
            Scope.Category cat = PromptScope.Get();
            int extra;
            string thinking = CustomAi.ThinkingField(nameof(TranslatorSetting.Engine.Custom), vendor, protocol, model,
                s.ThinkingWire(nameof(TranslatorSetting.Engine.Custom), vendor), out extra);
            try
            {
                // gemini 格式的模型名与 key 都在 URL 里、anthropic 走 x-api-key + 版本头、其余一律 Bearer ——
                // 这些差异全在 CustomAi 里按 protocol 分派，这里不再出现任何厂商名特例。
                string url = CustomAi.TranslateUrl(protocol, vendor, baseUrl, model, key);
                string resp = PostLlm(url, (frag, room) => CustomAi.BodyFor(cat, protocol, model, text, toLocale, frag, room),
                    thinking, extra, wc =>
                {
                    wc.Headers.Add(CustomAi.AuthHeaderNameFor(protocol), CustomAi.AuthHeaderValueFor(protocol, key));
                    if (protocol == CustomAi.ProtocolAnthropic) wc.Headers.Add("anthropic-version", AnthropicApi.Version);
                }, model);    // 反馈3：DeepSeek/kira 这类网关生成一句就要十几秒，10 秒档位必然全军覆没
                string r = CustomAi.ParseFor(protocol, resp);
                // 需求2：平台报了 usage 就用真值，没报就按字数本地估算（所以界面文案写了「与实际消耗量可能会有出入」）。
                // ⚠ 估算时喂进去的提示词必须与【这一条真实发出的】逐字相同，否则玩家看到的 token 数对不上账 ——
                // v1.2.0 起它是按范围拼的（cat 变了整段话都变），所以这里现拼一份一样的，不能拿固定串。
                TranslatorSetting.AddTokens(vendor, Tokens.Charge(
                    CustomAi.UsageFor(protocol, resp), AiPrompt.System(cat, toLocale), AiPrompt.User(text), r));
                if (string.IsNullOrEmpty(r)) ModLog.Error("[Custom/" + vendor + "/" + protocol + "] 模型「" + model + "」没回译文：" + Probe.Trunc(resp ?? "", 200));
                return r;
            }
            catch (Exception ex) { return Fail(ex); }
        }
    }

    // ===== 本模组的运行时数据目录 =====
    // BepInEx 时代用的是 Paths.PluginPath（= BepInEx\plugins\）。发布到 Paradox Mods 之后那个目录根本不存在，
    // 而且 Mod.targets 的 DeployWIP 每次构建都会 RemoveDir 整个 Mods\<模组名>\，绝不能往部署目录里写数据。
    // 官方约定：运行时数据放 <UserData>\ModsData\<模组名>\（实测 Anarchy、FindIt、I18NEverywhere 都写在这里），
    // 设置放 <UserData>\ModsSettings\。UserData 就是 Application.persistentDataPath。
    internal static class ModPaths
    {
        private static string _dataDir;
        private static string _settingsFile;

        // 只能在主线程调用，且必须在后台线程启动前调完（Unity API 的限制）。OnLoad 里已调。
        // persistentDataPath = Application.persistentDataPath，是整个模组唯一一处读 Unity 路径的地方。
        public static void Init(string persistentDataPath)
        {
            // Unity 返回的 persistentDataPath 用正斜杠，Windows 上 Path.Combine 又掺反斜杠，
            // 拼出来是「C:/.../Cities Skylines II\ModsData\...」这种混合分隔符，会原样显示在
            // 设置页的「日志路径」栏和日志里。GetFullPath 只按词法归一化分隔符，不解析 junction、不碰磁盘。
            _dataDir = Path.GetFullPath(Path.Combine(persistentDataPath, "ModsData", "Cs2AutoTranslator"));
            _settingsFile = Path.GetFullPath(Path.Combine(persistentDataPath, "ModsSettings", "Cs2AutoTranslator.json"));
        }

        public static string DataDir => _dataDir;

        // 自管设置存档路径。放这里算是因为它同样依赖只能在主线程读的 persistentDataPath。
        public static string SettingsFile => _settingsFile;
    }

    // ===== 已注册模组 id 列表（供 Scope 判别「这条 Options.* 属于模组还是本体」）=====
    // 从 Scope.cs 搬过来：反射 typeof(ModSetting) 与 UnityEngine.Time 都留在这个【离线不可加载】的文件里，
    // Scope.cs 只收一个 Func<string[]>，这样 Scope.cs + TextKit.cs 才能被零游戏 DLL 依赖的 tests\t3 壳编译。
    // 模组在加载期陆续注册，所以列表缓存 5 秒；保存设置时 Invalidate() 强制刷一次。
    internal static class RegisteredModIds
    {
        private static string[] _ids = Array.Empty<string>();
        private static float _at = -1f;
        private static bool _ever;                  // 有没有真的反射过一次（v1.1.3 性能审查修的就是这道闸）
        private static PropertyInfo _pi;            // ModSetting.instances 是静态属性，反射一次就够

        public static void Invalidate() { _at = -1f; _ever = false; Get(); }

        public static string[] Get()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            // ⚠ 旧写法这里是 `_ids.Length > 0 && now - _at < 5f`：一台机器上【没装别的模组】时 _ids 永远是空数组，
            // 这道时间闸就永远不成立 ⇒ 每一次 Options.* 取词都重新反射一遍 ModSetting.instances 并新建一个 list。
            // 那正是 AccessAnarchy 拖慢模拟的同一条模式（"高频路径上做与变化无关的重活"，交接文档 §10 第 79 条），
            // 只不过这里的代价落在 UI 主线程的取词钩子上。判据改成"反射过一次就按时间走"。
            if (_ever && now - _at < 5f) return _ids;
            _at = now;
            _ever = true;
            try
            {
                if (_pi == null)
                    _pi = typeof(ModSetting).GetProperty("instances", BindingFlags.NonPublic | BindingFlags.Static);
                if (_pi?.GetValue(null) is IDictionary dict)
                {
                    var list = new List<string>(dict.Count);
                    foreach (var k in dict.Keys)
                    {
                        string s = k as string;
                        if (!string.IsNullOrEmpty(s) && s.IndexOf("Cs2AutoTranslator", StringComparison.Ordinal) < 0)
                            list.Add(s);
                    }
                    // 内容没变就【不要】换新数组：Scope.ClassifyMemo 是按引用判"模组 id 表换过了"的，
                    // 每 5 秒换一份新数组 = 每 5 秒把整张类目缓存白白作废一次（缓存就等于没有）。
                    if (!SameSet(_ids, list)) _ids = list.ToArray();
                }
            }
            catch { /* 反射失败：退化为「无法判别模组」，Options.* 一律归游戏本体（保守，不误翻本体设置）*/ }
            return _ids;
        }

        // 集合相等（顺序无关）：模组注册顺序会变，但同一批 id 不该被当成"表换了"。
        private static bool SameSet(string[] a, List<string> b)
        {
            if (a.Length != b.Count) return false;
            for (int i = 0; i < b.Count; i++)
                if (Array.IndexOf(a, b[i]) < 0) return false;
            return true;
        }
    }

    // ===== 本模组专属日志（需求8）=====
    // 把带时间戳的日志行追加写到 ModsData\Cs2AutoTranslator\Cs2AutoTranslator.log，
    // 同时仍转发给游戏自己的日志系统（Colossal.Logging.ILog → Logs\Modding.log 等），方便玩家单独分享本模组日志。
    internal static class ModLog
    {
        private static ILog _game;
        private static string _filePath;
        private static readonly object _lock = new object();

        public static string FilePath
        {
            get
            {
                if (string.IsNullOrEmpty(_filePath))
                    _filePath = Path.Combine(ModPaths.DataDir, "Cs2AutoTranslator.log");
                return _filePath;
            }
        }

        public static void Attach(ILog game)
        {
            _game = game;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                RotateIfTooBig();      // 先按大小归档，再写本次会话的头一行
                File.AppendAllText(FilePath,
                    "\n=== session start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===\n",
                    Encoding.UTF8);
                PruneOldLogs(); // 需求：日志只保留最近保存的 3 个，更旧的自动清理。
            }
            catch { }
        }

        // 日志按大小滚动：超过 KMaxLogBytes 就在【本次会话开始前】把它改名成 Cs2AutoTranslator-<时间戳>.log.bak。
        // 为什么必须有这一格：PruneOldLogs 只负责"多余的归档删掉"，而从前根本没有任何代码产生过 .log.bak
        // ⇒ 那 3 个名额永远只有当前这一份在用，日志其实是一条无限增长的串
        // （本机 2026-09-30 实测：30 来局累积 1.6 MB，量不大，但对一个装机几年的玩家是只增不减）。
        // 滚动之后上界就真成立了：8 MB × 最近 3 份 ⇒ 最坏 24 MB，且旧日志还在（玩家反馈问题时能翻到上一局）。
        // 文件名必须同时满足 PruneOldLogs 的过滤条件（以 Cs2AutoTranslator 开头、以 .log.bak 结尾），
        // 否则归档不会被回收 —— 那正是"加了滚动却把上界搞坏"的形状。
        private const long KMaxLogBytes = 8L * 1024 * 1024;

        private static void RotateIfTooBig()
        {
            try
            {
                if (!File.Exists(FilePath)) return;
                if (new FileInfo(FilePath).Length < KMaxLogBytes) return;
                string bak = Path.Combine(Path.GetDirectoryName(FilePath),
                    "Cs2AutoTranslator-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log.bak");
                if (File.Exists(bak)) File.Delete(bak);       // 同一秒内重复滚动（玩家反复开关游戏）：覆盖旧的那份
                File.Move(FilePath, bak);
            }
            catch { }   // 滚动失败就用原文件继续写，绝不影响载入
        }

        // 只保留最近保存的 3 个日志文件（当前 .log + 最近的 .log.bak 归档），按修改时间倒序，删掉更旧的。
        // 严格限定：文件名以 Cs2AutoTranslator 开头、以 .log 或 .log.bak 结尾 —— 绝不触碰 dll/cache/json。全程吞异常。
        // v1.2.4 需求4：这条判据（`LogKit.IsOurLogFile`）搬进 TextKit.cs 以便离线壳钉住 ——
        // 这里不再自己写第二份 StartsWith/EndsWith（两处判据会各自漂移，§10 第 9 条）。
        private const int KeepLogs = 3;
        private static void PruneOldLogs()
        {
            try
            {
                // v1.2.4 需求4 的收尾：把上一次退出时万一残留的裁剪中间文件清掉。
                // 什么时候会残留：进程在"写 .tmp 之后、Replace 之前"被强杀（崩溃/任务管理器结束）——
                // 那种情况下 .tmp 是一份新日志，而它不以 .log 结尾，不会被下面的过滤器命中，
                // 不主动清就是每崩一次多一份几 MB 的垃圾。只删这一个确定名字，不做通配 ——
                // 与"只碰自己文件"那条原则一致。
                try
                {
                    string t = FilePath + ".tmp"; if (File.Exists(t)) File.Delete(t);
                }
                catch { }

                string dir = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrEmpty(dir)) return;
                var logs = new List<FileInfo>();
                foreach (string f in Directory.GetFiles(dir))
                {
                    if (!LogKit.IsOurLogFile(Path.GetFileName(f))) continue;
                    try { logs.Add(new FileInfo(f)); } catch { }
                }
                if (logs.Count <= KeepLogs) return;
                logs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc)); // 新→旧
                for (int i = KeepLogs; i < logs.Count; i++)
                    try { logs[i].Delete(); } catch { }
            }
            catch { }
        }

        // ===== v1.2.4 需求4：日志只保留最近 10 次游戏（含闪退的那几次）=====
        // 玩家原话：「模组日志log文件只保留最近10次游戏的记录就行了（包括闪退，只要游戏启动并记录下来就算），
        // 在退出游戏时自动把前面的记录删除。注意不要在游戏退出时对安全性稳定性有影响。」
        //
        // 现状（这一版之前）：日志是【一个文件】用 `=== session start … ===` 分隔会话，只按 8 MB 大小归档，
        // 归档只留 3 份 ⇒ 一个容量上界，但没有"次数"概念。本机实测那一份攒了 112 次会话 / 3.26 MB
        // （24,963 行）—— 玩家要按【次】管，正好补这一格。
        //
        // 为什么上一版一直没做：它需要在【退出时】写盘，而退出路径是本项目最脆弱的一段
        // （§10 那条"退出到桌面卡死无响应"就是这么来的：Thread.Abort 投递不到原生 socket 读）。
        // 所以这一格的设计原则是【只做最保守的事，任何一步出问题都退化成"什么都不做"】：
        //   ① 【只碰这一份文件】：路径就是 FilePath 本身，不递归目录、不 glob、不碰 .log.bak（归档另由
        //      PruneOldLogs 按份数管）。绝不触碰 translation.cache / 设置 json / dll。
        //   ② 【量本来就小】：一份日志几 MB，ReadAllLines + WriteAllLines 各一次，几十毫秒量级；
        //      而且它只在"会话数 > 10"时才真的写（正常一局就是 11 次以上才触发一次）。
        //   ③ 【原子替换】：先写 .tmp，再 File.Replace（同卷、目标存在时是原子的）。
        //      绝不在原文件上原地截断 —— 那一步中途崩溃就是"日志被砍成半截 + 尾部全是 0"。
        //   ④ 【异常全吞】：读失败、写失败、被别人占用（玩家正用记事本开着它）、磁盘满 ——
        //      任何一条都只是"这一局不裁剪"，绝不向上抛。整个方法包在 try/catch 里，且【不 Sleep、不重试、不等锁】。
        //   ⑤ 【时机】：挂在 Application.quitting（Unity 主线程，退出通知）而不是 ProcessExit，
        //      并且排在 FlushOnExit 之后、BeginShutdown 之前 —— 那一刻设置已经落盘、翻译线程已停，
        //      这一格不会与任何写盘动作抢同一批文件。ProcessExit 兜底路径【不】做裁剪：
        //      那条路可能在进程已被拆掉一半时执行，多写一个文件不值得。
        //   ⑥ 【幂等】：写完就算完，不记状态、不需要"上次裁剪过没有"。重复调用只会看到"会话数 ≤ 10"直接返回。
        //
        // ⚠ 为什么按【会话头】数次数而不是按天/按大小：玩家要的单位就是"次游戏"，
        //   而且闪退的那一局也要算一次 —— 闪退那一局正好是玩家最想留的凭据，所以它必须【在窗口里】，
        //   只把更早的那些切掉。算法（从尾往前数第 KeepSessions 个会话头）见 LogKit.KeepFromLine，
        //   它被 tests/t3 的 AE 段逐条钉住（含"日志正文里恰好提到 session start 不算会话头"那一格）。
        private const int KeepSessions = 10;

        // 退出时调用（主线程）。**任何失败都只是"这次不裁剪"**，绝不抛出、绝不阻塞、绝不删掉原文件。
        public static void TrimSessionsOnExit()
        {
            // 与 Write 同一把锁：Write 是 File.AppendAllText（每次自己开关文件），而这里要做
            // "读全文 → 写临时文件 → 换名"。两者不互斥的话，退出那一瞬间后台 worker 正在写的那一行
            // 正好落在替换窗口里 —— 轻则那一行丢，重则 AppendAllText 拿到已换名的句柄后
            // 写进一个马上被删除的 inode（Windows 上是共享冲突异常，被 catch 吞掉，那一行丢失）。
            // ⚠ 这把锁只在这两个地方用、临界区都是纯文件 I/O（无网络、无 Sleep、无回调），
            //   所以退出时最多等一次 append 的时间（毫秒级），不会变成卡死的入口。
            lock (_lock)
            {
                try
                {
                    string path = FilePath;
                    if (!File.Exists(path)) return;
                    string[] lines;
                    try { lines = File.ReadAllLines(path, Encoding.UTF8); }
                    catch { return; }                       // 被占用/权限/IO：直接放过，不改动任何东西

                    // 会话数不够就不用动（最常见的一局就是这条早退路：新装玩家、或刚被裁过）。
                    if (LogKit.CountSessions(lines) <= KeepSessions) return;

                    int from = LogKit.KeepFromLine(lines, KeepSessions);
                    if (from <= 0 || from >= lines.Length) return;   // 0 = 不用删；>=长度 = 异常形状，宁可不动

                    // 保留的那一段：从 from 开始到最后一行。先写临时文件，再整体换名。
                    string tmp = path + ".tmp";
                    try
                    {
                        File.WriteAllLines(tmp, SubArray(lines, from), new UTF8Encoding(false));
                    }
                    catch
                    {
                        // 连临时文件都没写出来（磁盘满/无权限）⇒ 一个字都不动，把可能写了一半的残片清掉。
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        return;
                    }

                    if (!ReplaceFile(tmp, path))
                    {
                        // 换名失败：原文件仍然完好（这是下面 ReplaceFile 的设计保证），删掉临时文件收场。
                        try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
                        return;
                    }

                    ModLog.Info("[日志] 已把更早的会话裁掉：本文件现有 "
                                + LogKit.CountSessions(File.ReadAllLines(path, Encoding.UTF8))
                                + " 次游戏记录（上限 " + KeepSessions + " 次）。缓存文件不受影响。");
                }
                catch { /* 退出路径上绝不抛：裁剪失败只是这一局没裁，下一次退出还会再试 */ }
            }
        }

        // 把 tmp 换成 path。**失败时保证原文件逐字节不变。**
        //
        // 为什么只用 File.Replace、不加"先拷贝一份 .old 当退路"：
        // 上一稿加过那一步，理由是"担心 Replace 把原文件弄坏"。这条担心已经被实机语义推翻 ——
        // 本机实测（2026-10-20，目标文件被另一句柄以 FileShare.None 独占打开）：
        //   File.Replace 抛 IOException，而【原文件内容逐字节未变】。
        // 它内部是"先把源换成目的名、再处理备份"，目标被占用时在第一步之前就失败了，不会出现
        // "原文件已删、新文件没换上去"的中间态。多出来的那次全文件拷贝（Copy 到 .old）反而是
        // 整条链上最容易失败的一步（日志被记事本开着时连读都可能失败），
        // 而且它一旦崩在 Copy 与 Replace 之间就会留一份永远不会被回收的几 MB 垃圾。
        // 所以按"最少步骤、最少副作用"收口：写 tmp → Replace → 成了就删 tmp（Replace 自己会移走它）。
        private static bool ReplaceFile(string tmp, string path)
        {
            try { File.Replace(tmp, path, null); return true; }
            catch { return false; }
        }

        // 取数组的一段（net48 有 Array.Copy，不用 LINQ —— 退出路径上少一层分配）。
        private static string[] SubArray(string[] src, int from)
        {
            if (src == null) return new string[0];
            if (from <= 0) return src;
            if (from >= src.Length) return new string[0];
            var dst = new string[src.Length - from];
            Array.Copy(src, from, dst, 0, dst.Length);
            return dst;
        }

        // ILog 的方法名是 Info/Warn/Error（不是 BepInEx ManualLogSource 的 LogInfo/LogWarning/LogError），参数类型为 object。
        public static void Info(string msg) { Write("INFO", msg); _game?.Info(msg); }
        public static void Warn(string msg) { Write("WARN", msg); _game?.Warn(msg); }
        public static void Error(string msg) { Write("ERROR", msg); _game?.Error(msg); }

        private static void Write(string level, string msg)
        {
            try
            {
                string line = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " [" + level + "] " + msg + "\n";
                lock (_lock) File.AppendAllText(FilePath, line, Encoding.UTF8);
            }
            catch { }
        }
    }

    internal static class Probe
    {
        public static string Trunc(string s, int n)
        {
            if (s == null) return "<null>";
            s = s.Replace('\n', ' ').Replace('\r', ' ');
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }
    }
}
