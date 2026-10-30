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
    // 搬出来之后 tests\t3 那个 net48 控制台壳可以只编译纯 BCL 那几个文件（现状：Scope.cs + 本文件 +
    // EngineKit.cs + LanguageCatalog.cs + ModInfo.cs + DropdownKit.cs），一个 <Reference> 都不填，
    // 从结构上保证「不启动游戏就能测」，也保证不可能误触真实 Unity 代码。
    // 回归断言源：D:\WorkSpace\CS2MOD\BridgeTheLanguageGap\tests\fixtures\translate-fixtures.txt

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

        // ===== 结构化取值（v0.31：13 个新引擎的响应解析全靠这几条）=====
        // 为什么不用上面的 ExtractValue：它只认「第一次出现的字面 marker」，两个坑都会踩——
        //   ① Gemini 的 {"candidates":[{"content":{"parts":[{"text":"…"}]}}]} 里 "content" 的值是对象不是字符串；
        //   ② Anthropic 的 {"content":[{"type":"text","text":"…"}]} 里 "type":"text" 含有字面量 "text"，
        //      按 marker "\"text\":\"" 去撞会命中【值】而不是【键】。
        // 所以下面一律要求 "key" 之后紧跟冒号，且能跳过类型不符的同名 key 继续往后找。
        // 局限（与全项目一致的手写 JSON 取舍）：不解析字符串值内部出现的 "key": 字面量。各引擎响应形状已知，不含此情况。

        private static int SkipWs(string s, int p)
        {
            while (p < s.Length && (s[p] == ' ' || s[p] == '\t' || s[p] == '\r' || s[p] == '\n')) p++;
            return p;
        }

        // 从 from 开始找下一个「"key" 后紧跟冒号」的位置，返回值首字符下标；找不到返回 -1。
        private static int NextValueAt(string json, string key, int from)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return -1;
            string needle = "\"" + key + "\"";
            int at = from < 0 ? 0 : from;
            while (true)
            {
                int hit = json.IndexOf(needle, at, StringComparison.Ordinal);
                if (hit < 0) return -1;
                int p = SkipWs(json, hit + needle.Length);
                if (p < json.Length && json[p] == ':') return SkipWs(json, p + 1);
                at = hit + needle.Length;
            }
        }

        // 第一个「值是字符串」的同名 key 的取值。值是对象/数组/数字的同名 key 被跳过继续找。
        public static string FirstString(string json, string key)
        {
            int from = 0;
            while (true)
            {
                int v = NextValueAt(json, key, from);
                if (v < 0) return null;
                if (v < json.Length && json[v] == '"')
                {
                    string s = Unquote(json, v + 1);
                    if (!string.IsNullOrEmpty(s)) return s;
                }
                from = v + 1;
            }
        }

        // 第一个「值是数组」的同名 key，取数组里第一个字符串。Yandex 的 {"text":["译文"]} 就是这种。
        public static string FirstStringInArray(string json, string key)
        {
            string arr = FindContainer(json, key);
            return arr == null ? null : ParseFirstJsonString(arr);
        }

        // 数组按下标取元素：返回第 index 个元素的【原样子串】（容器带首尾括号，字符串带首尾引号，标量就是那几个字符）。
        // 为什么要新加这个口子：现有取值口全是「按 key 找值」，而谷歌网页版 batchexecute 的响应是
        // 一串【只有位置、没有字段名】的嵌套数组（译文长在 payload[1][0][i][5][j][0]），只能按坐标取。
        //  json 不是数组、下标越界、结构读不动 —— 一律返回 null，交给调用方判失败；
        // 绝不「顺手」从别处捡个字符串冒充译文（与 ConcatGtxSegments 同一条铁律）。
        public static string ArrayElement(string json, int index)
        {
            if (string.IsNullOrEmpty(json) || index < 0) return null;
            int p = 0;
            while (p < json.Length && IsBlank(json[p])) p++;
            if (p >= json.Length || json[p] != '[') return null;
            int i = p + 1, n = 0;
            while (i < json.Length)
            {
                char c = json[i];
                if (c == ']') return null;                                   // 元素就这些，下标越界
                if (c == ',' || IsBlank(c)) { i++; continue; }
                int end = SkipValue(json, i);
                if (end <= i) return null;                                   // 防呆：走不动立刻收工，绝不原地打转
                if (n == index) return json.Substring(i, end - i).Trim();
                n++;
                i = end;
            }
            return null;
        }

        // 数组元素的原始文本是不是一个字符串字面量（首字符是引号）。语言码、译文、rpcid 都要这样认。
        public static bool IsStringLiteral(string elementText)
        {
            return !string.IsNullOrEmpty(elementText) && elementText[0] == '"';
        }

        // 字符串字面量 -> 内容（去掉首尾引号并反转义 \n \uXXXX \" 等）。不是字符串字面量返回 null。
        public static string StringValue(string elementText)
        {
            return IsStringLiteral(elementText) ? Unquote(elementText, 1) : null;
        }

        private static bool IsBlank(char c)
        {
            return c == ' ' || c == '\t' || c == '\r' || c == '\n' || c == '\f' || c == '\v';
        }

        // 第一个「值是 {…} 或 […]」的同名 key，返回含首尾括号的完整子串（括号配对，跳过字符串内的括号与转义）。
        public static string FindContainer(string json, string key)
        {
            int v = NextValueAt(json, key, 0);
            if (v < 0 || v >= json.Length) return null;
            char open = json[v];
            if (open != '{' && open != '[') return null;
            char close = open == '{' ? '}' : ']';
            int depth = 0;
            bool inStr = false;
            for (int i = v; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inStr = false;
                    continue;
                }
                if (c == '"') inStr = true;
                else if (c == open) depth++;
                else if (c == close)
                {
                    depth--;
                    if (depth == 0) return json.Substring(v, i - v + 1);
                }
            }
            return null;
        }

        // 所有「值是字符串」的同名 key，按出现顺序。模型列表用：{"data":[{"id":"a"},{"id":"b"}]}。
        public static List<string> AllStrings(string json, string key)
        {
            var list = new List<string>();
            int from = 0;
            while (true)
            {
                int v = NextValueAt(json, key, from);
                if (v < 0) return list;
                if (v < json.Length && json[v] == '"')
                {
                    string s = Unquote(json, v + 1);
                    if (!string.IsNullOrEmpty(s)) list.Add(s);
                }
                from = v + 1;
            }
        }

        // 取整数值（token 用量）。值不是数字时返回 def。
        public static long ReadLong(string json, string key, long def)
        {
            int v = NextValueAt(json, key, 0);
            if (v < 0 || v >= json.Length) return def;
            int i = v;
            if (json[i] == '-' || json[i] == '+') i++;
            int digitsStart = i;
            while (i < json.Length && json[i] >= '0' && json[i] <= '9') i++;
            if (i == digitsStart) return def;
            long n;
            string num = json.Substring(v, i - v);
            return long.TryParse(num, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : def;
        }

        // 谷歌 gtx（client=gtx|dict-chrome-ex，dt=t）响应：[[["译1","原1",null,null,1],["译2",…]],null,"en",…后面全是杂项…]。
        // 长文本被切成多个分段，必须【全部拼起来】——旧版 GoogleEngine 用 ParseFirstJsonString 只取第一段，
        // 多句文本会被静默截断（v0.31 修）。
        // 第八轮实机修尾缀：以前是「扫全文，凡深度 3 的数组就取它的第一个字符串」，可响应尾部的语言码
        // 与置信度【也长在深度 3 上】（[[…],null,"zh-CN",…,["en",1.0,0.0],["en"]]），于是玩家看到
        // 「Hello, worldenen」这种带尾缀的译文。现在改成按结构取：只认最外层数组的第一个元素 = 分段列表，
        // 列表里每个分段数组的第一项才是译文，遇到列表的收尾 ']' 就停手，后面是什么都不看。
        // 认不出形状时返回 null 让引擎报失败，绝不从别处捡字符串冒充译文（与 BatchKit 的「宁缺毋滥」同一条铁律）。
        public static string ConcatGtxSegments(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            if (json[0] != '[') return null;
            if (json.Length < 2 || json[1] != '[') return null;     // 分段列表必须是响应的第一个元素
            var sb = new StringBuilder();
            int i = 2;                                        // 分段列表的 '[' 在 1，第一个分段从 2 起
            while (i < json.Length)
            {
                char c = json[i];
                if (c == ']') break;                 // 分段列表到此为止
                if (c == '[')
                {
                    int q = i + 1;                    // 一个分段：[ "译文", "原文", null, null, n ]
                    if (q < json.Length && json[q] == '"')
                    {
                        string s = Unquote(json, q + 1);
                        if (!string.IsNullOrEmpty(s)) sb.Append(s);
                    }
                    i = SkipValue(json, i);
                    continue;
                }
                // 逗号、空白、null 之类：一格一格往前走。字符串整段跨过去，免得它里面的 ']' 被当成列表收尾。
                i = c == '"' ? SkipValue(json, i) : i + 1;
            }
            string result = sb.ToString();
            return result.Length == 0 ? null : result;
        }

        // 跳过 json[at] 起的【一个完整值】，返回它之后的下标。容器里的字符串整段跨过去，
        // 所以译文里出现的 [ ] 不会把结构算错（v0.31 断言里就有这一条）。
        private static int SkipValue(string json, int at)
        {
            char c = json[at];
            if (c == '"') return SkipString(json, at) + 1;
            if (c != '[' && c != '{')
            {
                int i = at;
                while (i < json.Length && json[i] != ',' && json[i] != ']' && json[i] != '}') i++;
                return i;
            }
            int depth = 0;
            for (int j = at; j < json.Length; j++)
            {
                char d = json[j];
                if (d == '"') { j = SkipString(json, j); continue; }
                if (d == '[' || d == '{') depth++;
                else if (d == ']' || d == '}')
                {
                    depth--;
                    if (depth == 0) return j + 1;
                }
            }
            return json.Length;   // 响应被截断：走到头，交给调用方按「没有更多分段」处理
        }

        // json[quoteAt]=='"' 时返回该字符串【结束引号】的下标（处理 \\ 转义）；未闭合返回 json.Length-1。
        private static int SkipString(string json, int quoteAt)
        {
            for (int i = quoteAt + 1; i < json.Length; i++)
            {
                if (json[i] == '\\') { i++; continue; }
                if (json[i] == '"') return i;
            }
            return json.Length - 1;
        }
    }

    // ===== 反馈7：同一屏的多条文本合成一次请求 =====
    // 为什么要合并：一个视野里常有几十条路名/区名/按钮文字，逐条请求就是几十次网络往返，
    // 免费额度的引擎每条还要歇 120 毫秒 —— 玩家等的早就不是 10 秒了。
    // 为什么敢用「编号行」这种土办法：所有对齐判定都在这里，是纯函数，tests\t3 能逐条断言。
    // 铁律：宁可整批作废退回单条，也绝不猜哪段译文属于哪条原文。
    internal static class BatchKit
    {
        // 一次最多带几条。v1.2.4 从 12 提到 40（玩家原话："同一个请求送去翻译的条数是否可以多一点"）。
        // 为什么以前只敢 12、现在敢到 40：条数本身不是危险项，**这一批的总字数**才是。
        // 旧写法只有 MaxChars（单条上限），条数一多就可能顶到小模型的单次输出额度 ⇒ 模型砍掉后几条
        // ⇒ Split 少格子 ⇒ 那几条各退回单条重试（不会错位，Box.B2 钉着），等于白跑一趟。
        // 所以 v1.2.4 把"总量"也钉住（MaxTotalChars），条数按"短文本多带、长文本少带"自动收缩。
        // 另外旧注释里那个"12 条在实机里从没被填满过"的观测，v1.2.4 查明原因不在条数上限、
        // 而在【取货】那一步（界面一条一条入队、5 条线程又在抢），见 Mod.cs TryBatch 顶部那段。
        // v1.2.5：40 仍是硬上限，但队列里还有积压时一批只先拿 ParallelItems(12) 条 ——
        // 一条线程吞满 40 会把另外 4 条线程和后面所有文字一起饿着，首屏要等整批生成完（实机 17 条 10 秒）。
        // ⚠ 再往上提之前先看日志里「一批 N 条」的对上率，那是这条线唯一的一手凭据。
        public const int MaxItems = 40;

        // 单条上限。v1.2.4 从 120 提到 300：说明段落（150~250 字）原来一律进不了批，
        // 于是"整段说明一起送"这件事在实机上根本做不到 —— 一条说明一次往返，玩家看到的就是
        // "翻译特别慢"；而且这一半与引擎无关（机翻那几家 Batchable=false，本来就不攒批，
        // 它们一样被拖成一次一条），与实机反馈"无论机翻还是 AI 翻译都慢很多"对得上。
        private const int MaxChars = 300;

        // 一批的原文总字数上限（v1.2.4 新增，来处见 MaxItems 那段）。
        // 1800 的算法：Anthropic 那条路 max_tokens 固定 2048（EngineKit AnthropicApi.Body），
        // 1800 字英文原文对应的中文译文 ≈ 1200~1400 字 ≈ 1400~2000 token，留出编号行与前缀的余量；
        // 其余引擎不给 max_tokens 字段、走模型默认额度（通常 4k），只会更宽松。
        public const int MaxTotalChars = 1800;

        // 凑批时等一小会儿（毫秒）。实机凭据：界面是一条一条入队的（扫描器走一行排一条），
        // 5 条工作线程又全堵在 Take 上 —— 纯非阻塞的捞取在实机里几乎永远捞到 null，
        // v1.2.3 那一局 155 条里 145 条因此走了单条请求（批量请求总共只发出 1 次）。
        // 只在"还没凑够第二条"时等，凑够了立刻不等 —— 不拿延迟换批次大小。
        public const int SliceMs = 40;

        // v1.2.5 需求2：有别的线程/下一轮在等着时，一批最多先拿这么多条。
        // 为什么不是把 MaxItems 直接压回 12：玩家上一轮明确要过"同一个请求多送几条"。
        // 这两件事其实不冲突 —— 40 条只在一批【独占】整条队列时才是省额度的；
        // 队列里还有活的时候，一条线程拿满 40 就等于把另外 4 条线程和后面几十条文字一起饿着，
        // 玩家看到的就是"先出来一两条，然后长时间没动静"（实机：17 条挤在一次请求里，10 秒后才一起上屏）。
        // 12 的取法：5 条线程 × 12 = 60 条在飞，足够把一屏铺满；再大就只是把首屏往后推。
        // 判据是纯函数，旧包 T3 能逐条钉住（B 段）。
        public const int ParallelItems = 12;

        // 这一批要不要停手、把后面的留给别的 worker：
        //   · 已经到 MaxItems ⇒ 必须停（硬上限）；
        //   · 已经到 ParallelItems 且队列里还有活 ⇒ 停（让并发去吃掉剩下的，首屏更快出来）；
        //   · 队列已经空了 ⇒ 继续捞到 MaxItems（没人抢，多带几条只省请求，不推迟任何人的首屏）。
        // backlog 由调用方现读（TransMap.BacklogSameTier）；这里不碰任何队列类型，T3 直接断言。
        public static bool ShouldLeaveForOthers(int count, int backlog)
        {
            if (count >= MaxItems) return true;
            return count >= ParallelItems && backlog > 0;
        }

        // 这一批的原文总量还放得下这一条吗（每条 4 字留给 "12. " + 换行）。
        public static bool Fits(int usedChars, string next)
        {
            return usedChars + (next == null ? 0 : next.Length) + 4 <= MaxTotalChars;
        }

        // 能不能进批次：非空、单行、无制表、长度合规、且没有会干扰编号识别的前导符号。
        // 自带换行的文本是最大杀手——它会把整批的行数打乱，所以直接排除（这类走原来的单条路径）。
        public static bool CanBatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            if (s.Length > MaxChars) return false;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\n' || c == '\r' || c == '\t') return false;
                if (c < ' ') return false;                     // 其它控制字符一律不进批
            }
            return !StartsWithNumber(s.TrimStart());           // "3. Elm Street" 会和行号标记撞车
        }

        // ===== v1.2.5 修复：板块级请求单元 =====
        // 玩家原话：「第一个板块有 5 个设置项，字数不多，应该把板块标题＋这 5 个设置项标题放在同一个请求里，
        // 后面两个板块依次发送」，而实机日志是几乎每个设置项一个请求（2 条 / 2 条 / 5 条地发，每条 11~42 秒）。
        // 攒不起来的原因不在 SliceMs 那个窗口太短 —— 扫描器【本来就把整页的文字一次收在手上了】，
        // 问题只在投递方式（入队一条、5 条线程各抓一条就发）。所以这里不碰任何队列，只做一件事：
        // 把【同一个板块】收来的一串文字按显示顺序切成若干块，每块就是一次请求。
        // 三条判据都是纯函数（CanBatch / Fits / MaxItems），tests\t3 的 B 段能逐条钉住。
        // 三条例外：
        //   · 进不了批的（自带换行 / 超长 / 数字开头）→ singles，走原来的单条路径，绝不塞进单元；
        //   · 切出来的块只有 1 条 → 也进 singles（一条没有"编号对齐"可言，单条语义更稳，也和旧行为一致）；
        //   · 块与块之间按显示顺序切，绝不重排 —— 玩家要的是"从上往下依次出来"。
        internal static void ChunkUnit(IList<string> texts, List<string[]> units, List<string> singles)
        {
            if (texts == null || units == null || singles == null) return;
            var cur = new List<string>();
            int used = 0;
            for (int i = 0; i < texts.Count; i++)
            {
                string s = texts[i];
                if (!CanBatch(s))
                {
                    FlushUnit(cur, units, singles);
                    used = 0;
                    if (!string.IsNullOrEmpty(s)) singles.Add(s);
                    continue;
                }
                if (cur.Count > 0 && (cur.Count >= MaxItems || !Fits(used, s)))
                {
                    FlushUnit(cur, units, singles);
                    used = 0;
                }
                cur.Add(s);
                used += s.Length + 4;                          // 与 Fits 的口径一致：每条 4 字留给 "12. " + 换行
            }
            FlushUnit(cur, units, singles);
        }

        // ChunkUnit 的收尾动作：≥2 条 ⇒ 一块（一次请求）；1 条 ⇒ 退回单条路径；0 条 ⇒ 什么都不做。
        private static void FlushUnit(List<string> cur, List<string[]> units, List<string> singles)
        {
            if (cur.Count == 0) return;
            if (cur.Count == 1) singles.Add(cur[0]);
            else units.Add(cur.ToArray());
            cur.Clear();
        }

        // 请求里的编号块："1. 文本" 每条一行。
        public static string Join(string[] texts)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < texts.Length; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append(i + 1).Append(". ").Append(texts[i]);
            }
            return sb.ToString();
        }

        // 把模型返回的编号块拆回 n 格。返回值长度恒等于 n，拿不准的槽位是 null。
        // 只有两种收法：①每一行都带编号、编号互不重复且都在 1..n —— 按编号归位；
        //             ②一行编号都没有、且行数正好等于 n —— 按顺序归位。
        // 其余任何情况（漏行、多行、编号撞车、模型先客套了一句）整批作废，交回单条路径。
        public static string[] Split(string raw, int n)
        {
            var outp = new string[n];
            if (string.IsNullOrEmpty(raw) || n <= 0) return outp;

            string[] lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var content = new List<string>(n);
            var number = new List<int>(n);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;                // 模型爱在条目间空一行，空行不算内容
                int num;
                string rest = StripNumber(line, out num);
                content.Add(rest);
                number.Add(num);                               // num<=0 = 这一行没带编号
            }
            if (content.Count == 0) return outp;

            bool allNumbered = true;
            bool anyNumbered = false;
            for (int i = 0; i < number.Count; i++)
            {
                if (number[i] > 0) anyNumbered = true; else allNumbered = false;
            }

            if (allNumbered)
            {
                for (int i = 0; i < content.Count; i++)
                {
                    int idx = number[i] - 1;
                    if (idx < 0 || idx >= n) return new string[n];        // 编号越界 = 它在回答别的批次
                    if (outp[idx] != null) return new string[n];          // 同号撞车 = 归位不可信，整批作废
                    if (content[i].Length > 0) outp[idx] = content[i];
                }
                return outp;
            }

            if (!anyNumbered && content.Count == n)
            {
                for (int i = 0; i < n; i++) if (content[i].Length > 0) outp[i] = content[i];
                return outp;
            }

            return new string[n];        // 半编号/行数对不上：宁可整批退回单条，也不错位
        }

        // "12. Foo" → num=12、返回 "Foo"；没带编号则 num=0、返回整行。
        private static string StripNumber(string line, out int num)
        {
            num = 0;
            int i = 0;
            while (i < line.Length && line[i] >= '0' && line[i] <= '9') i++;
            if (i == 0 || i > 2) return line;                          // 没有数字前缀，或数字长到不像行号
            if (i + 1 >= line.Length) return line;
            char sep = line[i];
            if (sep != '.' && sep != '、' && sep != '．' && sep != ')' && sep != ':' && sep != '：' && sep != ']') return line;
            int v;
            if (!int.TryParse(line.Substring(0, i), NumberStyles.None, CultureInfo.InvariantCulture, out v)) return line;
            num = v;
            string rest = line.Substring(i + 1).TrimStart();
            // 模型偶尔把编号包成 markdown 列表："1. **Foo**" —— 只剥成对的加粗，别动内容里的星号。
            if (rest.Length > 4 && rest.StartsWith("**") && rest.EndsWith("**")) rest = rest.Substring(2, rest.Length - 4).Trim();
            return rest;
        }

        private static bool StartsWithNumber(string s)
        {
            int i = 0;
            while (i < s.Length && s[i] >= '0' && s[i] <= '9') i++;
            if (i == 0 || i + 1 >= s.Length) return false;
            char sep = s[i];
            return sep == '.' || sep == '、' || sep == ')';
        }
    }

    // ===== 选项界面用的「压成一行」（反馈4）=====
    // 症结：「上次测试结果」这行的值一长，框架就把左边的标题挤成一列一字，整行难看。
    // ⚠ 想按玩家说的那样「把文本框默认改成 2 行」做不到 —— Game.Settings.SettingsUITextInputAttribute
    // 是个【无参数】属性（探针实测只有一个 public ctor()），框架没出行数/高度的口子。
    // 所以只能在【值】这一侧收口：折掉换行、按字数截断，完整结果照样写进日志。
    internal static class Display
    {
        // 断点候选：在这些字符【之后】断开，不会把 URL 或英文单词切成半截。
        private const string Separators = "  ，,、。;；.!'”)]】」）/|";

        public static string OneLine(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (maxChars < 4) maxChars = 4;

            string flat = s.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
            while (flat.Contains("  ")) flat = flat.Replace("  ", " ");
            flat = flat.Trim();
            if (flat.Length <= maxChars) return flat;

            int cut = maxChars - 1;                                   // 留一格给省略号
            for (int i = cut; i > maxChars / 2; i--)
                if (Separators.IndexOf(flat[i - 1]) >= 0) { cut = i; break; }   // 断在这个分隔符后面
            return flat.Substring(0, cut).TrimEnd(' ', '，', ',', '、', '。', ';', '；', '.', '!', '\'') + "…";
        }

        // ===== 快捷键绑定路径的清洗（反馈6）=====
        // 症结：ProxyBinding.path 读回来常见形如 "<Keyboard>/t,"——尾巴塞了一个空段。
        // 这种值【写回去】就是一条残缺的组合键，输入系统直接不要，玩家看到的仍是「键没了」。
        // 所以读、写两侧都过这一遍：切段、丢空段、丢重复段（免得每局往返一次就多长一截）、再拼回。
        public static string NormalizeBindingPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            string[] parts = path.Split(',');
            var sb = new System.Text.StringBuilder(path.Length + 4);
            for (int i = 0; i < parts.Length; i++)
            {
                string p = parts[i].Trim();
                if (p.Length == 0) continue;
                // 已经收过同样的段就跳过：往返一次长度必须不变。
                if (sb.Length > 0 && ("," + sb.ToString() + ",").Contains("," + p + ",")) continue;
                if (sb.Length > 0) sb.Append(',');
                sb.Append(p);
            }
            return sb.ToString();
        }

        // 归一化之后还要再问一句：这条路径真的指向一颗键吗？
        // 框架给【没人绑】的 action 生成的默认路径只写了设备没写控件（"<Keyboard>/"），输入系统把这种
        // 前缀当整块设备匹配 —— 于是玩家随便碰一下键盘，这颗 action 就算「被按下」了。模组据此启用
        // 快捷键的话，后果是重开游戏后玩家没碰过任何热键，模组却自己清了缓存重翻一遍。
        // 判据：按逗号切开后，每一段都必须有 '/' 且斜杠后面还有东西。
        public static bool IsBindablePath(string path)
        {
            string p = NormalizeBindingPath(path);
            if (p.Length == 0) return false;
            foreach (string seg in p.Split(','))
            {
                int slash = seg.LastIndexOf('/');
                if (slash < 0 || slash == seg.Length - 1) return false;
            }
            return true;
        }
    }

    // ===== 日志只保留最近 N 次会话（v1.2.4 需求4）=====
    // 玩家原话：「模组日志log文件只保留最近10次游戏的记录就行了（包括闪退，只要游戏启动并记录下来就算），
    // 在退出游戏时自动把前面的记录删除。注意不要在游戏退出时对安全性稳定性有影响。」
    //
    // 为什么判据要放在这个文件（而不是直接在 Mod.cs 里写 I/O）：本文件进得了零游戏 DLL 的 tests\t3 壳。
    // "截到哪一行"这件事有一个真会算错的边界（会话头在不在最后一行、日志里恰好有一行正文提到
    // "session start"），而它**一旦算错就是永久丢玩家的日志**（那是他反馈问题唯一的凭据）。
    // 所以判定做成纯字符串函数在离线壳里逐条钉住，Mod.cs 那边只剩"读文件→调这里→写回去"三步。
    internal static class LogKit
    {
        // 会话分隔行。ModLog.Attach 每次启动写一行，所以它的条数 = 这个文件里记过的启动次数。
        // ⚠ 判据必须【从行首开始】匹配：正文里也可能出现这几个词（比如某条日志在讲这个机制本身），
        //   那种行前面有时间戳（`HH:mm:ss [INFO] ...`），行首不是 `===`，因此不会被误当成会话头。
        //   真实会话头的形状（Attach 里写的就是这一句，前面只有一个换行符）：
        //     "=== session start 2026-10-20 05:48:17 ==="
        public const string SessionMark = "=== session start ";

        // 这一行是不是会话头。
        // allowLeadingSpace：Attach 写的是 "\n=== session start ...\n"，行本身没有前导空白；
        // 但日志滚动/手工编辑/别的编辑器另存之后可能带上空白，所以这里容忍空白，
        // 而**不容忍**正文那种"时间戳在前"的形状 —— 那种行首是数字，Trim 之后也不以 `===` 打头。
        public static bool IsSessionStart(string line)
        {
            if (line == null) return false;
            string t = line.Trim();
            return t.StartsWith(SessionMark, StringComparison.Ordinal) && t.EndsWith("===", StringComparison.Ordinal);
        }

        // 数这个文件里有几次会话（= 玩家启动过几次游戏，含闪退的那些）。
        public static int CountSessions(IList<string> lines)
        {
            if (lines == null) return 0;
            int n = 0;
            for (int i = 0; i < lines.Count; i++) if (IsSessionStart(lines[i])) n++;
            return n;
        }

        // 从第几行开始保留：要让留下的部分【正好包含 keep 次会话】。
        // 返回值 = 保留起点（0 表示一行都不用删）；lines.Count 表示"全删"（只在 keep<=0 时出现）。
        //
        // 算法（从文件尾往前数到第 keep 个会话头）：
        //   · 会话头之后的正文属于那一次会话；往前数到第 keep 个会话头，就从它开始留 ——
        //     于是最早的 (总数 - keep) 次会话连头带正文一起被切掉，不会留下半截正文。
        //   · 若文件开头那段（第一次会话头【之前】的行）存在 —— 比如手工粘贴进来的内容 ——
        //     它不属于任何一次会话，跟着被切掉是对的（它比任何会话都旧）。
        //   · keep <= 0 ⇒ 全部切掉（返回 lines.Count）。
        //   · 会话不足 keep 次 ⇒ 返回 0（什么都不删）。这是最常见的路径，必须零成本。
        //
        // ⚠ 为什么按"会话头"而不是按"行数/字节数"：玩家要的单位是【次游戏】。
        //   按行数切会把最近一次会话砍成半截，那比不切更糟（他要的凭据正好没了）。
        public static int KeepFromLine(IList<string> lines, int keep)
        {
            if (lines == null || lines.Count == 0) return 0;
            if (keep <= 0) return lines.Count;
            int seen = 0;
            for (int i = lines.Count - 1; i >= 0; i--)
            {
                if (!IsSessionStart(lines[i])) continue;
                seen++;
                if (seen >= keep) return i;      // 从这一行（含）开始保留
            }
            return 0;                            // 会话不足 keep 次：一行都不删
        }

        // 这个日志文件叫什么才算我们的（Mod.cs 的清理只碰这些名字）。
        // 严格限定：以 Cs2AutoTranslator 开头、以 .log 或 .log.bak 结尾 —— 绝不触碰 dll/cache/json。
        // 与 v1.0.3 起就有的 PruneOldLogs 判据逐字一致（那一份写在 Mod.cs 里，这里只是把它提取到可离线测的一侧；
        // 两处判据必须同一个答案，所以 Mod.cs 那边改成调这里，不再自己写第二份 StartsWith/EndsWith）。
        public static bool IsOurLogFile(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return false;
            return fileName.StartsWith("Cs2AutoTranslator", StringComparison.Ordinal)
                && (fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                    || fileName.EndsWith(".log.bak", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ===== 凭据文本的清洗（v1.1.7 从 Mod.CleanKey 搬过来）=====
    // 为什么搬：这颗是 v0.15 那次真 bug 的住址 —— 中文输入法会把不可见控制字符混进玩家粘进来的 key，
    // `WebClient.Headers.Add` 当场报 "Specified value has invalid Control characters"（§10 第 19 条）。
    // 方法体 100% 纯 BCL，可它原先住在 `class Mod` 里，而 Mod 在【声明处】就引用了游戏类型
    //（`: IMod`）⇒ CLR 加载类型即失败，离线壳一个方法都调不到（§10 第 29 条点名的就是这一颗）。
    // 搬进本文件之后它进得了 T3：洗多了一格（把 key 里合法的 `-`/`=` 洗掉）、洗漏了一格（留下 \u0007）
    // 这两种错法从此先在离线断言里红，而不是等玩家在游戏里发现「我填的 key 明明是对的」。
    // ⚠ Mod.cs / Setting.cs 里所有 Mod.CleanKey(...) 调用点已全部改口到 Keys.Clean(...)，本文件不留旧名，
    //   避免出现两份会各自漂移的判据。§10 第 19 条里那句「CleanKey」现在指的是这一颗。
    internal static class Keys
    {
        // 去掉控制字符（< 空格，以及 DEL \u007F）和首尾空白。输入法有时会把不可见字符混进 key。
        // 判据只有一条：可打印字符留下、控制字符丢掉。空格留着是故意的 —— Azure region、
        // 「Português (Brasil)」这类母语名本身就有空格，丢掉就把合法值洗成非法值（v0.15 那轮的对照组）。
        public static string Clean(string k)
        {
            if (string.IsNullOrEmpty(k)) return k ?? string.Empty;
            var sb = new StringBuilder(k.Length);
            foreach (char c in k) if (c >= ' ' && c != '\u007F') sb.Append(c);
            return sb.ToString().Trim();
        }
    }

}
