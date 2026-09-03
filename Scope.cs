using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Game.Modding;

namespace Cs2AutoTranslator
{
    // 翻译「范围」分类 + 本地预筛（需求 1/2/4/11）。
    //
    // 设计要点：
    //  - 分类只看本地化 key 的前缀（Group.SUBKEY[Identifier] 形式），纯字符串比较、零联网、极廉价。
    //  - ShouldSkipLocal 在入队前跑，命中即「无需翻译」：不联网、不显示「…」标记，直接放行原文。
    //    它覆盖三类：① 纯数字/符号；② 快捷键（Ctrl+Enter 这种）；③ 已经是目标语言（同文字系统）。
    //  - 玩家自定义/输入的文字（重命名道路·资产、模组参数文本框、任何输入框）天然没有 key、
    //    不进本地化字典，所以 Prefix 的 dict.TryGetValue 永远拿不到它们 → 结构性地永不被翻译（需求 11）。
    internal static class Scope
    {
        internal enum Category { ModName, ModOptions, AssetName, AssetDesc, WorldLabel, GameCore }

        // ===== key -> 类目 =====
        public static Category Classify(string key)
        {
            if (string.IsNullOrEmpty(key)) return Category.GameCore;

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

            // 模组选项及说明（Options.* / Common.ACTION[...]，且 identifier 属于已注册模组）。
            if (key.StartsWith("Options.", StringComparison.Ordinal) || key.StartsWith("Common.ACTION[", StringComparison.Ordinal))
                return IsModIdentifier(key) ? Category.ModOptions : Category.GameCore;

            // 其余全部：游戏本体。
            return Category.GameCore;
        }

        private static bool StartsWithAny(string s, params string[] prefixes)
        {
            for (int i = 0; i < prefixes.Length; i++)
                if (s.StartsWith(prefixes[i], StringComparison.Ordinal)) return true;
            return false;
        }

        // ===== 模组 identifier 判别（best-effort，反射读取 ModSetting.instances）=====
        private static string[] _modIds = Array.Empty<string>();
        private static float _modIdsAt = -1f;

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
            if (a < 0 || b <= a) return null;
            return key.Substring(a + 1, b - a - 1);
        }

        // 每 5 秒最多刷新一次模组 id 列表（模组在加载期陆续注册，缓存即可；SaveNow 会强制刷新）。
        public static string[] GetModIds()
        {
            float now = UnityEngine.Time.realtimeSinceStartup;
            if (_modIds.Length > 0 && now - _modIdsAt < 5f) return _modIds;
            _modIdsAt = now;
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
                    _modIds = list.ToArray();
                }
            }
            catch { /* 反射失败：退化为「无法判别模组」，Options.* 一律归游戏本体（保守，不误翻本体设置）*/ }
            return _modIds;
        }

        public static void RefreshModIds() { _modIdsAt = -1f; GetModIds(); }

        // ===== 本地预筛：是否「无需翻译」（需求 1 + 4）=====
        // 返回 true = 跳过翻译（ResolveAsSkip、不显示「…」、不联网）。极廉价，热路径可逐条调用。
        public static bool ShouldSkipLocal(string text, string targetLocale, string activeLocale)
        {
            if (string.IsNullOrEmpty(text)) return true;

            // 规则①：纯数字/符号/空白（去掉数字、空白、标点、符号后为空）→ 跳过。
            // 例：15、{符号}{价值}、+、100%、1.5、—— 这些翻译没意义，且常被误翻成占位符。
            bool hasLetterOrIdeo = false;
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c) && !char.IsDigit(c)) { hasLetterOrIdeo = true; break; }
            }
            if (!hasLetterOrIdeo) return true; // 全是数字/标点/符号/空白

            // 规则②：快捷键组合（Ctrl+Enter / Shift + A / W A S D / Mouse0 等）→ 跳过。
            if (LooksLikeKeyBinding(text)) return true;

            // 规则③：文本「书写系统」与目标语言一致 → 已是目标语言，本地跳过、不联网（所有语言通用）。
            //   - 中日韩：汉字/假名/谚文彼此字形正交，纯按字符安全判定（下详），绝不误伤别种语言。
            //   - 拉丁 / 西里尔：同族内部（英↔法、俄↔乌）字形完全相同，无法只凭字符区分语言，
            //     故仅当「当前游戏语言 == 目标语言」时才按同文字跳过——这样英→英秒跳，而英文游戏翻成
            //     法语(目标≠游戏语言)时英文正文仍照翻，不会被误判成"已达标"而漏翻。
            //   - 跨族（拉丁↔西里尔↔中日韩）字形正交，外语文字永不被同族规则误跳，仍照常翻译。
            if (IsAlreadyTargetScript(text, targetLocale, activeLocale)) return true;

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

            // 形如 "A"、"W A S D"、"Q+E" 的极短 ASCII 组合（含分隔符或单字母）。
            if (hasPlusOrCombo) return true;
            string trimmed = text.Trim();
            if (trimmed.Length == 1 && char.IsLetter(trimmed[0])) return true;

            return false;
        }
    }
}
