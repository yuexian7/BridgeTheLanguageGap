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
    }
}
