namespace Cs2AutoTranslator
{
    // 目标语言目录（需求 2/3）：不局限于游戏官方的 12 种语言，但只收录「母语名能被游戏字体正常显示」的约 44 种。
    // code  = 传给翻译引擎/本地化系统的语言标识（LangMap 会按各引擎再做一次归一化/透传）。
    // name  = 该语言的母语写法，作为下拉框显示名（玩家一眼认得自己的语言）。
    // 需求3：删掉了希腊/泰/印度语系/阿拉伯/希伯来/波斯/乌尔都/亚美尼亚/格鲁吉亚等——它们的母语名（及译文）
    //        在游戏字体里没有字形，会显示成豆腐块□，故不再列出。保留拉丁/西里尔/中日韩/假名/谚文（官方 12 语言的书写系统）。
    internal static class LanguageCatalog
    {
        public static readonly (string code, string name)[] All =
        {
            ("zh-HANS", "简体中文"),
            ("zh-HANT", "繁體中文"),
            ("en",      "English"),
            ("ja",      "日本語"),
            ("ko",      "한국어"),
            ("fr",      "Français"),
            ("de",      "Deutsch"),
            ("es",      "Español"),
            ("it",      "Italiano"),
            ("pt-BR",   "Português (Brasil)"),
            ("pt",      "Português"),
            ("ru",      "Русский"),
            ("uk",      "Українська"),
            ("pl",      "Polski"),
            ("nl",      "Nederlands"),
            ("sv",      "Svenska"),
            ("da",      "Dansk"),
            ("no",      "Norsk"),
            ("fi",      "Suomi"),
            ("is",      "Íslenska"),
            ("tr",      "Türkçe"),
            ("cs",      "Čeština"),
            ("sk",      "Slovenčina"),
            ("hu",      "Magyar"),
            ("ro",      "Română"),
            ("bg",      "Български"),
            ("hr",      "Hrvatski"),
            ("sr",      "Српски"),
            ("sl",      "Slovenščina"),
            ("lt",      "Lietuvių"),
            ("lv",      "Latviešu"),
            ("et",      "Eesti"),
            ("vi",      "Tiếng Việt"),
            ("id",      "Bahasa Indonesia"),
            ("ms",      "Bahasa Melayu"),
            ("tl",      "Filipino"),
            ("kk",      "Қазақша"),
            ("az",      "Azərbaycan"),
            ("ca",      "Català"),
            ("eu",      "Euskara"),
            ("gl",      "Galego"),
            ("sw",      "Kiswahili"),
            ("af",      "Afrikaans"),
            ("la",      "Latina"),
        };

        // ===== 「这一格是不是在选语言」—— 语言下拉框永不机翻（v1.1.2，玩家点名）=====
        // 实机凭据（2026-10-01 15:09:07 与 15:13:14 两行）：
        //   `写入译文：页:Cs2AutoTranslator… id=한국어 源=한국어 → 上屏=朝鲜语`
        //   `写入译文：页:Cs2AutoTranslator… id=Français 源=Français → 上屏=英语`   ← 法语被翻成"英语"
        //   `写入译文：页:Traffic.Traffic.Mod id=English 源=English → 上屏=英语`
        // 为什么这必须是【一条独立的判据】而不能指望"这是我们自己的键"：目标语言那一栏的候选项走的是
        // `LocalizedString.Value(母语名)` 那类裸文字（本模组故意用母语写法，玩家一眼认出自己的语言），
        // 里面【没有】我们的键名标记，于是 v1.1.1 放开"裸文字 id 也送翻"之后，别的模组的语言选择框
        // （Traffic、I18NEverywhere…）和本模组自己的目标语言栏一起被翻了 —— 后果比漏翻更坏：
        // 一个中文玩家在一个列着「Français / Deutsch / 日本語」的框里找法语，靠的就是那三个词长自己本来的样子。
        // 判据只用【整串相等】（去首尾空白、忽略大小写），命中就整格不翻；句子里出现语言名不算
        // （"Use French layout" 仍照翻）—— 与 Scope 规则⑥ 专有名词同一套收法。
        private static readonly System.Collections.Generic.HashSet<string> s_LanguageNames =
            new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

        static LanguageCatalog()
        {
            // 44 种母语写法：直接吃上面那张表，别处不再抄一份（抄了就会两边不一致）。
            for (int i = 0; i < All.Length; i++) s_LanguageNames.Add(All[i].name);
            // 常见"用英文称呼语言"的写法：有些模组的语言栏是 English / Chinese / Japanese 这种形状。
            // 只收【整串就是一个语言名】的，带修饰的（"Chinese (Simplified)"）交给下面的整栏判据兜。
            string[] exonyms =
            {
                "arabic","bengali","bosnian","bulgarian","catalan","czech","danish","dutch","english",
                "estonian","finnish","french","georgian","german","greek","hebrew","hindi","hungarian",
                "icelandic","indonesian","italian","japanese","kazakh","korean","latin","latvian",
                "lithuanian","malay","nepali","norwegian","persian","polish","portuguese","romanian",
                "russian","serbian","slovak","slovenian","spanish","swahili","swedish","tagalog","thai",
                "turkish","ukrainian","urdu","vietnamese","chinese","filipino","afrikaans","azerbaijani",
                "basque","galician","croatian","farsi","malayalam","tamil","telugu","kannada","bhojpuri",
            };
            for (int i = 0; i < exonyms.Length; i++) s_LanguageNames.Add(exonyms[i]);
            // 带地区的英文写法（"English (United Kingdom)" / "Português (Brasil)" 这类）：整串相等命中不了，
            // 所以另外记下这些"语言名 + 括号"的前缀形状，交给下面第二层判据。
        }

        // 整串就是一个语言名 ⇒ true。
        public static bool IsLanguageName(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            string t = text.Trim();
            if (t.Length < 2 || t.Length > 28) return false;
            if (s_LanguageNames.Contains(t)) return true;
            // 「English (United Kingdom)」「Chinese (Simplified)」这类带括号的写法：剥掉括号再比一次。
            // ⚠ 只在剥完之后确实是一个语言名时才算，避免把 "Fire (Station)" 这种当成语言。
            int p = t.IndexOf('(');
            if (p > 2 && t[t.Length - 1] == ')')
            {
                string head = t.Substring(0, p).Trim();
                if (head.Length >= 2 && s_LanguageNames.Contains(head)) return true;
            }
            return false;
        }

        // 一栏候选项里【过半】都是语言名 ⇒ 这一整栏就是"选择语言"的下拉框（v1.1.2 的整栏判据）。
        // 为什么要整栏判：单个模组的语言栏可能写着 "Magyar nyelv" 这种我们表里没有的串，逐条认会漏；
        // 而"过半都是语言名"这件事本身几乎不可能是别的用途的一栏。要求至少两条，防止"只有 1 项的框"
        // （比如某档就写着 English）被误判成整栏语言列表而永远不翻。
        public static bool IsLanguagePickerColumn(System.Collections.Generic.IList<string> itemNames)
        {
            if (itemNames == null || itemNames.Count < 2) return false;
            int hit = 0;
            for (int i = 0; i < itemNames.Count; i++) if (IsLanguageName(itemNames[i])) hit++;
            return hit >= 2 && hit * 10 >= itemNames.Count * 6;     // ≥60% 命中
        }

        // ===== 游戏官方的 12 种界面语言 =====
        // 这是「本模组自己的界面文字有手写翻译」的那 12 种（L10n.cs 的表就是按这个顺序写的，
        // 所以那张表直接引用这里，不再各处抄一份 —— 两边不一致的后果是「加了语言但表里没有」这种静默错行）。
        // 用途（v1.0.5 需求2）：玩家选的【目标语言】在这 12 种之内 ⇒ 我们的界面文字用手写表；
        // 在这 12 种之外（小语种）⇒ 我们的文字也送去机翻，否则全游戏都翻了、只有本模组的设置页还是英语。
        public static readonly string[] OfficialUiLocales =
        {
            "de-DE", "en-US", "es-ES", "fr-FR", "it-IT", "ja-JP",
            "ko-KR", "pl-PL", "pt-BR", "ru-RU", "zh-HANS", "zh-HANT",
        };

        // 目标语言是不是那 12 种之一 —— **按基础语言码比**，不是整串比。
        // 为什么不能整串比：这套判据的两个调用方用的码形状不一样。游戏侧的界面 locale 是
        // "ja-JP"/"zh-HANS" 这种带地区的，而玩家在下拉框里选的【目标语言】是目录裸码 "ja"/"ko"/"vi"
        // （只有简繁中文例外，目录里就写成 zh-HANS/zh-HANT）。整串比的话 "ja" 会判成"不是官方语言"
        // ⇒ 日语目标也去机翻我们自己的文案，等于把手写表废掉了。按基础码比，12 种正好落成 10 个基础码
        // （zh 一格同时盖住简繁），且目录里其它 32 种没有一个基础和这 10 个撞车（核对过：sr/nl/vi/… 都不撞）。
        static readonly System.Collections.Generic.HashSet<string> s_OfficialUiBaseCodes =
            new System.Collections.Generic.HashSet<string>(MakeOfficialBaseCodes());

        static string[] MakeOfficialBaseCodes()
        {
            var arr = new string[OfficialUiLocales.Length];
            for (int i = 0; i < OfficialUiLocales.Length; i++) arr[i] = BaseCode(OfficialUiLocales[i]);
            return arr;
        }

        public static bool IsOfficialUiLocale(string locale)
        {
            string b = BaseCode(locale);
            return b.Length > 0 && s_OfficialUiBaseCodes.Contains(b);
        }

        // "zh-Hans" / "zh_HANS" / "ZH-hans" → "zh-hans"；只统一分隔符与大小写，**不丢掉地区后缀**
        // （zh-HANS 与 zh-HANT 必须还能分开）。
        private static string Normalize(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return "";
            var sb = new System.Text.StringBuilder(locale.Length);
            foreach (char c in locale)
            {
                if (c == '_' || c == '-') { sb.Append('-'); continue; }
                sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }

        // ===== 「Hello, world」的各语言写法（v1.0.5 需求1：测试按钮那颗探针要随游戏语言变）=====
        // 为什么要随游戏语言：探针送去的方向必须和玩家实际会走的方向一致才叫"测过了"。
        // 游戏语言是日语、目标是简中时，玩家实际发出去的是日→中；拿英文 Hello world 去测，
        // 通不通只证明了一条他根本不会走的路。
        // 按【基础语言码】收录（键不含地区），所以游戏侧的 "ja-JP"、引擎侧的 "ja" 都能落到同一句。
        // 键 = All[] 里出现的语言 + 官方 12 语用到的基础码；值取该语言里"你好，世界"的通行写法。
        static readonly System.Collections.Generic.Dictionary<string, string> s_Greetings =
            new System.Collections.Generic.Dictionary<string, string>
            {
                { "zh", "你好，世界" },
                { "en", "Hello, world" },
                { "ja", "こんにちは、世界" },
                { "ko", "안녕하세요, 세계" },
                { "fr", "Bonjour le monde" },
                { "de", "Hallo, Welt" },
                { "es", "Hola, mundo" },
                { "it", "Ciao, mondo" },
                { "pt", "Olá, mundo" },
                { "ru", "Привет, мир" },
                { "uk", "Привіт, світ" },
                { "pl", "Cześć, świecie" },
                { "nl", "Hallo, wereld" },
                { "sv", "Hej, världen" },
                { "da", "Hej, verden" },
                { "no", "Hei, verden" },
                { "fi", "Hei, maailma" },
                { "is", "Halló, heimur" },
                { "tr", "Merhaba, dünya" },
                { "cs", "Ahoj, světe" },
                { "sk", "Ahoj, svet" },
                { "hu", "Helló, világ" },
                { "ro", "Bună, lume" },
                { "bg", "Здравей, свят" },
                { "hr", "Bok, svijete" },
                { "sr", "Здраво, свете" },
                { "sl", "Pozdravljen, svet" },
                { "lt", "Sveikas, pasauli" },
                { "lv", "Sveika, pasaule" },
                { "et", "Tere, maailm" },
                { "vi", "Xin chào, thế giới" },
                { "id", "Halo, dunia" },
                { "ms", "Hai, dunia" },
                { "tl", "Kumusta, mundo" },
                { "fil", "Kumusta, mundo" },
                { "kk", "Сәлем, әлем" },
                { "az", "Salam, dünya" },
                { "ca", "Hola, món" },
                { "eu", "Kaixo, mundua" },
                { "gl", "Ola, mundo" },
                { "sw", "Habari, dunia" },
                { "af", "Hallo, wêreld" },
                { "la", "Salve, mundus" },
            };

        // 取游戏语言那句「你好，世界」。查不到就回英语 —— 探针的作用是把这条链打通，
        // 拿一句保底的外语句子照样能测出连通性，绝不能因为表里缺一格就"测试失败"误导玩家。
        public static string GreetingFor(string locale)
        {
            string baseCode = BaseCode(locale);
            string g;
            if (baseCode.Length > 0 && s_Greetings.TryGetValue(baseCode, out g)) return g;
            return "Hello, world";
        }

        // ===== 「测试翻译」那颗探针该用哪一句（v1.0.5 第四条，2026-09-29 用户改的主意）=====
        // 固定用英文 "Hello, world"：玩家一般把目标语言设在英文之外，英文句子对任何引擎都是一条有效探针。
        // 唯一的例外是目标语言本身就是英文 —— 那时英文→英文等于没测，换成中文「你好，世界」送过去。
        // 两句都取自上面那张 44 语表（GreetingFor），所以表里那两格就是唯一真值，不再另写字面量。
        public static string ProbeFor(string targetLocale)
        {
            return BaseCode(targetLocale) == "en" ? GreetingFor("zh") : GreetingFor("en");
        }

        // "zh-HANS" / "pt_BR" / "en-US" → "zh" / "pt" / "en"（问候语不需要区分地区；
        // 需要区分地区的判断走 IsOfficialUiLocale，那里保留后缀）。
        public static string BaseCode(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return "";
            string n = Normalize(locale);
            int dash = n.IndexOf('-');
            return dash > 0 ? n.Substring(0, dash) : n;
        }
    }
}
