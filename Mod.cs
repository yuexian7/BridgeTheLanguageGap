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
            ModLog.Info("=== 拯救语言不通（都市天际线2 自动翻译）已载入 (v0.30：模组选项范围改用注入的已注册模组 id 列表；空格分隔键位串与纯 {VALUE} 占位符不再送翻；残留私有区哨兵的译文判废并还原原文；去掉框架 .coc 双存储；删掉 BepInEx 旧缓存搬迁；新增离线回归测试) ===");
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
            _worker = new Thread(WorkerLoop) { IsBackground = true, Name = "Cs2AutoTranslatorWorker" };
            _worker.Start();
            ModLog.Info("[线程] 后台工作线程已启动。");
        }

        private static void WorkerLoop()
        {
            try
            {
                // 1) 等本地化管理器就绪。
                LocalizationManager lm = WaitForLocalizationManager();
                if (lm == null) { ModLog.Error("[线程] 超时：没等到本地化管理器。"); return; }

                // 2) 在主线程注册官方选项界面（RegisterInOptionsUI 必须在主线程）。
                MainThreadDispatcher.RunOnMainThread(() =>
                {
                    try { TranslatorSetting.Register(Instance); ModLog.Info("[选项] 已在主线程注册官方选项界面（12 语言跟随游戏语言）。"); }
                    catch (Exception ex) { ModLog.Error("[选项] 注册异常: " + ex); }
                });

                // 3) 等选项注册完成 + 字典有内容。
                if (!WaitFor(() => TranslatorSetting.Instance != null && (lm.activeDictionary?.entryCount ?? 0) > 100, 120))
                { ModLog.Error("[线程] 超时：选项界面或本地化字典未就绪。"); return; }

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
                    TranslatorSetting.OnClearCacheClicked += ClearCacheNow;
                    TranslatorSetting.OnOpenLogFolderClicked += OpenLogFolderNow;
                    Patches.Apply();
                    // 注册「补丁守护」updater：每帧节流检查，被别的全局 UnpatchAll 拆掉就自动重打 + 重刷界面 + 周期存盘。
                    // 用游戏自己的 MainThreadDispatcher（非 Harmony），不受 UnpatchAll 影响。
                    _updaterGuid = MainThreadDispatcher.RegisterUpdater((Func<bool>)Patches.EnsureApplied);
                });

                // 6) 进入常驻的「按需翻译」循环。
                ModLog.Info("[懒翻译] 后台翻译循环启动：玩家实际看到的、勾选范围内的、非目标语言文字会被逐条自动翻译，实时生效、无需重启。");
                TranslateLoop();
            }
            catch (Exception ex) { ModLog.Error("[线程] 未处理异常: " + ex); }
        }

        // 常驻循环：消费 Prefix 入队的「源文本」，自动检测源语言→翻成目标语言，填进 TransMap + 磁盘缓存。
        private static void TranslateLoop()
        {
            int failures = 0; // 连续失败计数：用于退避，避免一次网络抖动把文字永久判死（需求9d）。
            while (!_shutdown)
            {
                // v0.28：落盘全部由本后台线程做【增量追加】（攒够一批或最晚 5 秒一次），主线程完全不碰缓存磁盘 I/O。
                TranslationCache.Flush(false);
                string src = TransMap.Take(500);
                if (src == null) { RefreshUi(true); continue; } // 队列空闲：把还没刷出去的译文一次性刷到界面。

                TranslatorSetting setting = TranslatorSetting.Instance;
                if (setting == null) { TransMap.Requeue(src); Thread.Sleep(500); continue; }

                string target = setting.TargetLocale;
                ITranslationEngine engine = TranslationEngines.Get(setting.TranslationEngine);
                if (!engine.IsConfigured(setting))
                {
                    // 没填 key（谷歌免 key 除外）：不显示待翻译标记（免得永远挂着省略号），退回队列等用户填好。
                    Patches.MarkerEnabled = false;
                    TransMap.Requeue(src);
                    Thread.Sleep(1500);
                    continue;
                }
                Patches.MarkerEnabled = true; // 引擎可用：待翻译文字显示「…」标记，翻好后原地消失。

                try
                {
                    // 命中磁盘缓存：不再联网。缓存里 value==src 表示「已知无需翻译」。
                    string cached = TranslationCache.Get(engine.CacheName, src, target);
                    if (cached != null)
                    {
                        if (cached == src) TransMap.ResolveAsSkip(src);
                        else TransMap.SetTranslation(src, cached);
                        failures = 0;
                        _uiDirty = true;
                        RefreshUi(false);
                        continue;
                    }

                    // 联网翻译：源语言一律 auto（自动检测），目标语言=用户设置。
                    // 需求5：送引擎前把 {占位符} 掩成哨兵，尽量让引擎别翻花括号内部；回来后还原。
                    string[] guardTokens;
                    string masked = TransGuard.Mask(src, out guardTokens);
                    string raw = engine.Translate(setting, masked, "auto", target);
                    string trans = TransGuard.Unmask(raw, guardTokens);

                    // 翻译途中用户可能改了目标语言：结果作废，退回队列按新目标重翻。
                    if (TranslatorSetting.Instance?.TargetLocale != target) { TransMap.Requeue(src); continue; }

                    if (string.IsNullOrEmpty(trans))
                    {
                        // 引擎无返回（多半是网络/限流/key 问题）：不确定结论，退避后退回队列重试，
                        // 绝不永久判成「无需翻译」（需求9d：避免一次 401/429 把文字永久判死）。
                        failures++;
                        TransMap.Requeue(src);
                        int backoff = Math.Min(30000, 500 * failures);
                        Thread.Sleep(backoff);
                        continue;
                    }

                    failures = 0;

                    // 需求5：完整性校验。译文若破坏了占位符（把 {token} 翻成中文、整个吞掉、或凭空增删花括号），
                    // 判为不安全 → 还原原文（落回下方 trans==src 分支：跳过 + 缓存 identity），绝不让坏译文上屏。
                    // 注：不再校验数字——机翻把数字与词互转（1,000↔千、双↔2）多为正常，旧数字校验会误杀正文。
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
                    _uiDirty = true;    // 无论翻成还是判定跳过，都要刷新界面（去掉「…」标记或换上译文）。
                    RefreshUi(false);   // 节流：突发翻译时最多每 1.5 秒原地刷新一次可见文字。

                    Thread.Sleep(120); // 轻微节流，别刷爆免费额度/被限流。
                }
                catch (Exception ex)
                {
                    // 异常同样按「不确定」处理：退避后退回队列重试，不永久判死（需求9d）。
                    ModLog.Error("[懒翻译] 处理异常（稍后重试该条）: " + ex.Message);
                    failures++;
                    TransMap.Requeue(src);
                    Thread.Sleep(Math.Min(30000, 500 * failures));
                }
            }
            TranslationCache.Flush(true);   // 正常退出循环：把攒下的增量条目强制写干净。
            ModLog.Info("[懒翻译] 后台循环退出。");
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
        private static void RefreshUi(bool force)
        {
            if (!_uiDirty) return;
            int now = Environment.TickCount;
            if (!force && now - _lastRefreshTick < 1500) return;
            _uiDirty = false;
            _lastRefreshTick = now;
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
            TranslatorSetting.LastTestResult = $"正在测试「{engine.DisplayName}」服务连通性…";
            ModLog.Info("[测试] 用户点击测试按钮（只测连通性），引擎=" + engine.DisplayName);
            new Thread(() =>
            {
                string result;
                try
                {
                    if (!engine.IsConfigured(setting))
                        result = $"✗ 「{engine.DisplayName}」未填 key（谷歌免 key 除外），无法测试。";
                    else
                    {
                        // 固定探针，目标语言=用户设置：验证服务连通的同时，回显能反映目标语言（如日语→こんにちは世界）。
                        const string probe = "Hello, world";
                        string trans = engine.Translate(setting, probe, "auto", setting.TargetLocale);
                        result = string.IsNullOrEmpty(trans)
                            ? $"✗ 「{engine.DisplayName}」服务不可达（检查网络/VPN/key 是否有效）。详见日志。"
                            : $"✓ 「{engine.DisplayName}」服务连通正常（'{probe}' → '{trans}'）。";
                    }
                }
                catch (Exception ex) { result = "✗ 测试异常: " + ex.Message; }
                TranslatorSetting.LastTestResult = result;
                ModLog.Info("[测试] " + result);
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
            }
            catch (Exception ex) { ModLog.Error("[保存] 应用异常: " + ex.Message); }
        }

        // 只把当前选项写盘 + 同步到热路径静态字段，不碰运行时译文表、不刷新界面（需求5：测试连通性用它，
        // 免得「测试」按钮顺手把全局译文清空重翻）。返回是否成功写盘。
        private static bool PersistSettingsOnly()
        {
            TranslatorSetting setting = TranslatorSetting.Instance;
            if (setting == null) { ModLog.Warn("[保存] 选项尚未注册，无法保存。"); return false; }
            try
            {
                setting.MicrosoftKey = CleanKey(setting.MicrosoftKey);
                setting.DeepLKey = CleanKey(setting.DeepLKey);
                setting.BaiduAppId = CleanKey(setting.BaiduAppId);
                setting.BaiduKey = CleanKey(setting.BaiduKey);

                bool ok = SettingsStore.Save(setting);
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

        // 「清除缓存」按钮回调（需求10，主线程）：清空磁盘+内存译文缓存，并把运行时译文表清空 → 全部回原文。
        // 绝不触碰任何设置（Enabled/范围/key/目标语言/引擎保持不变）——这不是重置设置。
        private static void ClearCacheNow()
        {
            try
            {
                TranslationCache.Clear();        // 清内存 Dict + 删 translation.cache 文件（含 "self" 命名空间）
                TransMap.Reset();                // 清运行时译文 → Prefix 全部放行原文
                Patches.Paused = true;           // 需求2：暂停按需翻译，让清除后的界面「停在原文」，不会立刻被重新填满
                _uiDirty = true; RefreshUi(true); // 立即重渲染：可见文字回到原文
                ModLog.Info("[缓存] 用户点击「清除缓存」：已清空所有翻译痕迹并暂停自动翻译，界面保持原文（设置未改动）。" +
                            "想重新翻译请点「保存设置并翻译」或重开「启用翻译」开关。");
            }
            catch (Exception ex) { ModLog.Error("[缓存] 清除失败: " + ex.Message); }
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

        // Application.quitting 回调（静态方法：不碰可能已被销毁的 MonoBehaviour 成员）。Unity 在主线程触发。
        private static void OnUnityQuitting()
        {
            _quittingSeen = true;   // 先置位：即使 BeginShutdown 已被其它路径抢先返回，拆除标记也可靠。
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
                EngineCacheName = TranslationEngines.Get(s.TranslationEngine).CacheName;
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
                Type optType = GetOptionsType();
                if (optType == null) return;

                Type worldType = typeof(Unity.Entities.World);
                object world = worldType.GetProperty("DefaultGameObjectInjectionWorld", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (world == null) return;

                MethodInfo getSys = worldType.GetMethod("GetExistingSystemManaged", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(Type) }, null);
                object sys = getSys?.Invoke(world, new object[] { optType });
                if (sys == null) return;

                if (_optionsRefreshMi == null)
                    _optionsRefreshMi = optType.GetMethod("RefreshPage", BindingFlags.Instance | BindingFlags.NonPublic);
                _optionsRefreshMi?.Invoke(sys, null);
            }
            catch { }
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
                if (_harmony == null) _harmony = new Harmony(HarmonyId);
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

                TransMap.Enqueue(src);                                  // 入队后台翻译；先返回原文，翻好后靠标签自然重渲染刷新
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
        private static readonly BlockingCollection<string> Pending = new BlockingCollection<string>(new ConcurrentQueue<string>());

        public static bool TryGetTranslated(string src, out string tr) => Translated.TryGetValue(src, out tr);

        public static bool IsResolved(string src) => Resolved.ContainsKey(src) || Translated.ContainsKey(src);

        public static void Enqueue(string src)
        {
            if (string.IsNullOrEmpty(src)) return;
            if (Translated.ContainsKey(src) || Resolved.ContainsKey(src)) return;
            if (Queued.TryAdd(src, 0)) { try { Pending.Add(src); } catch { /* 已 CompleteAdding */ } }
        }

        public static string Take(int msTimeout)
        {
            try { return Pending.TryTake(out string s, msTimeout) ? s : null; }
            catch { return null; }
        }

        public static void Requeue(string src)
        {
            if (string.IsNullOrEmpty(src)) return;
            try { Pending.Add(src); } catch { }
        }

        public static void SetTranslation(string src, string tr)
        {
            Translated[src] = tr;
            Queued.TryRemove(src, out _);
            Resolved.TryRemove(src, out _);
        }

        public static void ResolveAsSkip(string src)
        {
            Resolved[src] = 0;
            Queued.TryRemove(src, out _);
            Translated.TryRemove(src, out _);
        }

        // 目标语言/引擎变更后清空内存译文，按新设置重新解析（磁盘缓存命中即时恢复）。
        public static void Reset()
        {
            Translated.Clear();
            Resolved.Clear();
            Queued.Clear();
            while (Pending.TryTake(out _)) { }
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

        public static void Load(TranslatorSetting s)
        {
            try
            {
                if (!File.Exists(FilePath)) { ModLog.Info("[配置] 未找到配置文件，使用默认值：" + FilePath); return; }
                string json = File.ReadAllText(FilePath, Encoding.UTF8);
                if (Enum.TryParse<TranslatorSetting.Engine>(Json.ReadString(json, "TranslationEngine"), out var eng)) s.TranslationEngine = eng;
                // 需求2：旧默认引擎「谷歌免密钥网页接口」已失效（中国大陆挂 VPN 仍 429）→ 自动改回微软，并提示用户确认保存。
                if (s.TranslationEngine == TranslatorSetting.Engine.Google)
                {
                    s.TranslationEngine = TranslatorSetting.Engine.Microsoft;
                    ModLog.Info("[配置] 旧默认引擎「谷歌免密钥网页接口」已失效，已自动改为「微软 Azure」。请在选项里确认引擎、填好 key 后点「保存设置并翻译」。");
                }
                string tl = Json.ReadString(json, "TargetLocale"); if (!string.IsNullOrEmpty(tl)) s.TargetLocale = tl;
                s.Enabled = Json.ReadBool(json, "Enabled", s.Enabled);
                s.ScopeModOptions = Json.ReadBool(json, "ScopeModOptions", s.ScopeModOptions);
                s.ScopeAssetNames = Json.ReadBool(json, "ScopeAssetNames", s.ScopeAssetNames);
                s.ScopeAssetDescriptions = Json.ReadBool(json, "ScopeAssetDescriptions", s.ScopeAssetDescriptions);
                s.ScopeGameCore = Json.ReadBool(json, "ScopeGameCore", s.ScopeGameCore);
                s.ScopeModName = Json.ReadBool(json, "ScopeModName", s.ScopeModName);
                s.ScopeWorldLabels = Json.ReadBool(json, "ScopeWorldLabels", s.ScopeWorldLabels);
                s.MicrosoftKey = Mod.CleanKey(Json.ReadString(json, "MicrosoftKey")) ?? string.Empty;
                s.MicrosoftRegion = Mod.CleanKey(Json.ReadString(json, "MicrosoftRegion")) ?? string.Empty;
                s.DeepLKey = Mod.CleanKey(Json.ReadString(json, "DeepLKey")) ?? string.Empty;
                s.BaiduAppId = Mod.CleanKey(Json.ReadString(json, "BaiduAppId")) ?? string.Empty;
                s.BaiduKey = Mod.CleanKey(Json.ReadString(json, "BaiduKey")) ?? string.Empty;
                ModLog.Info($"[配置] 已从自管 JSON 载入：启用={s.Enabled} 引擎={s.TranslationEngine} 目标语言={s.TargetLocale} 微软key长度={s.MicrosoftKey.Length}。路径={FilePath}");
            }
            catch (Exception ex) { ModLog.Error("[配置] 载入失败（用默认值）: " + ex.Message); }
        }

        public static bool Save(TranslatorSetting s)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                var sb = new StringBuilder();
                sb.Append("{\n");
                sb.Append("  \"TranslationEngine\": \"").Append(Json.Escape(s.TranslationEngine.ToString())).Append("\",\n");
                sb.Append("  \"TargetLocale\": \"").Append(Json.Escape(s.TargetLocale)).Append("\",\n");
                sb.Append("  \"Enabled\": ").Append(s.Enabled ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeModOptions\": ").Append(s.ScopeModOptions ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeAssetNames\": ").Append(s.ScopeAssetNames ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeAssetDescriptions\": ").Append(s.ScopeAssetDescriptions ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeGameCore\": ").Append(s.ScopeGameCore ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeModName\": ").Append(s.ScopeModName ? "true" : "false").Append(",\n");
                sb.Append("  \"ScopeWorldLabels\": ").Append(s.ScopeWorldLabels ? "true" : "false").Append(",\n");
                sb.Append("  \"MicrosoftKey\": \"").Append(Json.Escape(s.MicrosoftKey)).Append("\",\n");
                sb.Append("  \"MicrosoftRegion\": \"").Append(Json.Escape(s.MicrosoftRegion)).Append("\",\n");
                sb.Append("  \"DeepLKey\": \"").Append(Json.Escape(s.DeepLKey)).Append("\",\n");
                sb.Append("  \"BaiduAppId\": \"").Append(Json.Escape(s.BaiduAppId)).Append("\",\n");
                sb.Append("  \"BaiduKey\": \"").Append(Json.Escape(s.BaiduKey)).Append("\"\n");
                sb.Append("}\n");
                File.WriteAllText(FilePath, sb.ToString(), Encoding.UTF8);
                bool ok = File.Exists(FilePath);
                ModLog.Info(ok ? "[配置] 已写入自管 JSON。" : "[配置] 写入后仍未找到文件。");
                return ok;
            }
            catch (Exception ex) { ModLog.Error("[配置] 保存失败: " + ex.Message); return false; }
        }
    }

    // ===== 翻译引擎抽象 =====
    internal interface ITranslationEngine
    {
        string DisplayName { get; }
        string CacheName { get; }
        bool IsConfigured(TranslatorSetting s);
        string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale);
    }

    internal static class TranslationEngines
    {
        public static ITranslationEngine Get(TranslatorSetting.Engine e)
        {
            switch (e)
            {
                case TranslatorSetting.Engine.DeepL: return new DeepLEngine();
                case TranslatorSetting.Engine.Baidu: return new BaiduEngine();
                case TranslatorSetting.Engine.Google: return new GoogleEngine();
                case TranslatorSetting.Engine.Microsoft:
                default: return new MicrosoftEngine();
            }
        }
    }

    internal static class Http
    {
        // v0.28（修「退出到桌面卡死无响应」）：
        // 裸 WebClient 用的是 .NET 默认超时——Timeout 100 秒、ReadWriteTimeout 300 秒。后台翻译线程一旦堵在
        // 原生 socket 读上，Unity 退出时的 Thread.Abort 就无法投递（只能等 TCP 超时），游戏表现为点了退出却没反应。
        // 对策：① 把超时收紧到 8/10 秒；② 登记所有存活客户端，退出时主动 Abort 在飞请求，让线程立刻回到托管点。
        private static readonly ConcurrentDictionary<TimeoutWebClient, byte> Live = new ConcurrentDictionary<TimeoutWebClient, byte>();

        public static WebClient NewClient()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var wc = new TimeoutWebClient { Encoding = Encoding.UTF8 };
            Live[wc] = 0;
            return wc;
        }

        // 退出收口调用：中止全部在飞请求（幂等，可反复调）。
        public static void AbortActive()
        {
            foreach (TimeoutWebClient wc in Live.Keys)
            {
                try { wc.AbortRequest(); } catch { }
            }
        }

        private sealed class TimeoutWebClient : WebClient
        {
            private volatile HttpWebRequest _current;

            protected override WebRequest GetWebRequest(Uri address)
            {
                HttpWebRequest req = base.GetWebRequest(address) as HttpWebRequest;
                if (req != null)
                {
                    req.Timeout = 8000;           // 连接 + 发送 + 等响应头
                    req.ReadWriteTimeout = 10000; // 单次读/写
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

    internal sealed class MicrosoftEngine : ITranslationEngine
    {
        public string DisplayName => "微软 Azure 翻译器";
        public string CacheName => "ms";
        public bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.MicrosoftKey));

        public string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                // 不带 from → 微软自动检测源语言（满足「所有非目标语言→目标语言」）。
                string url = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Uri.EscapeDataString(LangMap.Microsoft(toLocale));
                string body = "[{\"Text\":\"" + Json.Escape(text) + "\"}]";
                using (WebClient wc = Http.NewClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/json";
                    wc.Headers.Add("Ocp-Apim-Subscription-Key", Mod.CleanKey(s.MicrosoftKey));
                    string region = Mod.CleanKey(s.MicrosoftRegion);
                    if (!string.IsNullOrEmpty(region)) wc.Headers.Add("Ocp-Apim-Subscription-Region", region); // 需求2：区域级资源必填，否则 401。
                    wc.Headers.Add(HttpRequestHeader.UserAgent, "Cs2AutoTranslator/0.20");
                    string resp = wc.UploadString(url, "POST", body);
                    return Json.ExtractValue(resp, "\"text\":\"");
                }
            }
            catch (Exception ex) { ModLog.Error("[微软翻译] 请求失败: " + ex.Message); return null; }
        }
    }

    internal sealed class DeepLEngine : ITranslationEngine
    {
        public string DisplayName => "DeepL 翻译器";
        public string CacheName => "deepl";
        public bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.DeepLKey));

        public string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                string key = Mod.CleanKey(s.DeepLKey);
                // 免费 key 以 ":fx" 结尾，走 api-free；否则走 api（Pro）。不带 source_lang → 自动检测。
                string host = key.EndsWith(":fx", StringComparison.Ordinal) ? "api-free.deepl.com" : "api.deepl.com";
                string url = "https://" + host + "/v2/translate";
                string form = "text=" + Uri.EscapeDataString(text) + "&target_lang=" + Uri.EscapeDataString(LangMap.DeepL(toLocale));
                using (WebClient wc = Http.NewClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    wc.Headers.Add("Authorization", "DeepL-Auth-Key " + key);
                    string resp = wc.UploadString(url, "POST", form);
                    return Json.ExtractValue(resp, "\"text\":\"");
                }
            }
            catch (Exception ex) { ModLog.Error("[DeepL翻译] 请求失败: " + ex.Message); return null; }
        }
    }

    internal sealed class BaiduEngine : ITranslationEngine
    {
        private static readonly System.Random Rnd = new System.Random();
        public string DisplayName => "百度翻译开放平台";
        public string CacheName => "baidu";
        public bool IsConfigured(TranslatorSetting s) => !string.IsNullOrEmpty(Mod.CleanKey(s?.BaiduAppId)) && !string.IsNullOrEmpty(Mod.CleanKey(s?.BaiduKey));

        public string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                string appid = Mod.CleanKey(s.BaiduAppId);
                string key = Mod.CleanKey(s.BaiduKey);
                string salt;
                lock (Rnd) salt = Rnd.Next(100000, 999999).ToString(CultureInfo.InvariantCulture);
                string sign = Md5Hex(appid + text + salt + key);
                string form = "q=" + Uri.EscapeDataString(text)
                    + "&from=auto&to=" + Uri.EscapeDataString(LangMap.Baidu(toLocale))
                    + "&appid=" + Uri.EscapeDataString(appid)
                    + "&salt=" + Uri.EscapeDataString(salt)
                    + "&sign=" + Uri.EscapeDataString(sign);
                using (WebClient wc = Http.NewClient())
                {
                    wc.Headers[HttpRequestHeader.ContentType] = "application/x-www-form-urlencoded";
                    string resp = wc.UploadString("https://fanyi-api.baidu.com/api/trans/vip/translate", "POST", form);
                    return Json.ExtractValue(resp, "\"dst\":\"");
                }
            }
            catch (Exception ex) { ModLog.Error("[百度翻译] 请求失败: " + ex.Message); return null; }
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

    internal sealed class GoogleEngine : ITranslationEngine
    {
        public string DisplayName => "谷歌翻译(免密钥·网页接口)";
        public string CacheName => "google";
        public bool IsConfigured(TranslatorSetting s) => true; // 免 key

        public string Translate(TranslatorSetting s, string text, string fromLocale, string toLocale)
        {
            if (string.IsNullOrEmpty(text)) return null;
            try
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl="
                    + Uri.EscapeDataString(LangMap.Google(toLocale)) + "&dt=t&q=" + Uri.EscapeDataString(text);
                using (WebClient wc = Http.NewClient())
                {
                    wc.Headers.Add(HttpRequestHeader.UserAgent, "Mozilla/5.0 (compatible; Cs2AutoTranslator/0.20)");
                    string resp = wc.DownloadString(url);
                    return Json.ParseFirstJsonString(resp);
                }
            }
            catch (Exception ex) { ModLog.Error("[谷歌翻译] 请求失败: " + ex.Message); return null; }
        }
    }

    internal static class LangMap
    {
        private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

        public static string Microsoft(string locale)
        {
            switch (Norm(locale))
            {
                case "zh-hans": case "zh-cn": return "zh-Hans";
                case "zh-hant": case "zh-tw": return "zh-Hant";
                case "en-us": case "en": return "en";
                case "ja-jp": case "ja": return "ja";
                case "ko-kr": case "ko": return "ko";
                case "fr-fr": case "fr": return "fr";
                case "de-de": case "de": return "de";
                case "es-es": case "es": return "es";
                case "it-it": case "it": return "it";
                case "pl-pl": case "pl": return "pl";
                case "pt-br": case "pt": return "pt-br";
                case "ru-ru": case "ru": return "ru";
                default: return locale;
            }
        }

        public static string DeepL(string locale)
        {
            switch (Norm(locale))
            {
                case "zh-hans": case "zh-cn": case "zh-hant": case "zh-tw": return "ZH";
                case "en-us": case "en": case "en-gb": return "EN";
                case "ja-jp": case "ja": return "JA";
                case "ko-kr": case "ko": return "KO";
                case "fr-fr": case "fr": return "FR";
                case "de-de": case "de": return "DE";
                case "es-es": case "es": return "ES";
                case "it-it": case "it": return "IT";
                case "pl-pl": case "pl": return "PL";
                case "pt-br": case "pt": return "PT-BR";
                case "ru-ru": case "ru": return "RU";
                default: return "EN";
            }
        }

        public static string Baidu(string locale)
        {
            switch (Norm(locale))
            {
                case "zh-hans": case "zh-cn": return "zh";
                case "zh-hant": case "zh-tw": return "cht";
                case "en-us": case "en": return "en";
                case "ja-jp": case "ja": return "jp";
                case "ko-kr": case "ko": return "kor";
                case "fr-fr": case "fr": return "fra";
                case "de-de": case "de": return "de";
                case "es-es": case "es": return "es";
                case "it-it": case "it": return "it";
                case "pl-pl": case "pl": return "pl";
                case "pt-br": case "pt": return "pt";
                case "ru-ru": case "ru": return "ru";
                default: return "en";
            }
        }

        public static string Google(string locale)
        {
            switch (Norm(locale))
            {
                case "zh-hans": case "zh-cn": return "zh-CN";
                case "zh-hant": case "zh-tw": return "zh-TW";
                case "en-us": case "en-gb": case "en": return "en";
                case "ja-jp": case "ja": return "ja";
                case "ko-kr": case "ko": return "ko";
                case "pt-br": return "pt";
                case "he": case "iw": return "iw";   // 谷歌网页接口用旧码 iw 表示希伯来语
                case "no": case "nb": return "no";
                // 其余（nl/sv/th/vi/hi/ar/fa/tr/cs/hu/ro/bg/hr/sr/...）：谷歌 gtx 接口直接吃 ISO 码，
                // 去掉可能的地区后缀、小写后透传即可（需求2：支持约 50 种语言）。
                default:
                {
                    string s = Norm(locale);
                    int dash = s.IndexOf('-');
                    if (dash > 0) s = s.Substring(0, dash);
                    return string.IsNullOrEmpty(s) ? "en" : s;
                }
            }
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
