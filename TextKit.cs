using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Cs2AutoTranslator
{
    // ===== 纯字符串工具层（不碰 Unity、不碰游戏程序集、不碰磁盘与网络）=====
    // 为什么单独一个文件：这四件事全是纯函数，而 Mod.cs 里几乎每个类型都在声明处引用了游戏类型
    // （class Mod : IMod、TranslatorSetting : ModSetting、SettingsLocale : IDictionarySource）。
    // CLR 在【加载类型】时就要解析基类型，所以哪怕方法体 100% 纯，只要它还在 Mod.cs 里就离线调不到。
    // 搬出来之后 tests\t3 那个 net48 控制台壳可以只编译 Scope.cs + 本文件，一个 <Reference> 都不填，
    // 从结构上保证「不启动游戏就能测」，也保证不可能误触真实 Unity 代码。
    // 回归断言源：D:\qoder md\BridgeTheLanguageGap\tests\fixtures\translate-fixtures.txt

    // ===== 占位符保护（需求5）=====
    // 机翻有时会把 {VALUE} 之类占位符翻成中文、或凭空加花括号（实证："2026年9月"→"{年}年{月}月"、
    // "{SIGN}{VALUE}%"→"{符号}{价值}%"）。策略：① 送引擎前把 {...} 掩成私有区哨兵，尽量让引擎别动它；
    // ② 回来后还原；③ 完整性校验：原文每个 {...} 在译文里逐字存在 + 译文没凭空多花括号 + 没多出还原不了的哨兵。
    // 任一不满足即判不安全，调用方还原原文。best-effort：宁可漏翻也不上屏坏译文。
    // 注：数字与词互转（1,000↔千、双↔2）是正常译文，不再按数字判定安全。
    internal static class TransGuard
    {
        private const char SentinelStart = '\uE000';
        private const char SentinelEnd = '\uE001';

        // 把每个「单行、无换行、长度合理」的 {...} 替换成哨兵 \uE000{i}\uE001（i=token 序号）。
        public static string Mask(string src, out string[] tokens)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(src)) { tokens = list.ToArray(); return src; }
            var sb = new StringBuilder(src.Length);
            for (int i = 0; i < src.Length; i++)
            {
                if (src[i] == '{')
                {
                    int close = src.IndexOf('}', i + 1);
                    if (close > i && close - i <= 64 && src.IndexOf('\n', i, close - i) < 0)
                    {
                        sb.Append(SentinelStart).Append(list.Count.ToString(CultureInfo.InvariantCulture)).Append(SentinelEnd);
                        list.Add(src.Substring(i, close - i + 1));
                        i = close;
                        continue;
                    }
                }
                sb.Append(src[i]);
            }
            tokens = list.ToArray();
            return sb.ToString();
        }

        // 把哨兵还原成原 token（引擎一般原样透传私有区字符；还原不了的保持原样，交给 IsSafe 兜底判废）。
        public static string Unmask(string masked, string[] tokens)
        {
            if (string.IsNullOrEmpty(masked) || tokens == null || tokens.Length == 0) return masked;
            var sb = new StringBuilder(masked.Length);
            for (int i = 0; i < masked.Length; i++)
            {
                if (masked[i] == SentinelStart)
                {
                    int end = masked.IndexOf(SentinelEnd, i + 1);
                    if (end > i && end - i <= 12 &&
                        int.TryParse(masked.Substring(i + 1, end - i - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int idx) &&
                        idx >= 0 && idx < tokens.Length)
                    {
                        sb.Append(tokens[idx]);
                        i = end;
                        continue;
                    }
                }
                sb.Append(masked[i]);
            }
            return sb.ToString();
        }

        public static bool IsSafe(string src, string trans)
        {
            if (string.IsNullOrEmpty(trans)) return false;
            if (string.IsNullOrEmpty(src)) return true;

            // 需求5：只校验【占位符完整性】，不再校验数字（旧①数字多重集已删）。
            //   原因：机翻常把数字与词互相转换且是正确的（每晚→1晩、双车道→2車線、每 1,000→每千），
            //   数字多重集相等判定会把这些正常译文误杀还原原文（日志实证 43 条 [校正]）。
            //   真正危险的占位符损坏由下面的 ①②③ 兜住，与数字无关。

            // ① 原文里每个 {...} token 必须在译文里逐字存在（引擎不得把 {SIGN} 翻成 {符号} 或整个吞掉）。
            foreach (string tok in BraceTokens(src))
                if (trans.IndexOf(tok, StringComparison.Ordinal) < 0) return false;

            // ② 译文的花括号数量不得超过原文（引擎不得凭空造 {...}，如把「2026年9月」翻成「{年}年{月}月」）。
            if (CountChar(trans, '{') > CountChar(src, '{')) return false;
            if (CountChar(trans, '}') > CountChar(src, '}')) return false;

            // ③ 译文不得多出原文没有的私有区哨兵：引擎把哨兵吞掉或复制错时，Unmask 还原不了，
            //    残留的 \uE000/\uE001 上屏就是一串方块。按【计数】判定而不是 Contains——调用方给的 src
            //    是【未掩码】的原文，所以我们造的哨兵必然使计数超过；而原文本来就含私有区字符、
            //    译文原样保留时计数相等，仍然判安全（不误杀极少数真有这字符的文本）。
            if (CountChar(trans, SentinelStart) > CountChar(src, SentinelStart)) return false;
            if (CountChar(trans, SentinelEnd) > CountChar(src, SentinelEnd)) return false;

            return true;
        }

        private static List<string> BraceTokens(string s)
        {
            var toks = new List<string>();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '{')
                {
                    int close = s.IndexOf('}', i + 1);
                    if (close > i && close - i <= 64 && s.IndexOf('\n', i, close - i) < 0)
                    { toks.Add(s.Substring(i, close - i + 1)); i = close; }
                }
            }
            return toks;
        }

        private static int CountChar(string s, char c)
        {
            int n = 0;
            foreach (char ch in s) if (ch == c) n++;
            return n;
        }
    }

    // ===== 手写 JSON：转义 / 取值 =====
    // 两处用途：① SettingsStore 自管配置文件的读写；② 从各引擎的 HTTP 响应里抠出译文。
    internal static class Json
    {
        public static string Escape(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }

        public static string ExtractValue(string json, string marker)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int i = json.IndexOf(marker, StringComparison.Ordinal);
            if (i < 0) return null;
            return Unquote(json, i + marker.Length);
        }

        public static string ParseFirstJsonString(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            int start = json.IndexOf('"');
            if (start < 0) return null;
            return Unquote(json, start + 1);
        }

        internal static string Unquote(string json, int p)
        {
            var sb = new StringBuilder();
            for (; p < json.Length; p++)
            {
                char c = json[p];
                if (c == '"') break;
                if (c == '\\' && p + 1 < json.Length)
                {
                    char e = json[++p];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (p + 4 < json.Length && int.TryParse(json.Substring(p + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
                            { sb.Append((char)code); p += 4; }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            string s = sb.ToString();
            return string.IsNullOrEmpty(s) ? null : s;
        }

        // ===== 自管设置文件的取值（原 SettingsStore.GetString / GetBool，搬到这里才能被离线断言覆盖）=====
        // 需求修（reload bug）：Save 写的是 "key": "value"（冒号后带空格），旧版按 "key":" 无空格匹配 → 永远取不到，
        // 重启后 key/目标语言/引擎全丢（且下次保存把空值写回覆盖）。所以定位值时必须跳过冒号后的空白。
        private static bool TryFindValue(string json, string key, out int valueStart)
        {
            valueStart = -1;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return false;
            int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (i < 0) return false;
            int colon = json.IndexOf(':', i);
            if (colon < 0) return false;
            int j = colon + 1;
            while (j < json.Length && (json[j] == ' ' || json[j] == '\t' || json[j] == '\r' || json[j] == '\n')) j++;
            if (j >= json.Length) return false;
            valueStart = j;
            return true;
        }

        // 取字符串项；不存在 / 值不是字符串一律返回 null（与旧 GetString 一致）。
        public static string ReadString(string json, string key)
        {
            if (!TryFindValue(json, key, out int at) || json[at] != '"') return null;
            return Unquote(json, at + 1);
        }

        // 取布尔项；不存在或值不是 true/false 时返回调用方给的默认值（与旧 GetBool 一致）。
        public static bool ReadBool(string json, string key, bool def)
        {
            if (!TryFindValue(json, key, out int at)) return def;
            if (at + 4 <= json.Length && json.Substring(at, 4) == "true") return true;
            if (at + 5 <= json.Length && json.Substring(at, 5) == "false") return false;
            return def;
        }
    }
}
