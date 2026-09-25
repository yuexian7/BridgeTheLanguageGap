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
        /// <summary>模组版本号，不带 v 前缀，与平台 ModVersion 同格式（平台那侧写的是 "1.0.4"）。</summary>
        internal const string kVersion = "1.0.4";

        /// <summary>作者署名。按作者本人要求逐字用 "yuexian"，不是平台账号名 "yuexian7"。</summary>
        internal const string kAuthor = "yuexian";

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
