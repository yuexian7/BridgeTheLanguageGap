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
        public const int MaxItems = 12;         // 一次最多带几条：再多就顶到小模型的输出长度，反而整批作废
        private const int MaxChars = 120;       // 超长文本（说明段落）单独走，混在一批里只会拉长整批

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

}
