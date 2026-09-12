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
            ModLog.Info("=== 拯救语言不通（都市天际线2 自动翻译）已载入 (v1.0.3：免费翻译引擎扩到 12 家；新增 GPT/DeepSeek/Claude 等 AI 大模型接口与自定义 API，本地统计 token 消耗；新增快捷键设置；设置界面拆成翻译/引擎/其它标签页；游戏世界渲染文本进存档后自动翻译；除目标语言外实时保存并生效；部署的 0Harmony.dll 换成 2.3.3 以消除与 Write Everywhere 的冲突) ===");
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

        // 反馈2：同时在飞的网络请求数。为什么是 3：
        // · 总请求数由「待翻条数 ÷ 每批条数」决定，加线程【不增加】总请求数，只是让它们重叠；
        //   免费档的限流按【每分钟请求数】算，所以真正需要防的是「三条一起撞墙」——由全局退避兜（见 CooldownMs）。
        // · 再往上加对玩家没好处：瓶颈是服务端生成速度，不是本地排队；而线程越多，同一屏的文字
        //   越可能被拆成几批各翻各的，白白多花几份请求。
        // · 3 条 = 一次视野内 36 条名字重叠着传，实测足够让「点一下界面等三秒」变成「点三四个界面才等一次」。
        private const int kWorkerCount = 3;

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
                // 1) 等本地化管理器就绪。
                LocalizationManager lm = WaitForLocalizationManager();
                if (lm == null) { ModLog.Error("[线程] 超时：没等到本地化管理器。"); return false; }

                // 2) 在主线程注册官方选项界面（RegisterInOptionsUI 必须在主线程）。
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    try { TranslatorSetting.Register(Instance); ModLog.Info("[选项] 已在主线程注册官方选项界面（12 语言跟随游戏语言）。"); }
                    catch (Exception ex) { ModLog.Error("[选项] 注册异常: " + ex); }
                });

                // 3) 等选项注册完成 + 字典有内容。
                if (!WaitFor(() => TranslatorSetting.Instance != null && (lm.activeDictionary?.entryCount ?? 0) > 100, 120))
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
                WaitForCooldown();     // 反馈2：被限流时三条 worker 一起歇，别让另外两条接着去撞同一堵墙。
                string src = TransMap.Take(500);
                if (src == null) { RefreshUi(true); continue; } // 队列空闲：把还没刷出去的译文一次性刷到界面。

                TranslatorSetting setting = TranslatorSetting.Instance;
                if (setting == null) { TransMap.Requeue(src); Thread.Sleep(500); continue; }

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
                    TransMap.Requeue(src);
                    Thread.Sleep(1500);
                    continue;
                }
                Patches.MarkerEnabled = true; // 引擎可用：待翻译文字显示「…」标记，翻好后原地消失。

                // 反馈7：能攒批的引擎先攒批 —— 一个视野里几十条路名/区名，逐条请求就是几十次网络往返。
                // TryBatch 返回 false 表示「这次没凑够两条」或「src 命中了缓存不必发」，交回下面的单条路径。
                if (engine.Batchable && BatchKit.CanBatch(src) && TryBatch(setting, engine, target, src)) continue;

                try
                {
                    // 命中磁盘缓存：不再联网。缓存里 value==src 表示「已知无需翻译」。
                    string cached = TranslationCache.Get(engine.CacheName, src, target);
                    if (cached != null)
                    {
                        if (cached == src) TransMap.ResolveAsSkip(src);
                        else TransMap.SetTranslation(src, cached);
                        ResetFailStreak();
                        _uiDirty = true;
                        RefreshUi(false);
                        continue;
                    }

                    // 联网翻译：源语言一律 auto（自动检测），目标语言=用户设置。
                    // 需求5：送引擎前把 {占位符} 掩成哨兵，尽量让引擎别翻花括号内部；回来后还原。
                    string[] guardTokens;
                    string masked = TransGuard.Mask(src, out guardTokens);
                    string raw = engine.Translate(setting, masked, "auto", target);
                    // 反馈1/2：紧跟着把这次的 HTTP 状态取走（线程私有，取完即清）。失败还是成功都要取，
                    // 否则上一轮的 429 会挂在线程上，被下一轮无关的失败当成原因。
                    int status = EngineBase.ConsumeStatus();
                    string trans = TransGuard.Unmask(raw, guardTokens);

                    // 翻译途中用户可能改了目标语言：结果作废，退回队列按新目标重翻。
                    if (TranslatorSetting.Instance?.TargetLocale != target) { TransMap.Requeue(src); continue; }

                    if (string.IsNullOrEmpty(trans))
                    {
                        // 引擎无返回（多半是网络/限流/key 问题）：不确定的结论，按次数退避后重试；
                        // 连失败三次这条就判死，别再让它把整条队列拖着（需求9d 的「不一次判死」仍然成立）。
                        OnFailure(src, status);
                        continue;
                    }

                    ResetFailStreak();
                    FinishOne(src, trans, target, engine);
                    _uiDirty = true;    // 无论翻成还是判定跳过，都要刷新界面（去掉「…」标记或换上译文）。
                    RefreshUi(false);   // 节流：突发翻译时最多每 1.5 秒原地刷新一次可见文字。

                    Thread.Sleep(120); // 轻微节流，别刷爆免费额度/被限流。
                }
                catch (Exception ex)
                {
                    // 异常同样按「不确定」处理，但一样有次数上限（需求9d 只要求「不因一次抖动判死」，不要求无限重试）。
                    ModLog.Error("[懒翻译] 处理异常（稍后重试该条）: " + ex.Message);
                    OnFailure(src, EngineBase.ConsumeStatus());
                }
            }
            TranslationCache.Flush(true);   // 正常退出循环：把攒下的增量条目强制写干净。
            ModLog.Info("[懒翻译] 后台循环退出。");
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
        private const int RateLimitCooldownMs = 20000;

        private static int _failStreak;
        private static int _cooldownUntil;

        private static void ResetFailStreak() { Interlocked.Exchange(ref _failStreak, 0); }

        private static void OnFailure(string src, int status)
        {
            int n = Interlocked.Increment(ref _failStreak);

            if (status == 429)
            {
                NoteRateLimited();
                TransMap.NoteFailure(src, TransMap.MaxAttempts);
                if (n == 1)
                    ModLog.Warn("[限流] " + EngineKit.DescribeStatus(429) + "：所有翻译线程一起歇 "
                                + (RateLimitCooldownMs / 1000) + " 秒再继续（已翻好的部分照常显示，不用等）。");
                return;
            }

            bool permanent = EngineKit.IsPermanentStatus(status);
            if (permanent) Interlocked.Exchange(ref _failStreak, DeadEndpointAfter);
            if (n == 1 && permanent)
                ModLog.Warn("[引擎] 服务端直接回绝（HTTP " + status + "）："
                            + (EngineKit.DescribeStatus(status) ?? "详见上一条日志")
                            + "。后续每条只试一次并歇 5 秒，请先去选项里修 key / 换模型 / 换引擎。");

            bool dead = Volatile.Read(ref _failStreak) >= DeadEndpointAfter;
            if (dead && n == DeadEndpointAfter)
                ModLog.Warn("[限流] 连续 " + DeadEndpointAfter + " 次都没翻成，判定接口暂时不可用："
                            + "后续每条只试一次并歇 5 秒，先让界面按已翻好的部分刷新（修好 key/换引擎后点保存或重新翻译即可重来）。");

            TransMap.NoteFailure(src, dead ? 1 : TransMap.MaxAttempts);
            if (dead) Thread.Sleep(5000);
            else Thread.Sleep(Math.Min(2000, 400 * n));
        }

        // 把冷却终点往后推（只抬高、不缩短：三条线程同时撞 429 时取最晚的那个）。
        private static void NoteRateLimited()
        {
            int until = Environment.TickCount + RateLimitCooldownMs;
            for (int cur = Volatile.Read(ref _cooldownUntil); until > cur; cur = Volatile.Read(ref _cooldownUntil))
                if (Interlocked.CompareExchange(ref _cooldownUntil, until, cur) == cur) break;
        }

        private static void WaitForCooldown()
        {
            for (int remain = Volatile.Read(ref _cooldownUntil) - Environment.TickCount;
                 remain > 0 && !_shutdown;
                 remain = Volatile.Read(ref _cooldownUntil) - Environment.TickCount)
                Thread.Sleep(Math.Min(remain, 1000));     // 分段睡：退出收口时最多多等 1 秒，不会卡在长睡眠里。
        }

        // 拿到一条译文之后的统一收尾：单条与批量共用，免得两份逻辑各自走偏。
        private static void FinishOne(string src, string trans, string target, ITranslationEngine engine)
        {
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
        private static bool TryBatch(TranslatorSetting setting, ITranslationEngine engine, string target, string src)
        {
            var items = new List<string> { src };
            var deferred = new List<string>();
            while (items.Count < BatchKit.MaxItems)
            {
                string more = TransMap.TakeSameTier(src, 0);   // 非阻塞，且只捞同一条队列：绝不把路名混进面板这一批
                if (more == null) break;
                if (BatchKit.CanBatch(more) && !deferred.Contains(more)) items.Add(more);
                else deferred.Add(more);                   // 不能进批的先记下，等会儿整批退回，避免同一条被反复取放
                if (deferred.Count >= BatchKit.MaxItems * 4) break;
            }
            foreach (string d in deferred) TransMap.Requeue(d);

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
            if (todo.Count < 2 || todo[0] != src)
            {
                int start = (todo.Count > 0 && todo[0] == src) ? 1 : 0;
                for (int i = start; i < todo.Count; i++) TransMap.Requeue(todo[i]);
                return start == 0;
            }

            string raw = null;
            try
            {
                raw = engine.Translate(setting, AiPrompt.BatchUser(masked.ToArray(), Lang.ForAiPrompt(target)), "auto", target);
            }
            catch (Exception ex) { ModLog.Error("[批量] 请求异常，本批退回单条: " + ex.Message); }
            // 紧跟请求取走状态码（取完即清）：下面每个提前 return 都必须经过这里，
            // 否则这一轮的 429 会挂在线程上，被下一轮无关的请求当成失败原因。
            int status = EngineBase.ConsumeStatus();

            // 请求期间玩家改了目标语言：整批作废，退回队列按新语言重来
            if (TranslatorSetting.Instance?.TargetLocale != target)
            {
                foreach (string t in todo) TransMap.Requeue(t);
                return true;
            }

            string[] parts = BatchKit.Split(raw, todo.Count);
            int ok = 0, bad = 0;
            // 与单条路径同一套判据：被限流不是这些文本的错（预算照旧，等冷却完再试）；
            // 账号级回绝或熔断已触发时，每条只试一次就放过，别让一整批 12 条一起拖着队列。
            bool dead = status != 429
                        && (EngineKit.IsPermanentStatus(status) || Volatile.Read(ref _failStreak) + 1 >= DeadEndpointAfter);
            for (int i = 0; i < todo.Count; i++)
            {
                string tr = parts[i] == null ? null : TransGuard.Unmask(parts[i], guards[i]);
                if (string.IsNullOrEmpty(tr))
                {
                    TransMap.NoteFailure(todo[i], dead ? 1 : TransMap.MaxAttempts);
                    bad++;
                    continue;
                }
                FinishOne(todo[i], tr, target, engine);
                ok++;
            }

            if (bad > 0)
            {
                if (ok == 0) OnBatchFailure(status);                 // 整批全废才算一次全局失败（约束③）
                else Interlocked.Increment(ref _failStreak);         // 部分对上：接口是活的，只记一笔、不拖慢后面的批次
            }
            else ResetFailStreak();

            if (ok > 0) { _uiDirty = true; RefreshUi(false); }
            ModLog.Info("[批量] 一次请求 " + todo.Count + " 条：对上了 " + ok + " 条，" + bad + " 条退回单条重试"
                        + (raw == null ? "（接口没返回）" : "（模型没按编号回的行会被判对不齐，不会错配）") + "。");
            Thread.Sleep(120);
            return true;
        }

        // 整批全废的收尾：与 OnFailure 共用同一份全局计数和冷却，但【不碰 TransMap】——
        // 这批每一条已经在上面的循环里各自 NoteFailure 过了，这里只负责「要不要退避、退避多久」。
        // 单条失败日志里那句人话也不用重复：这批退回单条后，单条路径会自己去撞同一次失败并写日志。
        private static void OnBatchFailure(int status)
        {
            int n = Interlocked.Increment(ref _failStreak);
            if (status == 429) { NoteRateLimited(); return; }
            if (EngineKit.IsPermanentStatus(status)) Interlocked.Exchange(ref _failStreak, DeadEndpointAfter);
            bool dead = Volatile.Read(ref _failStreak) >= DeadEndpointAfter;
            Thread.Sleep(dead ? 5000 : Math.Min(2000, 400 * n));
        }

        // 把磁盘缓存里「当前引擎 + 当前目标语言」命中的历史译文，预载进运行时 TransMap（需求9a）。
        // 这样重启后玩家看到的旧译文能立即显示，不必逐条重新显示「…」再翻一遍。
        private static void SeedCacheIntoTransMap()
        {
            try
            {
                TranslatorSetting setting = TranslatorSetting.Instance;
                if (setting == null) return;
                string engine = TranslationEngines.Get(setting.TranslationEngine).CacheName;
                string target = setting.TargetLocale;
                int n = TranslationCache.SeedTransMap(engine, target);
                if (n > 0) { _uiDirty = true; RefreshUi(true); }
                ModLog.Info($"[缓存] 预载 {n} 条历史译文进内存表（引擎={engine} 目标={target}）：重启后即时显示、不再重翻。");
            }
            catch (Exception ex) { ModLog.Error("[缓存] 预载失败: " + ex.Message); }
        }

        // 把新译文/去标记「原地」刷到当前可见界面：调用 Cohtml 的本地化重译（只刷新，不惊动 I18N 等订阅者）。
        // force=true（队列空闲时）立即刷；否则节流，突发翻译期间最多每 1500ms 刷一次，避免全屏频繁重绘。
        // 反馈2：三条 worker 会同时想刷界面，所以「查 + 改」这两个字段必须一起做，否则三个线程都判定
        // 「距上次已经 1.6 秒了」→ 同一帧重绘三遍。派活动作本身只是入队，放在锁外做。
        private static readonly object _uiRefreshLock = new object();

        private static void RefreshUi(bool force)
        {
            lock (_uiRefreshLock)
            {
                if (!_uiDirty) return;
                int now = Environment.TickCount;
                if (!force && now - _lastRefreshTick < 1500) return;
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
                        // 固定探针，目标语言=用户设置：验证服务连通的同时，回显能反映目标语言（如日语→こんにちは世界）。
                        const string probe = "Hello, world";
                        string trans = engine.Translate(setting, probe, "auto", setting.TargetLocale);
                        // 反馈1：状态码能说出「为什么没成功」，比一律怪网络靠谱得多 ——
                        // 实测这一颗探针就是玩家唯一的诊断入口（402 是没余额、404 是模型没免费档）。
                        string why = EngineKit.DescribeStatus(EngineBase.ConsumeStatus());
                        result = string.IsNullOrEmpty(trans)
                            ? $"✗ 「{engine.DisplayName}」请求没成功："
                              + (why ?? "模型可能已下架或额度不足（点上方「获取模型列表」会自动改用可用的免费模型），也可能是网络/key 不对")
                              + "。详见日志。"
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

        // 「获取模型列表」按钮回调（需求2，主线程触发、后台线程拉取）：拉到的模型名进 TranslatorSetting 的列表，
        // 下拉框刷新后就能看到。四个 AI 引擎共用（自定义 AI + Groq / OpenRouter / SiliconFlow）。
        private static void FetchModelsNow()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) return;
            PersistSettingsOnly();
            string name = setting.TranslationEngine.ToString();
            string vendor = setting.TranslationEngine == TranslatorSetting.Engine.Custom ? CustomAi.Normalize(setting.CustomVendor.ToString()) : null;
            string key = Mod.CleanKey(setting.GetKeyFor(setting.TranslationEngine));
            string baseUrl = setting.TranslationEngine == TranslatorSetting.Engine.Custom
                ? CustomAi.Base(vendor, setting.CustomBaseUrl)
                : EngineKit.HostedBase(name);
            string url = setting.TranslationEngine == TranslatorSetting.Engine.Custom
                ? CustomAi.ModelsUrl(vendor, setting.CustomBaseUrl, key)
                : (name == "Gemini" ? GeminiApi.ModelsUrl(null, key) : OpenAiChat.Url(baseUrl, "models"));
            TranslatorSetting.FetchModelsResult = "正在获取模型列表…";
            ModLog.Info("[模型列表] 引擎=" + name + " 厂商=" + (vendor ?? "-") + " 端点=" + url);
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
                MarkUiDirtyAndRefresh();
            }) { IsBackground = true }.Start();
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
            for (int i = 0; i < KeyGetters.Length; i++) KeySetters[i](s, CleanKey(KeyGetters[i](s)));
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
                Patches.Paused = false;          // 与 ClearCacheNow 相反：不是要停在原文，而是要马上重翻
                if (!PersistSettingsOnly()) return;
                SeedCacheIntoTransMap();         // 磁盘缓存刚清掉，这里等于空跑；保留是为了跟 SaveNow 走同一条路径
                _uiDirty = true; RefreshUi(true);
                WorldLabels.ForceRefresh("玩家触发重新翻译，世界文字按当前视野重新取名字并入队");
                ModLog.Info("[快捷键] 已清空缓存并按当前设置重新开始翻译。");
            }
            catch (Exception ex) { ModLog.Error("[快捷键] 重新翻译失败: " + ex.Message); }
        }

        // 去掉控制字符（< 空格，以及 DEL \u007F）和首尾空白。输入法有时会把不可见字符混进 key。
        internal static string CleanKey(string k)
        {
            if (string.IsNullOrEmpty(k)) return k ?? string.Empty;
            var sb = new StringBuilder(k.Length);
            foreach (char c in k) if (c >= ' ' && c != '\u007F') sb.Append(c);
            return sb.ToString().Trim();
        }

        private static LocalizationManager WaitForLocalizationManager()
        {
            for (int i = 0; i < 180; i++)
            {
                Thread.Sleep(1000);
                try
                {
                    LocalizationManager lm = GameManager.instance?.localizationManager;
                    if (lm != null) { ModLog.Info($"[线程] 本地化管理器就绪，activeLocaleId={lm.activeLocaleId}。"); return lm; }
                }
                catch (Exception ex) { ModLog.Error("[线程] 等待本地化异常: " + ex.Message); }
            }
            return null;
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
        private const string HarmonyId = "com.yourname.cs2autotranslator";
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
        internal static volatile string EngineCacheName = "google";

        // 从 TranslatorSetting.Instance 把开关/目标语言/引擎名镜像到上面的静态字段。主线程或后台线程皆可调用。
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
                try { ActiveLocale = GameManager.instance?.localizationManager?.activeLocaleId ?? ""; } catch { }
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
        }

        // 反射重渲染当前可见选项页（需求1）。不直接 typeof(OptionsUISystem)（万一它是 internal 或命名空间不同会导致整个编译失败），
        // 改为按全名运行时解析类型；跨版本可编译，任何一步失败都静默跳过（只是不刷新选项页，不影响翻译主功能）。
        private static Type _optionsType;
        private static bool _optionsTypeSearched;

        private static void RefreshOptionsPage()
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
        private static object GetOptionsSystem()
        {
            try
            {
                Type optType = GetOptionsType();
                if (optType == null) return null;

                Type worldType = typeof(Unity.Entities.World);
                object world = worldType.GetProperty("DefaultGameObjectInjectionWorld", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (world == null) return null;

                MethodInfo getSys = worldType.GetMethod("GetExistingSystemManaged", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
                return getSys?.Invoke(world, new object[] { optType });
            }
            catch { return null; }
        }

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
        private static object ReadObj(object o, string prop)
        {
            const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            try { return o.GetType().GetProperty(prop, F)?.GetValue(o, null); }
            catch { return null; }
        }

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
                // 不翻译本模组自己的选项文字（已由 L10n 12 语言表 / SelfL10n 按游戏语言渲染）。
                if (key.IndexOf("Cs2AutoTranslator", StringComparison.Ordinal) >= 0) return true;

                // 范围过滤（需求2）：未勾选的类目直接放行原文，不翻不标记。
                Scope.Category cat = Scope.Classify(key);
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

                TransMap.Enqueue(baseVal);            // 首次见到：入队后台翻译（入队的是干净原文，绝不含标记）。
                if (!_firstEnqueueLogged) { _firstEnqueueLogged = true; ModLog.Info($"[Hook] 前缀已生效，首次入队待翻译文本：'{Probe.Trunc(baseVal, 40)}'（key={Probe.Trunc(key, 40)}）。"); }
                // 静态待翻译标记：原文末尾加一个「…」表示「正在翻译」，翻好后连同标记一起原地变成译文。
                if (MarkerEnabled) { data.Set(baseVal + PendingMarker); return false; }
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
                TransMap.Enqueue(src);                                  // 入队后台翻译；先返回原文，翻好后由 WorldLabels 清缓存让游戏重新取一遍
            }
            catch { /* 实验性钩子：任何异常都放行原文，绝不影响世界渲染 */ }
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
        // 现在界面文字走前台，工作线程【只有前台空了】才去捞世界文字；两条队列共用同一套退避与在飞计数。
        private static readonly BlockingCollection<string> Pending = new BlockingCollection<string>(new ConcurrentQueue<string>());
        private static readonly BlockingCollection<string> PendingWorld = new BlockingCollection<string>(new ConcurrentQueue<string>());

        // 反馈7：「在飞」计数。Take() 已经把这条从队列里摘走了，请求却还要跑 0.1~8 秒，
        // 所以光看 Pending.Count==0 会在「最后一条还堵在网上」时误判排空，把首批刷新提前发出去。
        // 不变式：Take 取走一条 → +1；这条最终走到 SetTranslation / ResolveAsSkip / Requeue 之一 → −1。
        private static int _inFlight;

        // 反馈7：单条文本的失败次数与「本局判死」标记。
        // 过去失败只走全局退避 + 无限重排队：一条永久翻不成的文本（下架的模型、坏掉的占位符）
        // 会把整条队列拖在 30 秒一次的退避里，队列永远排不空，界面就一直不刷新 —— 正是玩家说的
        // 「不可能进入存档后等半个小时才刷新出来吧」。判死后本局保留原文，改设置或按重新翻译时 Reset 复活。
        public const int MaxAttempts = 3;
        private static readonly ConcurrentDictionary<string, int> Attempts = new ConcurrentDictionary<string, int>();
        private static readonly ConcurrentDictionary<string, byte> GivenUp = new ConcurrentDictionary<string, byte>();

        public static bool TryGetTranslated(string src, out string tr) => Translated.TryGetValue(src, out tr);

        public static bool IsResolved(string src) => Resolved.ContainsKey(src) || Translated.ContainsKey(src) || GivenUp.ContainsKey(src);

        // 队列空且没有在飞的请求 = 真的闲下来了（反馈6 的首批刷新判据）。两条队列都算。
        public static bool IsIdle => Pending.Count == 0 && PendingWorld.Count == 0 && Volatile.Read(ref _inFlight) == 0;

        // 走哪条队列由需求3 那本 WorldSources 台账决定 —— WorldLabelPostfix 在 Enqueue 的【前一行的】
        // MarkWorldSource(src) 刚登记过，所以入队时判据已经就绪，不必多传一个参数。
        private static BlockingCollection<string> QueueFor(string src)
            => WorldSources.ContainsKey(src) ? PendingWorld : Pending;

        public static void Enqueue(string src)
        {
            if (string.IsNullOrEmpty(src)) return;
            if (Translated.ContainsKey(src) || Resolved.ContainsKey(src) || GivenUp.ContainsKey(src)) return;
            if (Queued.TryAdd(src, 0)) { try { QueueFor(src).Add(src); } catch { /* 已 CompleteAdding */ } }
        }

        public static string Take(int msTimeout)
        {
            string s = TakeFrom(Pending, msTimeout);
            return s ?? TakeFrom(PendingWorld, 0);
        }

        // 攒一批时继续捞用：只从【同一条队列】拿。混着攒的话，一条面板文字会顺手拽进 11 条路名，
        // 这一屏就得等整批生成完 —— 优先级就白分了。
        public static string TakeSameTier(string src, int msTimeout) => TakeFrom(QueueFor(src), msTimeout);

        private static string TakeFrom(BlockingCollection<string> q, int msTimeout)
        {
            try
            {
                if (!q.TryTake(out string s, msTimeout)) return null;
                Interlocked.Increment(ref _inFlight);
                return s;
            }
            catch { return null; }
        }

        // 反馈7：这条又失败了一次。未到上限退回队列（返回值就是第几次失败，调用方据此决定退避多久）；
        // 到上限就判死：不再入队、界面保留原文，返回 0，调用方应把全局退避清零 —— 别让一条坏文本
        // 把后面所有正常文本一起拖慢。maxAttempts 由调用方给：端点像是已经死了时压到 1，逐条只试一次就放过。
        public static int NoteFailure(string src, int maxAttempts = MaxAttempts)
        {
            int n = Attempts.AddOrUpdate(src, 1, (_, v) => v + 1);
            if (n < maxAttempts) { Requeue(src); return n; }
            GivenUp[src] = 0;
            Attempts.TryRemove(src, out _);
            Queued.TryRemove(src, out _);
            Interlocked.Decrement(ref _inFlight);
            ModLog.Warn("[重试] 「" + Probe.Trunc(src, 30) + "」第 " + n + " 次没翻成，本局先保留原文"
                        + "（改设置、点保存或按重新翻译都会重试）。");
            return 0;
        }

        public static void Requeue(string src)
        {
            if (string.IsNullOrEmpty(src)) return;
            Interlocked.Decrement(ref _inFlight);
            try { QueueFor(src).Add(src); } catch { }     // 回哪条队列与入队时同一个判据，别把路名塞回前台
        }

        public static void SetTranslation(string src, string tr)
        {
            Translated[src] = tr;
            Queued.TryRemove(src, out _);
            Resolved.TryRemove(src, out _);
            GivenUp.TryRemove(src, out _);
            Attempts.TryRemove(src, out _);
            Interlocked.Decrement(ref _inFlight);
            CountIfWorld(src);
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
            Interlocked.Decrement(ref _inFlight);
        }

        // 目标语言/引擎变更后清空内存译文，按新设置重新解析（磁盘缓存命中即时恢复）。
        public static void Reset()
        {
            Translated.Clear();
            Resolved.Clear();
            Queued.Clear();
            WorldSources.Clear();
            Attempts.Clear();
            // 反馈7：判死是「本局」的决定，不能跨设置变更留着 —— 玩家换引擎/换语言/按重新翻译就是想再试一次。
            GivenUp.Clear();
            Interlocked.Exchange(ref _inFlight, 0);
            ClearWorldNew();
            while (Pending.TryTake(out _)) { }
            while (PendingWorld.TryTake(out _)) { }
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
        // 只由主线程的按钮回调（SaveNow / ClearCacheNow）调用，所以不需要限流。
        public static void ForceRefresh(string reason)
        {
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
        public static int SeedTransMap(string engine, string dst)
        {
            if (string.IsNullOrEmpty(engine) || string.IsNullOrEmpty(dst)) return 0;
            string prefix = engine + "|" + dst + "|";
            int n = 0;
            foreach (var kv in Dict)
            {
                if (!kv.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                string src = FromB64(kv.Key.Substring(prefix.Length));
                string trans = FromB64(kv.Value);
                if (string.IsNullOrEmpty(src)) continue;
                if (string.IsNullOrEmpty(trans) || trans == src) TransMap.ResolveAsSkip(src);
                else { TransMap.SetTranslation(src, trans); n++; }
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

        // 字符串项统一过一遍 CleanKey：输入法混进来的不可见字符会让 key 校验失败，而肉眼看两者一模一样。
        // 同时它也是【缺键安全】的读取口（Json.ReadString 取不到键返回 null，直接 .Length 就是 NRE）。
        private static string Str(string json, string key) => Mod.CleanKey(Json.ReadString(json, key)) ?? string.Empty;

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
                        Str(sb, "VendorThinking_" + v, s.GetThinking(e, v).ToString());
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
                        s.PutThinking(e, v, ParseThinking(raw));
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
                !string.IsNullOrEmpty(Mod.CleanKey(s.GetKeyFor(s.TranslationEngine))),
                !string.IsNullOrEmpty(Mod.CleanKey(s.GetSecondFor(s.TranslationEngine))),
                !string.IsNullOrEmpty(s.EffectiveModel),
                !string.IsNullOrEmpty(Mod.CleanKey(s.CloudflareAccountId)),
                !freeform || !string.IsNullOrEmpty(Mod.CleanKey(s.CustomBaseUrl)));
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

        public static WebClient NewClient() => NewClient(0);

        // timeoutMs <= 0 用默认档位；AI 引擎传 LlmTimeoutMs。
        public static WebClient NewClient(int timeoutMs)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            TuneServicePoints();
            var wc = new TimeoutWebClient { Encoding = Encoding.UTF8 };
            if (timeoutMs > 0) { wc.RequestMs = timeoutMs; wc.ReadMs = timeoutMs; }
            Live[wc] = 0;
            return wc;
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
            using (WebClient wc = NewClient(timeoutMs))
            {
                if (headers != null) headers(wc);
                return wc.DownloadString(url);
            }
        }

        public static string Post(string url, string body, Action<WebClient> headers) => Post(url, body, headers, 0);

        public static string Post(string url, string body, Action<WebClient> headers, int timeoutMs)
        {
            using (WebClient wc = NewClient(timeoutMs))
            {
                if (headers != null) headers(wc);
                // 默认按 JSON 发；要发表单的引擎自己在 headers 回调里改成 x-www-form-urlencoded。
                if (string.IsNullOrEmpty(wc.Headers[HttpRequestHeader.ContentType]))
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                return wc.UploadString(url, "POST", body);
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
        protected string Fail(Exception ex)
        {
            string body = Http.ErrorBody(ex);
            _lastStatus = Http.StatusOf(ex);
            ModLog.Error("[" + Name + "] 请求失败: " + ex.Message
                + (string.IsNullOrEmpty(body) ? "" : " | 服务端：" + Probe.Trunc(body, 300)));
            return null;
        }

        // 反馈1/2：最近一次失败的 HTTP 状态码（0 = 这次没拿到响应，纯网络问题/超时）。
        // [ThreadStatic] 是必须的：并发 worker 各发各的请求，一个全局字段会被别的线程刚写的值盖掉，
        // 于是「A 线程的 429」会被当成「B 线程那条文本」的失败原因，退避判据就全乱了。
        [ThreadStatic] private static int _lastStatus;

        // 第九轮：思考字段该不该发、发成什么形状，只能按玩家选的那个模型名猜（各家文档给的档位集合互相冲突，
        // 而且模型改名、上新之后旧名字就不作数）。猜错就是一个 400，而这一句就永远翻不出来了。
        // 所以带思考字段的请求被服务端拒掉时，剥掉思考字段重发一次：buildBody(null, 0) 出来的请求体
        // 与「这一格压根不存在」逐字一致，最差也就是这一句按平台默认档翻完，而不是界面上一片空白。
        // 只认 400/422：429 与 401/404 跟思考字段无关，重发只是多烧一次请求。
        protected string PostLlm(string url, Func<string, int, string> buildBody, string thinking, int extraTokens, Action<WebClient> headers)
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
                ModLog.Info("[" + Name + "] 带思考字段的请求被拒 HTTP " + st + "，剥掉思考字段重发一次（这一句按平台默认档翻）。");
                return Http.Post(url, buildBody(null, 0), headers, Http.LlmTimeoutMs);
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

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.MicrosoftKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Mod.CleanKey(s?.MicrosoftKey);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            string region = Mod.CleanKey(s?.MicrosoftRegion);
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

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.DeepLKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Mod.CleanKey(s?.DeepLKey);
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
            => !string.IsNullOrEmpty(Mod.CleanKey(s?.BaiduAppId)) && !string.IsNullOrEmpty(Mod.CleanKey(s?.BaiduKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string appid = Mod.CleanKey(s?.BaiduAppId);
            string key = Mod.CleanKey(s?.BaiduKey);
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
    // 2026-09-08：client=gtx 已被反爬拦截（429），dict-chrome-ex 仍可用 —— 所以 client 与域名都要轮询，见 FreeApi.GoogleClients。
    internal sealed class GoogleEngine : EngineBase
    {
        // 上次成功的 client 与域名下标。粘住它们，免得每条文本都从头白试一遍（每个组合 8 秒超时）。
        private static int _clientIndex;
        private static int _hostIndex;

        public GoogleEngine() : base(TranslatorSetting.Engine.Google) { }

        public override bool IsConfigured(TranslatorSetting s) => true;   // 完全免注册免 key

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string tl = Lang.Google(toLocale);
            int hosts = FreeApi.GoogleHosts.Length, clients = FreeApi.GoogleClients.Length;
            // 每一次失败都留一条。原来只记最后一条，结果日志里只看得到 .cn 的 404，
            // 把真正说明问题的 429 全掩盖了 —— 排查时等于没有日志。
            var failures = new List<string>();
            for (int attempt = 0; attempt < hosts * clients; attempt++)
            {
                int ci = (_clientIndex + attempt / hosts) % clients;
                int hi = (_hostIndex + attempt) % hosts;
                string client = FreeApi.GoogleClients[ci];
                string host = FreeApi.GoogleHosts[hi];
                string tag = client + "@" + host;
                try
                {
                    string resp = Http.Get(FreeApi.GoogleUrl(host, client, text, tl), BrowserUa);
                    string r = EngineKit.ParseResponse(Name, null, resp);
                    if (!string.IsNullOrEmpty(r))
                    {
                        if (ci != _clientIndex || hi != _hostIndex)
                        {
                            _clientIndex = ci; _hostIndex = hi;
                            ModLog.Info("[Google] 已切到可用组合：" + tag);
                        }
                        return r;
                    }
                    failures.Add(tag + " 返回空：" + Probe.Trunc(resp ?? "", 120));
                }
                catch (Exception ex) { failures.Add(tag + " → " + ex.Message); }
            }
            ModLog.Error("[Google] " + (hosts * clients) + " 个 client×域名组合全部失败："
                         + Probe.Trunc(string.Join(" | ", failures.ToArray()), 900));
            return null;
        }
    }

    // ===== 免注册通道（需求1：下拉框前四位，开箱即用）=====

    // DuckDuckGo 是两步握手：先 GET /translate 从页面里抠 vqd-4 令牌，再带 x-vqd-4 头 POST。
    // 令牌是会话级的，拿到就复用；失效（返回空）时丢掉重取一次，两轮都失败才判负。
    internal sealed class DuckDuckGoEngine : EngineBase
    {
        private static string _vqd;

        public DuckDuckGoEngine() : base(TranslatorSetting.Engine.DuckDuckGo) { }

        public override bool IsConfigured(TranslatorSetting s) => true;   // 完全免注册免 key

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string from = Lang.DuckDuckGo(SourceLang(fromLocale));
            string to = Lang.DuckDuckGo(toLocale);
            try
            {
                for (int round = 0; round < 2; round++)
                {
                    string vqd = _vqd ?? (_vqd = FetchVqd());
                    if (string.IsNullOrEmpty(vqd)) return null;   // FetchVqd 里已记过日志
                    string resp = Http.Post(FreeApi.DuckDuckGoTranslateUrl, FreeApi.DuckDuckGoBody(text, from, to), wc =>
                    {
                        BrowserUa(wc);
                        wc.Headers.Add("x-vqd-4", vqd);
                        wc.Headers.Add(HttpRequestHeader.Referer, "https://duckduckgo.com/");
                    });
                    string r = EngineKit.ParseResponse(Name, null, resp);
                    if (!string.IsNullOrEmpty(r)) return r;
                    _vqd = null;   // 令牌多半过期了，下一轮重取
                }
                ModLog.Error("[DuckDuckGo] 两轮都没拿到译文（令牌或页面结构可能已变）。");
                return null;
            }
            catch (Exception ex) { _vqd = null; return Fail(ex); }
        }

        private static string FetchVqd()
        {
            try
            {
                string v = FreeApi.DuckDuckGoVqd(Http.Get(FreeApi.DuckDuckGoTranslateUrl, BrowserUa));
                if (string.IsNullOrEmpty(v)) ModLog.Error("[DuckDuckGo] 页面里抓不到 vqd-4 令牌，接口可能已改版。");
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
                string url = FreeApi.MyMemoryUrl(text, Lang.MyMemory(SourceLang(fromLocale)), Lang.MyMemory(toLocale), Mod.CleanKey(s?.MyMemoryEmail));
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

    // Yandex 两条路（用户选的方案）：填了 IAM token 走官方 API，没填走免 key 网页通道。
    // 2026-09-08 实测：/api/v1/tr.json/uuid 已下线，直接 POST /translate 回 {"code":405,"message":"Session is invalid"}，
    // 所以网页通道的 sid/ucid 只能从首页 HTML 里抓。抓不到就明确失败，不瞎猜一个 sid。
    internal sealed class YandexEngine : EngineBase
    {
        private static string _sid, _ucid;
        private static int _counter;

        public YandexEngine() : base(TranslatorSetting.Engine.Yandex) { }

        public override bool IsConfigured(TranslatorSetting s) => true;   // IAM token 选填

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            string key = Mod.CleanKey(s?.YandexKey);
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
    internal sealed class GeminiEngine : EngineBase
    {
        public GeminiEngine() : base(TranslatorSetting.Engine.Gemini) { }

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.GeminiKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Mod.CleanKey(s?.GeminiKey);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            try
            {
                // Gemini 的鉴权走 URL 查询参数 ?key=，模型名也在 URL 里，不在 body 里。
                // 这一档引擎没有模型下拉框（ShowField 的 model 不含 Gemini），发出去的一律是这个别名。
                string model = GeminiApi.DefaultModel;
                string langName = Lang.ForAiPrompt(toLocale);
                int extra;
                string thinking = CustomAi.ThinkingField(Name, null, null, model,
                    s.GetThinking(Name, null).ToString(), out extra);
                string resp = PostLlm(GeminiApi.TranslateUrl(null, model, key),
                    (frag, room) => GeminiApi.Body(text, langName, frag, room), thinking, extra, null);
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
    internal sealed class OpenAiCompatEngine : EngineBase
    {
        public OpenAiCompatEngine(TranslatorSetting.Engine id) : base(id) { }

        public override bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.GetKeyFor(Id)));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string key = Mod.CleanKey(s?.GetKeyFor(Id));
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(text)) return null;
            string model = s.EffectiveModel;
            try
            {
                string langName = Lang.ForAiPrompt(toLocale);
                int extra;
                string thinking = CustomAi.ThinkingField(Name, null, null, model,
                    s.GetThinking(Name, null).ToString(), out extra);
                string resp = PostLlm(OpenAiChat.Url(EngineKit.HostedBase(Name), null),
                    (frag, room) => OpenAiChat.Body(model, text, langName, frag), thinking, extra,
                    wc => wc.Headers.Add("Authorization", "Bearer " + key));    // 反馈2/3/4：服务端要生成完才回，8/10 秒的通用档位会把整批请求全判成超时
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
            => !string.IsNullOrEmpty(Mod.CleanKey(s?.CloudflareAccountId)) && !string.IsNullOrEmpty(Mod.CleanKey(s?.CloudflareToken));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            string account = Mod.CleanKey(s?.CloudflareAccountId);
            string token = Mod.CleanKey(s?.CloudflareToken);
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
    internal sealed class CustomEngine : EngineBase
    {
        public CustomEngine() : base(TranslatorSetting.Engine.Custom) { }

        public override bool IsConfigured(TranslatorSetting s)
            => s != null && CustomAi.IsVendor(s.CustomVendor.ToString()) && !string.IsNullOrEmpty(Mod.CleanKey(s.CustomKey));

        public override string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (s == null || string.IsNullOrEmpty(text)) return null;
            // 反馈3：先把厂商定下来，再按【这一家】那一格取 key 与接口地址。两样都现读「当前格」的话，
            // 请求在飞的几百毫秒里玩家切了厂商就会凑出半新半旧的组合 —— A 家的 key 打上 B 家的端点，
            // 正是这一次要修的 bug（实测日志里出现过「用第三方 sk- 打 Google 接口拿 401」）。
            string vendor = CustomAi.Normalize(s.CustomVendor.ToString());
            string key = Mod.CleanKey(s.GetVendorKey(vendor));
            if (string.IsNullOrEmpty(key)) return null;
            string baseUrl = Mod.CleanKey(s.GetVendorBaseUrl(vendor));
            // 反馈8：厂商选「自定义」时，接口格式由玩家在选项里挑；其余 8 家的格式是固定的，Protocol() 会忽略
            // 玩家的选择 —— 选了 deepseek 又把格式改成 anthropic，只会得到一个必然失败的请求。
            string protocol = CustomAi.Protocol(vendor, s.CustomProtocol.ToString());
            // 第八轮：思考强度按厂商分格，所以跟着上面那颗 vendor 一起定下来。读「当前格」的话，
            // 请求在飞的几百毫秒里玩家切了厂商，这一档就会跑到别家那一格去。
            // 第九轮：Off 不再等于「一个字段都不发」——该发什么由 CustomAi.ThinkingField 按这一家 + 这个模型名决定。
            string model = s.EffectiveModel;
            string langName = Lang.ForAiPrompt(toLocale);
            int extra;
            string thinking = CustomAi.ThinkingField(nameof(TranslatorSetting.Engine.Custom), vendor, protocol, model,
                s.GetThinking(nameof(TranslatorSetting.Engine.Custom), vendor).ToString(), out extra);
            try
            {
                // gemini 格式的模型名与 key 都在 URL 里、anthropic 走 x-api-key + 版本头、其余一律 Bearer ——
                // 这些差异全在 CustomAi 里按 protocol 分派，这里不再出现任何厂商名特例。
                string url = CustomAi.TranslateUrl(protocol, vendor, baseUrl, model, key);
                string resp = PostLlm(url, (frag, room) => CustomAi.BodyFor(protocol, model, text, langName, frag, room),
                    thinking, extra, wc =>
                {
                    wc.Headers.Add(CustomAi.AuthHeaderNameFor(protocol), CustomAi.AuthHeaderValueFor(protocol, key));
                    if (protocol == CustomAi.ProtocolAnthropic) wc.Headers.Add("anthropic-version", AnthropicApi.Version);
                });    // 反馈3：DeepSeek/kira 这类网关生成一句就要十几秒，10 秒档位必然全军覆没
                string r = CustomAi.ParseFor(protocol, resp);
                // 需求2：平台报了 usage 就用真值，没报就按字数本地估算（所以界面文案写了「与实际消耗量可能会有出入」）。
                TranslatorSetting.AddTokens(vendor, Tokens.Charge(
                    CustomAi.UsageFor(protocol, resp), AiPrompt.System(langName), AiPrompt.User(text), r));
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

        public static void Invalidate() { _at = -1f; Get(); }

        public static string[] Get()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_ids.Length > 0 && now - _at < 5f) return _ids;
            _at = now;
            try
            {
                PropertyInfo pi = typeof(ModSetting).GetProperty("instances", BindingFlags.NonPublic | BindingFlags.Static);
                if (pi?.GetValue(null) is IDictionary dict)
                {
                    var list = new List<string>(dict.Count);
                    foreach (var k in dict.Keys)
                    {
                        string s = k as string;
                        if (!string.IsNullOrEmpty(s) && s.IndexOf("Cs2AutoTranslator", StringComparison.Ordinal) < 0)
                            list.Add(s);
                    }
                    _ids = list.ToArray();
                }
            }
            catch { /* 反射失败：退化为「无法判别模组」，Options.* 一律归游戏本体（保守，不误翻本体设置）*/ }
            return _ids;
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
                File.AppendAllText(FilePath,
                    "\n=== session start " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " ===\n",
                    Encoding.UTF8);
                PruneOldLogs(); // 需求：日志只保留最近保存的 3 个，更旧的自动清理。
            }
            catch { }
        }

        // 只保留最近保存的 3 个日志文件（当前 .log + 最近的 .log.bak 归档），按修改时间倒序，删掉更旧的。
        // 严格限定：文件名以 Cs2AutoTranslator 开头、以 .log 或 .log.bak 结尾 —— 绝不触碰 dll/cache/json。全程吞异常。
        private const int KeepLogs = 3;
        private static void PruneOldLogs()
        {
            try
            {
                string dir = Path.GetDirectoryName(FilePath);
                if (string.IsNullOrEmpty(dir)) return;
                var logs = new List<FileInfo>();
                foreach (string f in Directory.GetFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    if (!name.StartsWith("Cs2AutoTranslator", StringComparison.Ordinal)) continue;
                    if (!(name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                          name.EndsWith(".log.bak", StringComparison.OrdinalIgnoreCase))) continue;
                    try { logs.Add(new FileInfo(f)); } catch { }
                }
                if (logs.Count <= KeepLogs) return;
                logs.Sort((a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc)); // 新→旧
                for (int i = KeepLogs; i < logs.Count; i++)
                    try { logs[i].Delete(); } catch { }
            }
            catch { }
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
