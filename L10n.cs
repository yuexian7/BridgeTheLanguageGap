using System;
using System.Collections.Generic;
using System.Threading;
using Colossal;
using Colossal.Localization;

namespace Cs2AutoTranslator
{
    // 本模组自身界面文字的多语言表。
    // v0.30 及以前的键：官方 12 种语言全部硬编码 —— 不依赖网络/key，离线即用，选项界面跟随游戏语言显示。
    // v0.31 起新增的键：只手写「英文源 + 简繁中文」（AddEnZh），其余 9 种官方语言由 SelfL10n 机翻补齐并写进缓存。
    // 所以取文字一律走 SelfL10n.T：有手写译文就用硬编码，没有就用机翻，机翻还没好就回退英文。
    internal static class L10n
    {
        // 列顺序固定：每行 Add(key, …) 必须按这个顺序给 12 个值。
        internal static readonly string[] Locales =
        {
            "de-DE", "en-US", "es-ES", "fr-FR", "it-IT", "ja-JP",
            "ko-KR", "pl-PL", "pt-BR", "ru-RU", "zh-HANS", "zh-HANT",
        };

        // key -> (locale -> text)
        private static readonly Dictionary<string, Dictionary<string, string>> Table = Build();

        internal static IEnumerable<string> AllKeys => Table.Keys;

        public static string T(string locale, string key)
        {
            if (Table.TryGetValue(key, out var perLocale))
            {
                if (!string.IsNullOrEmpty(locale) && perLocale.TryGetValue(locale, out var v) && !string.IsNullOrEmpty(v)) return v;
                if (perLocale.TryGetValue("en-US", out var en) && !string.IsNullOrEmpty(en)) return en; // 回退英文
                foreach (var kv in perLocale) if (!string.IsNullOrEmpty(kv.Value)) return kv.Value;     // 再回退任意非空
            }
            return key;
        }

        // 硬编码表里【这条键、这个语言】到底有没有手写译文。
        // v0.31 起新键只手写「英文源 + 简繁中文」，其余官方语言靠 SelfL10n 机翻补 —— 靠这个方法判断该不该补。
        internal static bool Has(string locale, string key)
        {
            if (string.IsNullOrEmpty(locale) || string.IsNullOrEmpty(key)) return false;
            return Table.TryGetValue(key, out var perLocale)
                && perLocale.TryGetValue(locale, out var v) && !string.IsNullOrEmpty(v);
        }

        private static void Add(Dictionary<string, Dictionary<string, string>> t, string key, params string[] vals)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < Locales.Length && i < vals.Length; i++) d[Locales[i]] = vals[i];
            t[key] = d;
        }

        // 只手写「英文源 + 简繁中文」：英文是机翻的输入源，中文是本模组的主要受众（手写比机翻稳）。
        // 其余 9 种官方语言留空，由 SelfL10n 用当前引擎机翻补齐并写进缓存（namespace "self"）。
        private static void AddEnZh(Dictionary<string, Dictionary<string, string>> t, string key, string en, string hans, string hant)
        {
            Add(t, key, null, en, null, null, null, null, null, null, null, null, hans, hant);
        }

        // 引擎下拉框的说明正文（需求5 重写）。
        // ⚠ 这段是 tooltip：鼠标移开就消失，而且【没有滚动条】，长了就放不下 —— 所以每个引擎只准占一行，
        // 一行控制在 25 个汉字宽度以内（2K 屏的量；1080p 更窄，所以实际写到 20 出头就收手），
        // 只写「引擎名 + MT/AI + 几个字的特点」。额度、限流、注册要求这类长信息全部搬到 notes.* 键，
        // 由界面里常驻的「说明：」行显示（13 个 NotesXxx 属性，按当前引擎显隐，见 Setting.cs 与反馈6 的注释）。
        // 英文是机翻的输入源，所以措辞尽量直白、每行只讲一个引擎。
        private static string EngineHelpEn()
        {
            return "MT = machine translation (fast, cheap). AI = large-model translation (reads context better).\n"
                + "Google (MT): no key, fast and accurate — the default\n"
                + "DuckDuckGo (MT): no key, private, average quality\n"
                + "MyMemory (MT): no key, average quality, backup only\n"
                + "Yandex (MT): needs an IAM token\n"
                + "Microsoft Azure (MT): the most accurate MT, big free quota\n"
                + "DeepL (MT): the most natural wording\n"
                + "Baidu (MT): the most stable in mainland China\n"
                + "Google Gemini (AI): reads game context best, slower\n"
                + "Groq Cloud (AI): the fastest AI option\n"
                + "OpenRouter (AI): aggregator with free models\n"
                + "SiliconFlow (AI): direct in mainland China, but even its free models need account balance\n"
                + "Cloudflare (AI): free daily allowance\n"
                + "Custom AI (uses tokens): your own key, your own bill";
        }

        private static string EngineHelpHans()
        {
            return "MT＝机器翻译，快而省；AI＝大模型翻译，更懂语境。\n"
                + "Google（MT）：免key，快而准，默认引擎\n"
                + "DuckDuckGo（MT）：免key，重隐私，质量一般\n"
                + "MyMemory（MT）：免key，质量中等，应急备用\n"
                + "Yandex（MT）：需填IAM令牌\n"
                + "微软Azure（MT）：机翻里最准，免费额度大\n"
                + "DeepL（MT）：译文最自然\n"
                + "百度（MT）：大陆最稳，需实名认证\n"
                + "Google Gemini（AI）：最懂游戏语境，速度偏慢\n"
                + "Groq Cloud（AI）：AI里最快\n"
                + "OpenRouter（AI）：模型聚合站，有免费档\n"
                + "硅基流动（AI）：大陆可直连，免费档也要账户里有余额\n"
                + "Cloudflare（AI）：每天有免费额度\n"
                + "自定义AI（消耗token）：自备key，费用自理";
        }

        private static string EngineHelpHant()
        {
            return "MT＝機器翻譯，快而省；AI＝大模型翻譯，更懂語境。\n"
                + "Google（MT）：免key，快而準，預設引擎\n"
                + "DuckDuckGo（MT）：免key，重隱私，品質普通\n"
                + "MyMemory（MT）：免key，品質中等，應急備用\n"
                + "Yandex（MT）：需填IAM權杖\n"
                + "微軟Azure（MT）：機翻裡最準，免費額度大\n"
                + "DeepL（MT）：譯文最自然\n"
                + "百度（MT）：大陸最穩，需實名認證\n"
                + "Google Gemini（AI）：最懂遊戲語境，速度偏慢\n"
                + "Groq Cloud（AI）：AI裡最快\n"
                + "OpenRouter（AI）：模型聚合站，有免費檔\n"
                + "矽基流動（AI）：大陸可直連，免費檔也要帳戶裡有餘額\n"
                + "Cloudflare（AI）：每天有免費額度\n"
                + "自訂AI（消耗token）：自備key，費用自理";
        }

        private static Dictionary<string, Dictionary<string, string>> Build()
        {
            var t = new Dictionary<string, Dictionary<string, string>>();

            // 标题（需求 6）：改名为「拯救语言不通」。
            Add(t, "name",
                "Sprachbarriere überbrücken", "Bridge the Language Gap", "Salva la barrera del idioma", "Combler la barrière de la langue", "Colma la barriera linguistica", "言葉の壁を救う",
                "언어 장벽 해소", "Pokonaj barierę językową", "Supere a barreira do idioma", "Преодолей языковой барьер", "拯救语言不通", "拯救語言不通");

            // 需求1：三个标签页。新键用 AddEnZh（英文源 + 简繁中），其余 9 种官方语言由 SelfL10n 机翻兜底。
            AddEnZh(t, "tab.translate", "Translation", "翻译设置", "翻譯設定");
            AddEnZh(t, "tab.engine", "Translation engine", "翻译引擎", "翻譯引擎");
            AddEnZh(t, "tab.misc", "Hotkeys & other", "快捷键与其它", "快速鍵與其它");

            AddEnZh(t, "group.engine", "Choose an engine", "选择引擎", "選擇引擎");
            AddEnZh(t, "group.test", "Test", "测试", "測試");
            AddEnZh(t, "group.hotkeys", "Hotkeys", "快捷键设置", "快速鍵設定");
            AddEnZh(t, "group.log", "Log", "日志", "日誌");
            AddEnZh(t, "group.disclaimer", "Disclaimer", "声明", "聲明");

            // ===== 总开关（需求 8）=====
            Add(t, "group.main",
                "Hauptschalter", "Master switch", "Interruptor principal", "Commutateur principal", "Interruttore principale", "メインスイッチ",
                "마스터 스위치", "Przełącznik główny", "Interruptor principal", "Главный переключатель", "总开关", "總開關");

            Add(t, "label.enabled",
                "Übersetzung aktivieren", "Enable translation", "Activar traducción", "Activer la traduction", "Attiva traduzione", "翻訳を有効化",
                "번역 활성화", "Włącz tłumaczenie", "Ativar tradução", "Включить перевод", "启用翻译", "啟用翻譯");

            // 反馈3：正文按用户原文四段照录，段间用 \n（与下面 desc.engine 那条 14 行清单同一条渲染路径，实机可换行）。
            // 语种从「12 份手写」改成「英/简/繁 + 机翻兜底」：这段字多且还会随反馈再改，手写 12 份只会有一份是新的。
            AddEnZh(t, "desc.enabled",
                "Please set the language, scope and translation engine first, and only then turn this on.\n"
                + "Don't worry — turning this off does not clear the translations.\n"
                + "With in-game world text turned on, loading a large city for the first time can take a while to translate road names, district names and the like; you are free to keep building while you wait. If you are testing a paid AI translation engine, try not to test it right after entering a save, because every single test re-translates all of that rendered text and that costs real money (you can also switch this option off while testing).\n"
                + "How long translation takes depends on how much text the current panel has. If text inside the game comes out partly untranslated, takes too long, or does not show up at all, try turning translation off and back on again (the hotkey is more convenient), or switch to another translation engine.",
                "请在设置好语言、范围及翻译引擎之后再启用。\n"
                + "请放心，译文不会在关闭后清除。\n"
                + "打开游戏世界渲染文本时，第一次加载大城市可能会消耗较多时间翻译道路名、区域名称等，你完全可以一边等待一边继续建设你的城市。如果你在测试收费的AI翻译引擎，尽量不要在进入游戏后测试，否则每次测试都会重新翻译这些渲染文本，很费钱的（你也可以关掉这个选项再测试）。\n"
                + "翻译时间与当前界面的文字数相关。游戏内若出现翻译不全、翻译过久、翻译不显示的情况，尝试关闭翻译再重新启用（快捷键更方便），或切换其它翻译引擎。",
                "請在設定好語言、範圍及翻譯引擎之後再啟用。\n"
                + "請放心，譯文不會在關閉後清除。\n"
                + "開啟遊戲世界渲染文字時，第一次載入大城市可能會花費較多時間翻譯道路名、區域名稱等，你完全可以一邊等待一邊繼續建設你的城市。如果你在測試收費的AI翻譯引擎，盡量不要在進入遊戲後測試，否則每次測試都會重新翻譯這些渲染文字，很花錢的（你也可以關掉這個選項再測試）。\n"
                + "翻譯時間與目前介面的文字數相關。遊戲內若出現翻譯不全、翻譯過久、翻譯不顯示的情況，嘗試關閉翻譯再重新啟用（快捷鍵更方便），或切換其它翻譯引擎。");

            // ===== 目标语言与保存分组（需求 7：置于翻译范围下方）=====
            Add(t, "group.target",
                "Zielsprache & Speichern", "Target language & save", "Idioma de destino y guardado", "Langue cible et enregistrement", "Lingua di destinazione e salvataggio", "翻訳先言語と保存",
                "번역 대상 언어 및 저장", "Język docelowy i zapis", "Idioma de destino e salvamento", "Целевой язык и сохранение", "翻译语言与保存", "翻譯語言與保存");

            Add(t, "label.engine",
                "Übersetzungs-Engine", "Translation engine", "Motor de traducción", "Moteur de traduction", "Motore di traduzione", "翻訳エンジン",
                "번역 엔진", "Silnik tłumaczenia", "Motor de tradução", "Движок перевода", "翻译引擎", "翻譯引擎");

            // 需求2：每个引擎单独一行，写清「机翻还是AI、免费额度、频率/注册注意事项」。
            // 行序 = 下拉框序（免注册 → 需注册有免费额度 → 自定义AI），玩家不用来回对照。
            // 正文太长，拆到下面三个 EngineHelp* 方法里，免得把 Build() 撑成一面墙。
            AddEnZh(t, "desc.engine", EngineHelpEn(), EngineHelpHans(), EngineHelpHant());

            // 需求5：鼠标移开 tooltip 就没了，而 tooltip 框没有滚动条，长文案放不下。
            // 所以每个引擎的额度/限流/注册说明改成【常驻一行小字】，显示在引擎下拉框正下方（标题位置=左侧）。
            //
            // 反馈6 根因：以前这里只有【一个】EngineNotes 属性，正文由 SettingsLocale.EngineNotes() 按「当前选中引擎」
            // 现算 —— 而 SettingsLocale.ReadEntries 一整局只被调用一次（建语言字典时），算出来的那句话就此冻住，
            // 玩家换了引擎看到的还是上一家的说明；点「清除缓存」之所以能治好，是因为那一下会重读语言字典、
            // 顺带把 ReadEntries 又跑了一遍。
            // 现在改成 13 个静态属性（NotesGoogle…NotesCustom）各占一行，每行文字固定不变，
            // 「显示哪一行」交给 [SettingsUIHideByCondition] —— 显隐条件是 RefreshPage 每次都会重算的，
            // 各引擎的 key 输入框早就靠它实时切换，所以这条路走得通。
            // 前缀仍是「说明：」，正文取 notes.<engine 小写>；键名必须与 enum Engine 的成员名小写完全一致。
            //
            // 反馈9：VPN／代理这类提醒只写在简体中文里 —— 海外玩家用不着，翻给他们看反而是噪音。
            AddEnZh(t, "label.engineNotes", "Notes: ", "说明：", "說明：");

            AddEnZh(t, "notes.google",
                "No sign-up and no key — works right away. If one endpoint gets rate-limited the mod switches to another on its own.",
                "免注册免 key，装上就能用。某个通道被限流时会自动换下一个，不用你管。",
                "免註冊免 key，裝上就能用。某個通道被限流時會自動換下一個，不用你管。");

            AddEnZh(t, "notes.duckduckgo",
                "No sign-up and no key. As of 2026-09 its keyless endpoint rejects this mod's requests, which may vary by region or network — if it fails, switch to Google.",
                "免注册免 key。2026-09 实测它的免 key 通道会拒绝本模组的请求，可能因地区或网络而异；不通就换 Google。",
                "免註冊免 key。2026-09 實測它的免 key 通道會拒絕本模組的請求，可能因地區或網路而異；不通就換 Google。");

            AddEnZh(t, "notes.mymemory",
                "5,000 characters a day without a key, or 50,000 a day if you optionally add an email. Average quality — best kept as an emergency backup.",
                "不填邮箱每天 5000 字符，选填邮箱提到每天 5 万字符。质量中等，适合当应急备用。",
                "不填信箱每天 5000 字元，選填信箱提到每天 5 萬字元。品質中等，適合當應急備用。");

            AddEnZh(t, "notes.yandex",
                "Yandex has shut down its keyless web channel, so an IAM token is now required.",
                "Yandex 已经关掉了免 key 的网页通道，现在必须填 IAM 令牌才能用；中国大陆通常还需代理。",
                "Yandex 已經關掉了免 key 的網頁通道，現在必須填 IAM 權杖才能用。");

            AddEnZh(t, "notes.microsoft",
                "Free F0 tier: 2 million characters a month, the most accurate machine translation overall. After creating the Translator resource the region must match; leave it empty for a Global resource.",
                "免费 F0 层每月 200 万字符，机器翻译里综合最准。建好 Translator 资源后区域要填对，全球（Global）资源留空。",
                "免費 F0 層每月 200 萬字元，機器翻譯裡綜合最準。建好 Translator 資源後區域要填對，全球（Global）資源留空。");

            AddEnZh(t, "notes.deepl",
                "500,000 free characters a month and the most natural wording.",
                "免费每月 50 万字符，译文最自然；中国大陆通常需 VPN。",
                "免費每月 50 萬字元，譯文最自然。");

            AddEnZh(t, "notes.baidu",
                "A mainland-China service. Free standard tier with a rate limit; sign-up needs real-name verification, and both the APP ID and the key are required.",
                "标准版免费但有频率限制，中国大陆最稳。注册需实名认证，APP ID 与密钥都要填。",
                "標準版免費但有頻率限制。註冊需實名認證，APP ID 與金鑰都要填。");

            AddEnZh(t, "notes.gemini",
                "Has a free tier and reads game context best of all engines, but is slower than machine translation and may hit per-minute limits. The model is pinned to the gemini-flash-latest alias.",
                "有免费额度，是所有引擎里最懂游戏语境的，但比机器翻译慢，可能触发每分钟限流。模型固定用 gemini-flash-latest 别名。",
                "有免費額度，是所有引擎裡最懂遊戲語境的，但比機器翻譯慢，可能觸發每分鐘限流。模型固定用 gemini-flash-latest 別名。");

            AddEnZh(t, "notes.groq",
                "Has a free tier and is the fastest AI option; the free tier is rate-limited only.",
                "有免费额度，是 AI 引擎里最快的，免费层只有速率限制。注册需谷歌账号或海外手机号，中国大陆通常还需代理。",
                "有免費額度，是 AI 引擎裡最快的，免費層只有速率限制。");

            AddEnZh(t, "notes.openrouter",
                "A model aggregator; free models have names ending in :free. After clicking \"Fetch model list\" every model your key can use is listed, and the ones that suit translation come first among the free ones. Free models share one upstream capacity lane, so a popular one often answers 429 (too busy) — switching to another free model usually fixes it.",
                "模型聚合站，免费档模型的名字以 :free 结尾；点「获取模型列表」后会列出你这个 key 能用的全部模型，免费档里适合翻译的排在最前面。免费档共用一条上游产能通道，热门那几个经常回「太挤了」（429），换一个免费档一般就好了。",
                "模型聚合站，免費檔模型的名字以 :free 結尾；按「取得模型清單」後會列出你這個 key 能用的全部模型，免費檔裡適合翻譯的排在最前面。免費檔共用一條上游產能通道，熱門那幾個經常回「太擠了」（429），換一個免費檔一般就好了。");

            AddEnZh(t, "notes.siliconflow",
                "Several nominally free models, and the endpoint is reachable directly from mainland China; registration needs a mainland phone number. After clicking \"Fetch model list\" every model is listed, with the free ones first. Heads-up: even those models are billed against your account balance, so a zero-balance account keeps answering “insufficient balance” — top up, or use Groq / OpenRouter instead.",
                "有多个标称免费的模型，中国大陆可直连；注册需国内手机号。点「获取模型列表」后会列出全部模型，免费的排在最前面。要注意：这里标称免费的模型照样按账户余额扣费，余额为 0 时会一直回「余额不足」——充值再用的话可以，否则建议改用 Groq 或 OpenRouter。",
                "有多個標稱免費的模型。按「取得模型清單」後會列出全部模型，免費的排在最前面。要注意：這裡標稱免費的模型照樣按帳戶餘額扣費，餘額為 0 會一直回「餘額不足」——儲值再用可以，否則建議改用 Groq 或 OpenRouter。");

            AddEnZh(t, "notes.cloudflare",
                "A free daily allowance on the dedicated m2m100 translation model. Needs a Cloudflare account, with both the Account ID and an API token. Supports fewer languages than the other engines.",
                "每天有免费额度，用 m2m100 专用翻译模型。需注册 Cloudflare 账号，Account ID 与 API 令牌都要填；支持的语言比其它引擎少。",
                "每天有免費額度，用 m2m100 專用翻譯模型。需註冊 Cloudflare 帳號，Account ID 與 API 權杖都要填；支援的語言比其它引擎少。");

            AddEnZh(t, "notes.custom",
                "Bring your own key. Eight vendors’ API formats are built in; pick “Custom” to fill in the address, key, model name and API format yourself. Quota and cost follow your own plan.",
                "自备 key。内置 gpt／claude／kimi／qwen／DeepSeek／gemini／grok／glm 八家接口格式；也可以选「自定义」，接口地址、key、模型名称与接口格式全部自己填。额度与费用按你自己的套餐算。",
                "自備 key。內建 gpt／claude／kimi／qwen／DeepSeek／gemini／grok／glm 八家介面格式；也可以選「自訂」，介面位址、key、模型名稱與介面格式全部自己填。額度與費用依你自己的方案算。");

            // 需求6：两个快捷键的标签。默认不给按键，玩家自己定。
            AddEnZh(t, "label.hotkey.toggle",
                "Toggle translation on/off", "启用/关闭翻译", "啟用/關閉翻譯");

            AddEnZh(t, "label.hotkey.retranslate",
                "Retranslate (clear cache and translate again)", "重新翻译（清除缓存并重新执行翻译）", "重新翻譯（清除快取並重新執行翻譯）");

            AddEnZh(t, "desc.hotkey.toggle",
                "Click this row and simply press the key you want — that is how the game sets shortcuts everywhere else, no typing involved. Press Esc to cancel. Both shortcuts start empty: pick your own keys, and they stay through a game restart. Pressing your key switches translation on or off right where you are, without going back to the options menu, and it works even while the mod is currently off.",
                "点中这一行，然后直接按下你想要的键就设好了 —— 和游戏里其他地方设快捷键的方式完全一样，不需要输入任何字母，按 Esc 可以取消。两项快捷键默认都是空的，由你自己设定，设定后重启游戏会保留。按下这个键会就地开/关翻译，不必回到选项界面，模组当前是关闭状态时也一样管用。",
                "點中這一行，然後直接按下你想要的鍵就設好了 —— 與遊戲裡其他地方設快捷鍵的方式完全一樣，不需要輸入任何字母，按 Esc 可以取消。兩項快捷鍵預設都是空的，由你自己設定，設定後重啟遊戲會保留。按下這個鍵會就地開/關翻譯，不必回到選項介面，模組目前是關閉狀態時也一樣管用。");

            AddEnZh(t, "desc.hotkey.retranslate",
                "Same control as the row above: click it and press a key to bind, Esc to cancel. Pressing it drops the cached translations and translates the interface again with your current settings — handy right after you change the target language or the engine.",
                "与上面那行相同的控件：点中后按下要用的键即完成设置，按 Esc 取消。按这个键会清掉已缓存的译文，并按你当前的设置把界面重新翻译一遍，换目标语言或换引擎之后正用得上。",
                "與上面那行相同的控件：點中後按下要用的鍵即完成設定，按 Esc 取消。按這個鍵會清掉已快取的譯文，並依你目前的設定把介面重新翻譯一遍，換目標語言或換引擎之後正用得上。");

            // 引擎下拉：只显示引擎名（需求 6），额度/VPN 等说明已移到 desc.engine 与各 key 的 desc。
            Add(t, "enum.google",
                "Google Übersetzer", "Google Translate", "Traductor de Google", "Google Traduction", "Google Traduttore", "Google 翻訳",
                "Google 번역", "Tłumacz Google", "Google Tradutor", "Google Переводчик", "谷歌翻译", "谷歌翻譯");

            Add(t, "enum.microsoft",
                "Microsoft Azure", "Microsoft Azure", "Microsoft Azure", "Microsoft Azure", "Microsoft Azure", "Microsoft Azure",
                "Microsoft Azure", "Microsoft Azure", "Microsoft Azure", "Microsoft Azure", "微软 Azure", "微軟 Azure");

            Add(t, "enum.deepl",
                "DeepL", "DeepL", "DeepL", "DeepL", "DeepL", "DeepL",
                "DeepL", "DeepL", "DeepL", "DeepL", "DeepL", "DeepL");

            Add(t, "enum.baidu",
                "Baidu Übersetzer", "Baidu Translate", "Traductor Baidu", "Baidu Traduction", "Baidu Traduttore", "Baidu 翻訳",
                "Baidu 번역", "Tłumacz Baidu", "Baidu Tradutor", "Baidu Переводчик", "百度翻译", "百度翻譯");

            // v0.31 新增的 10 个引擎名（需求1）。大多是专有名词，各语言写法一致，所以只手写英文源 + 中文。
            AddEnZh(t, "enum.duckduckgo", "DuckDuckGo Translate", "DuckDuckGo 翻译", "DuckDuckGo 翻譯");
            AddEnZh(t, "enum.mymemory", "MyMemory", "MyMemory 翻译", "MyMemory 翻譯");
            AddEnZh(t, "enum.yandex", "Yandex Translate", "Yandex 翻译", "Yandex 翻譯");
            AddEnZh(t, "enum.gemini", "Google Gemini", "Google Gemini", "Google Gemini");
            AddEnZh(t, "enum.groq", "Groq Cloud", "Groq Cloud", "Groq Cloud");
            AddEnZh(t, "enum.openrouter", "OpenRouter", "OpenRouter", "OpenRouter");
            AddEnZh(t, "enum.siliconflow", "SiliconFlow", "硅基流动", "矽基流動");
            AddEnZh(t, "enum.cloudflare", "Cloudflare Workers AI", "Cloudflare Workers AI", "Cloudflare Workers AI");
            AddEnZh(t, "enum.custom", "Custom AI model", "自定义AI模型", "自訂AI模型");

            // 需求2：下拉框里免注册引擎后面标「（直接使用）」，自定义 AI 标「（消耗token）」，其余不加括号。
            // 后缀单独成键，由 L10n.SettingsLocale 与 EngineBase.DisplayName 拼在引擎名后面 —— 免得为 14 个引擎 × 12 种语言各写一遍。
            AddEnZh(t, "enum.tag.direct", " (direct use)", "（直接使用）", "（直接使用）");
            AddEnZh(t, "enum.tag.token", " (consumes tokens)", "（消耗token）", "（消耗token）");

            // 需求2：必填项名称末尾标「*」。同样是统一后缀拼接，不逐个改 12 种语言的标签文案。
            AddEnZh(t, "label.required", " *", " *", " *");

            // 测试结果里要说出到底缺哪一项（Mod.FieldLabel），field 名由 EngineKit.MissingField 给。
            AddEnZh(t, "field.key", "API key", "API 密钥", "API 金鑰");
            AddEnZh(t, "field.appid", "APP ID", "APP ID", "APP ID");
            AddEnZh(t, "field.accountid", "Account ID", "账户 ID", "帳戶 ID");
            AddEnZh(t, "field.token", "API token", "API 令牌", "API 權杖");
            AddEnZh(t, "field.model", "model name", "模型名", "模型名");
            AddEnZh(t, "field.baseurl", "API base URL", "接口地址", "介面位址");

            // 需求2：token 计数。平台报了 usage 就用真值，没报就按字数本地估算，所以必须写明「可能有出入」。
            // 反馈3：每家厂商各算各的，措辞要点明这是「当前这一家」的用量，不是全部 AI 加总。
            // 反馈3（本轮改名）：换 key 会把计数清零，所以主语是「这把 API Key」而不是「这家厂商」。
            // 反馈5：这一行只做【静态标题】，数字不写在这里 —— 字典一整局只建一次，写进来就冻在 0；
            //        数字由 Setting.cs 的 Tok* getter 挂在行的值上，每次界面刷新现读。
            AddEnZh(t, "label.tokens",
                "Tokens used by this API key",
                "该API Key已消耗Token数",
                "該API Key已消耗Token數");

            Add(t, "label.target",
                "Übersetzen in", "Translate into", "Traducir a", "Traduire vers", "Traduci in", "翻訳先言語",
                "번역 대상 언어", "Tłumacz na", "Traduzir para", "Переводить на", "翻译成哪种语言", "翻譯成哪種語言");

            Add(t, "desc.target",
                "Zielsprache. Rund 50 gängige Sprachen verfügbar (nicht nur die offiziellen 12). Wird übersprungen, wenn Text bereits in dieser Sprache vorliegt.",
                "Target language. About 50 common languages available (not just the official 12). Text already in this language is skipped.",
                "Idioma de destino. Unos 50 idiomas comunes disponibles (no solo los 12 oficiales). El texto que ya esté en ese idioma se omite.",
                "Langue cible. Environ 50 langues courantes disponibles (pas seulement les 12 officielles). Le texte déjà dans cette langue est ignoré.",
                "Lingua di destinazione. Circa 50 lingue comuni disponibili (non solo le 12 ufficiali). Il testo già in questa lingua viene saltato.",
                "翻訳先の言語。公式12種だけでなく約50の主要言語から選択可。既にその言語の文章は翻訳をスキップ。",
                "대상 언어. 공식 12개뿐 아니라 약 50개 주요 언어 사용 가능. 이미 해당 언어인 글은 건너뜁니다.",
                "Język docelowy. Dostępnych ok. 50 popularnych języków (nie tylko oficjalne 12). Tekst już w tym języku jest pomijany.",
                "Idioma de destino. Cerca de 50 idiomas comuns disponíveis (não só os 12 oficiais). Texto já nesse idioma é ignorado.",
                "Целевой язык. Доступно около 50 распространённых языков (не только официальные 12). Текст уже на этом языке пропускается.",
                "目标语言。不止官方 12 种，约 50 种常见语言可选。已经是该语言的文字会自动跳过。",
                "目標語言。不止官方 12 種，約 50 種常見語言可選。已經是該語言的文字會自動跳過。");

            // ===== 翻译范围（需求 2/11）=====
            Add(t, "group.scope",
                "Übersetzungsumfang", "Translation scope", "Alcance de traducción", "Périmètre de traduction", "Ambito di traduzione", "翻訳範囲",
                "번역 범위", "Zakres tłumaczenia", "Escopo da tradução", "Область перевода", "翻译范围", "翻譯範圍");

            Add(t, "label.scope.modOptions",
                "Mod-Einstellungen & In-Game-Panels", "Mod settings & in-game panels", "Ajustes de mods y paneles del juego", "Paramètres de mods et panneaux en jeu", "Impostazioni mod e pannelli di gioco", "Mod設定とゲーム内パネル",
                "모드 설정 및 게임 내 패널", "Ustawienia modów i panele w grze", "Configurações de mods e painéis no jogo", "Настройки модов и внутриигровые панели", "模组设置项及游戏内面板", "模組設定項及遊戲內面板");

            Add(t, "desc.scope.modOptions",
                "Übersetzt die Beschriftungen und Beschreibungen der Mod-Einstellungen im Optionsmenü sowie die Panel-Texte, die Mods im Spiel anzeigen.",
                "Translates the setting labels and descriptions of mods in the options menu, and the panel text mods show inside the game.",
                "Traduce las etiquetas y descripciones de los ajustes de los mods en el menú de opciones, y los textos de los paneles que los mods muestran en el juego.",
                "Traduit les libellés et descriptions des réglages des mods dans le menu des options, ainsi que les textes des panneaux affichés en jeu par les mods.",
                "Traduce le etichette e le descrizioni delle impostazioni dei mod nel menu opzioni e i testi dei pannelli che i mod mostrano nel gioco.",
                "オプションメニュー内のMod設定のラベルと説明、およびModがゲーム内に表示するパネルの文章を翻訳。",
                "옵션 메뉴에서 모드 설정의 레이블과 설명, 그리고 모드가 게임 안에 표시하는 패널 텍스트를 번역합니다.",
                "Tłumaczy etykiety i opisy ustawień modów w menu opcji oraz teksty paneli, które mody wyświetlają w grze.",
                "Traduz os rótulos e as descrições das configurações dos mods no menu de opções e os textos dos painéis que os mods exibem no jogo.",
                "Переводит названия и описания настроек модов в меню параметров, а также тексты панелей, которые моды показывают в игре.",
                "翻译模组在选项菜单里的设置项标签与说明，以及模组在游戏里显示的面板文字。",
                "翻譯模組在選項選單裡的設定項標籤與說明，以及模組在遊戲裡顯示的面板文字。");

            // 反馈5：少数模组（例：Traffic Tool Essentials）的游戏内面板文字写死在模组自己的文件里，
            // 压根不进游戏的本地化流程 —— 我们的钩子读不到，也没法靠逐个适配解决（每个模组都得改它的源码）。
            // 单独一个键、注册时拼在上面那句后面：这样 12 种语言都能带上这句限制说明，而不必逐语言重写整段。
            AddEnZh(t, "note.modOptionsLimit",
                " Note: part of some mods’ in-game panel text is hard-coded inside the mod’s own files. This mod cannot read that text, so it can neither translate nor change it.",
                "注意：部分模组的游戏内面板文字是写死在模组文件里的，本模组读不到这些文字，因此既无法翻译也无法修改。",
                "注意：部分模組的遊戲內面板文字是寫死在模組檔案裡的，本模組讀不到這些文字，因此既無法翻譯也無法修改。");

            Add(t, "label.scope.assetNames",
                "Asset-Namen", "Asset names", "Nombres de assets", "Noms d’assets", "Nomi degli asset", "アセット名",
                "에셋 이름", "Nazwy zasobów", "Nomes de assets", "Названия ассетов", "资产名称", "資產名稱");

            Add(t, "desc.scope.assetNames",
                "Übersetzt Namen von Gebäuden, Diensten und anderen Assets.",
                "Translates the names of buildings, services and other assets.",
                "Traduce los nombres de edificios, servicios y otros assets.",
                "Traduit les noms des bâtiments, services et autres assets.",
                "Traduce i nomi di edifici, servizi e altri asset.",
                "建物・サービス・その他アセットの名前を翻訳。",
                "건물·서비스·기타 에셋의 이름을 번역.",
                "Tłumaczy nazwy budynków, usług i innych zasobów.",
                "Traduz os nomes de edifícios, serviços e outros assets.",
                "Переводит названия зданий, служб и других ассетов.",
                "翻译资产（建筑、服务等）的名称。",
                "翻譯資產（建築、服務等）的名稱。");

            Add(t, "label.scope.assetDesc",
                "Asset-Beschreibungen", "Asset descriptions", "Descripciones de assets", "Descriptions d’assets", "Descrizioni degli asset", "アセット説明",
                "에셋 설명", "Opisy zasobów", "Descrições de assets", "Описания ассетов", "资产描述", "資產描述");

            Add(t, "desc.scope.assetDesc",
                "Übersetzt den Beschreibungstext unter dem Asset-Titel (ohne Eigenschafts-/Parameter-Namen).",
                "Translates the description text below the asset title (excludes property/parameter names).",
                "Traduce el texto descriptivo bajo el título del asset (excluye nombres de propiedades/parámetros).",
                "Traduit le texte de description sous le titre de l’asset (exclut les noms de propriétés/paramètres).",
                "Traduce il testo descrittivo sotto il titolo dell’asset (esclude i nomi di proprietà/parametri).",
                "アセットのタイトル下の説明文を翻訳（属性・パラメータ名は含まない）。",
                "에셋 제목 아래의 설명 텍스트를 번역(속성/매개변수 이름은 제외).",
                "Tłumaczy tekst opisu pod tytułem zasobu (bez nazw właściwości/parametrów).",
                "Traduz o texto de descrição abaixo do título do asset (exclui nomes de propriedades/parâmetros).",
                "Переводит текст описания под названием ассета (кроме названий свойств/параметров).",
                "翻译资产标题下方的描述文字（不含属性/参数名）。",
                "翻譯資產標題下方的描述文字（不含屬性/參數名）。");

            Add(t, "label.scope.gameCore",
                "Spiel-Kern", "Game core", "Núcleo del juego", "Cœur du jeu", "Nucleo del gioco", "ゲーム本体",
                "게임 본체", "Rdzeń gry", "Núcleo do jogo", "Основа игры", "游戏本体", "遊戲本體");

            Add(t, "desc.scope.gameCore",
                "Übersetzt den restlichen UI-Text des Basisspiels (Menüs, Infopanels, Tutorials usw.).",
                "Translates the rest of the base game’s UI text (menus, info panels, tutorials, etc.).",
                "Traduce el resto del texto de la interfaz del juego base (menús, paneles, tutoriales, etc.).",
                "Traduit le reste du texte de l’interface du jeu de base (menus, panneaux, tutoriels, etc.).",
                "Traduce il resto del testo dell’interfaccia del gioco base (menu, pannelli, tutorial, ecc.).",
                "ゲーム本体のその他すべてのUI文字（メニュー・情報パネル・チュートリアル等）を翻訳。",
                "게임 본체의 나머지 모든 UI 글(메뉴·정보 패널·튜토리얼 등)을 번역.",
                "Tłumaczy pozostały tekst interfejsu gry podstawowej (menu, panele, samouczki itp.).",
                "Traduz o resto do texto da interface do jogo base (menus, painéis, tutoriais etc.).",
                "Переводит остальной текст интерфейса базовой игры (меню, инфопанели, обучения и т.д.).",
                "翻译游戏本体的其余全部界面文字（菜单、信息面板、教程等）。",
                "翻譯遊戲本體的其餘全部介面文字（選單、資訊面板、教學等）。");

            // 需求3：「模组名称」去掉「实验性」并提到翻译范围第一排；说明也去掉「实验性功能」开头。
            // 语义变了，原来 12 语硬写的 de/es/fr/it/ja/ko/pl/pt/ru 全都对不上，改成 AddEnZh（三语手写 + 其余机翻兜底）。
            AddEnZh(t, "label.scope.modName", "Mod names", "模组名称", "模組名稱");

            AddEnZh(t, "desc.scope.modName",
                "Many mod titles are made-up words or abbreviations, so translating them can look worse than leaving them alone. Off by default.",
                "由于模组标题很多都奇奇怪怪，所以不推荐打开。",
                "由於模組標題很多都奇奇怪怪，所以不推薦打開。");

            // 需求3：改名为「游戏世界渲染文本（实验性）」，说明用玩家看得懂的话讲清它管的是 3D 世界里实时渲染的贴地文字。
            AddEnZh(t, "label.scope.worldLabels",
                "World-rendered text (experimental)", "游戏世界渲染文本（实验性）", "遊戲世界渲染文字（實驗性）");

            // 反馈4：刷新方式三档全部取消（玩家实测前两档看不出区别、第三档每次都卡），改成唯一一种行为，
            // 所以「游戏世界文字显示刷新方式」这个设置项连同它的枚举文案一起删除，原本那段解释全部并进下面这条说明 ——
            // 玩家只要在这一行看懂「什么时候会更新、什么时候会卡一下、翻译没赶上时怎么办」就够了。
            AddEnZh(t, "desc.scope.worldLabels",
                "Text drawn live inside the 3D game world and kept in the game’s own label cache — road names, district names, what other mods display, and so on.\n"
                + "How it works: as soon as you switch this on or enter a save, the mod reads the game’s language dictionary and sends off every road / district name in view that has not been translated yet, then forces one refresh when that first batch comes back. Expect one or two light hitches at that moment; after that there is almost no impact on play.\n"
                + "From then on every newly translated name updates by itself. Translation takes time, so if a road or district is finished before its name comes back the translation will not show yet — it appears right after your next interaction, for example moving the mouse over it.",
                "在3D游戏世界中实时渲染的文本，保存在游戏自己的标签缓存里，比如道路上显示的路名、区名以及其它模组的显示数据等。\n"
                + "工作方式：启用这一项或进入存档后，模组会立即读取语言字典，把所有还没翻译过的道路／区域名称送去翻译，这批翻译完成后会强制刷新一次。此时可能轻微卡顿 1 到 2 次，之后基本不影响游戏。\n"
                + "此后每有新的名称翻译完成，都会自动更新。受限于翻译速度，若新名称还没翻译完成时道路／区域就已经放置好了，画面上暂时不会显示译文，但下次操作后就会马上刷新，比如鼠标划过。",
                "在3D遊戲世界中即時渲染的文字，儲存在遊戲自己的標籤快取裡，比如道路上顯示的路名、區名以及其它模組的顯示資料等。\n"
                + "運作方式：啟用這一項或進入存檔後，模組會立即讀取語言字典，把所有還沒翻譯過的道路／區域名稱送去翻譯，這批翻譯完成後會強制重新整理一次。此時可能輕微卡頓 1 到 2 次，之後基本不影響遊戲。\n"
                + "此後每有新的名稱翻譯完成，都會自動更新。受限於翻譯速度，若新名稱還沒翻譯完成時道路／區域就已經放置好了，畫面上暫時不會顯示譯文，但下次操作後就會馬上重新整理，比如滑鼠劃過。");

            // ===== 选择引擎与测试（需求 1/6/7）=====
            Add(t, "label.test",
                "Engine-Verbindung testen", "Test engine connection", "Probar conexión del motor", "Tester la connexion du moteur", "Prova connessione motore", "エンジン接続をテスト",
                "엔진 연결 테스트", "Testuj połączenie silnika", "Testar conexão do motor", "Проверить подключение движка", "测试引擎连通性", "測試引擎連通性");

            Add(t, "desc.test",
                "Testet mit „Hello, world“ nur, ob der aktuelle Engine-Dienst erreichbar ist. Übersetzt NICHT die Oberfläche und ändert keinen Text.",
                "Only checks whether the current engine service is reachable, using “Hello, world”. Does NOT translate the interface or change any text.",
                "Solo comprueba si el servicio del motor actual es accesible, usando “Hello, world”. NO traduce la interfaz ni cambia ningún texto.",
                "Vérifie seulement si le service du moteur actuel est joignable, avec « Hello, world ». NE traduit PAS l’interface et ne modifie aucun texte.",
                "Verifica solo se il servizio del motore attuale è raggiungibile, usando “Hello, world”. NON traduce l’interfaccia né modifica alcun testo.",
                "「Hello, world」で現在のエンジンサービスに到達できるかだけを確認。界面は翻訳せず、任何文字も変更しません。",
                "‘Hello, world’로 현재 엔진 서비스에 연결되는지만 확인합니다. 인터페이스를 번역하지 않고 어떤 글도 바꾸지 않습니다.",
                "Sprawdza tylko, czy obecna usługa silnika jest osiągalna, używając „Hello, world”. NIE tłumaczy interfejsu i nie zmienia żadnego tekstu.",
                "Apenas verifica se o serviço do motor atual está acessível, usando “Hello, world”. NÃO traduz a interface nem altera nenhum texto.",
                "Проверяет только доступность текущего сервиса движка с помощью «Hello, world». НЕ переводит интерфейс и не меняет текст.",
                "只用 “Hello, world” 检测当前引擎服务能否连通，不翻译界面、不改动任何文字。",
                "只用 “Hello, world” 檢測目前引擎服務能否連通，不翻譯介面、不變動任何文字。");

            Add(t, "label.testResult",
                "Letztes Testergebnis", "Last test result", "Último resultado", "Dernier résultat", "Ultimo risultato", "前回のテスト結果",
                "마지막 테스트 결과", "Ostatni wynik testu", "Último resultado", "Последний результат", "上次测试结果", "上次測試結果");

            Add(t, "label.save",
                "Einstellungen speichern & übersetzen", "Save settings & translate", "Guardar ajustes y traducir", "Enregistrer et traduire", "Salva impostazioni e traduci", "設定を保存して翻訳",
                "설정 저장 및 번역", "Zapisz ustawienia i tłumacz", "Salvar configurações e traduzir", "Сохранить настройки и перевести", "保存设置并翻译", "保存設定並翻譯");

            // 反馈1：原来那段写的是「开关/范围/目标语言/key 写盘」外加一堆填 key 的小技巧，早就过时了 ——
            // 填 key 的提示如今在各输入框自己的说明里。改成玩家给定的两句原话；顺带把反馈5 删掉的
            // 「模组不会覆盖玩家修改过的名称」并到这里（那三处重复的提示行都拆了，这句话只剩这一个家）。
            AddEnZh(t, "desc.save",
                "Saves all settings and starts translating. The mod will never overwrite a name you have changed yourself.",
                "保存所有设置并开始翻译。模组不会覆盖玩家修改过的名称。",
                "保存所有設定並開始翻譯。模組不會覆蓋玩家修改過的名稱。");

            Add(t, "label.msKey",
                "Microsoft Azure-Schlüssel", "Microsoft Azure key", "Clave de Microsoft Azure", "Clé Microsoft Azure", "Chiave Microsoft Azure", "Microsoft Azure キー",
                "Microsoft Azure 키", "Klucz Microsoft Azure", "Chave Microsoft Azure", "Ключ Microsoft Azure", "微软 Azure key", "微軟 Azure key");

            Add(t, "desc.msKey",
                "portal.azure.com → Translator-Ressource erstellen (F0, kostenlos, 2 Mio. Zeichen/Monat) → „key1“ hier einfügen. Nur nötig, wenn die Engine auf Microsoft steht.",
                "portal.azure.com → create a Translator resource (F0, free, 2M chars/month) → paste “key1” here. Only needed when the engine is set to Microsoft.",
                "portal.azure.com → crea un recurso Translator (F0, gratis, 2M caracteres/mes) → pega aquí “key1”. Solo es necesario si el motor está en Microsoft.",
                "portal.azure.com → créez une ressource Translator (F0, gratuit, 2M caractères/mois) → collez « key1 » ici. Utile seulement si le moteur est sur Microsoft.",
                "portal.azure.com → crea una risorsa Translator (F0, gratuito, 2M caratteri/mese) → incolla qui “key1”. Serve solo se il motore è su Microsoft.",
                "portal.azure.com → Translatorリソース（F0無料・200万文字/月）を作成 → 「key1」をここに貼る。エンジンをMicrosoftにした場合のみ必要。",
                "portal.azure.com → Translator 리소스 생성(F0 무료, 200만 글자/월) → ‘key1’을 여기에 붙여넣기. 엔진을 Microsoft로 쓸 때만 필요.",
                "portal.azure.com → utwórz zasób Translator (F0, darmowy, 2 mln znaków/mies.) → wklej tu „key1”. Potrzebny tylko, gdy silnik to Microsoft.",
                "portal.azure.com → crie um recurso Translator (F0, grátis, 2M caracteres/mês) → cole “key1” aqui. Só é necessário se o motor for Microsoft.",
                "portal.azure.com → создайте ресурс Translator (F0, бесплатно, 2 млн симв./мес.) → вставьте «key1» сюда. Нужно только если движок — Microsoft.",
                "portal.azure.com 建 Translator 资源（F0 免费层、每月 200 万字符）→ 把 key1 粘贴到这里。仅当引擎选微软时才需要。",
                "portal.azure.com 建 Translator 資源（F0 免費層、每月 200 萬字元）→ 把 key1 貼到這裡。僅當引擎選微軟時才需要。");

            Add(t, "label.microsoftRegion",
                "Microsoft-Region", "Microsoft region", "Región de Microsoft", "Région Microsoft", "Area Microsoft", "Microsoft リージョン",
                "Microsoft 지역", "Region Microsoft", "Região da Microsoft", "Регион Microsoft", "微软区域(Region)", "微軟區域(Region)");

            Add(t, "desc.microsoftRegion",
                "Für einige Azure-Translator-Ressourcen (regional/Multi-Service) erforderlich, sonst kann 401 auftreten. Für globale Ressourcen oder bei Unsicherheit leer lassen.",
                "Required by some Azure Translator resources (regional/multi-service), otherwise you may get 401. Leave empty for Global resources or if unsure.",
                "Obligatorio para algunos recursos de Azure Translator (regionales/multiservicio); de lo contrario puede dar 401. Déjalo vacío para recursos globales o si no estás seguro.",
                "Requis par certaines ressources Azure Translator (régionales/multi-services), sinon une erreur 401 peut survenir. Laissez vide pour une ressource globale ou en cas de doute.",
                "Richiesto da alcune risorse Azure Translator (regionali/multi-servizio), altrimenti potresti ricevere 401. Lascia vuoto per risorse globali o se non sei sicuro.",
                "一部のAzure Translatorリソース（リージョン/マルチサービス）で必須。空だと401になることがあります。Globalリソースや不明な場合は空欄で構いません。",
                "일부 Azure Translator 리소스(지역/다중 서비스)에는 필수이며, 비워 두면 401이 발생할 수 있습니다. Global 리소스이거나 확실하지 않으면 비워 두세요.",
                "Wymagany przez niektóre zasoby Azure Translator (regionalne/wielousługowe), inaczej może wystąpić 401. Pozostaw puste dla zasobów globalnych lub w razie wątpliwości.",
                "Exigido por alguns recursos do Azure Translator (regionais/multisserviço); caso contrário pode ocorrer 401. Deixe vazio para recursos globais ou se não tiver certeza.",
                "Требуется для некоторых ресурсов Azure Translator (региональных/мульти-сервисных), иначе возможна ошибка 401. Оставьте пустым для глобальных ресурсов или если не уверены.",
                "部分 Azure 翻译资源（区域/多服务）必填，否则会 401。全球（Global）资源或不确定就留空。",
                "部分 Azure 翻譯資源（區域/多服務）必填，否則會 401。全球（Global）資源或不確定就留空。");

            Add(t, "label.deeplKey",
                "DeepL-Schlüssel", "DeepL key", "Clave DeepL", "Clé DeepL", "Chiave DeepL", "DeepL キー",
                "DeepL 키", "Klucz DeepL", "Chave DeepL", "Ключ DeepL", "DeepL key", "DeepL key");

            Add(t, "desc.deeplKey",
                "deepl.com → Gratis-Konto → Auth Key einfügen (Gratis-Schlüssel enden mit :fx). Nur nötig, wenn die Engine auf DeepL steht.",
                "deepl.com → free account → paste the Auth Key (free keys end with :fx). Only needed when the engine is set to DeepL.",
                "deepl.com → cuenta gratis → pega la Auth Key (las claves gratis terminan en :fx). Solo es necesario si el motor está en DeepL.",
                "deepl.com → compte gratuit → collez la clé d’auth (les clés gratuites finissent par :fx). Utile seulement si le moteur est sur DeepL.",
                "deepl.com → account gratuito → incolla l’Auth Key (le chiavi gratuite finiscono con :fx). Serve solo se il motore è su DeepL.",
                "deepl.com → 無料アカウント → Auth Keyを貼る（無料キーは :fx で終わる）。エンジンをDeepLにした場合のみ必要。",
                "deepl.com → 무료 계정 → Auth Key 붙여넣기(무료 키는 :fx로 끝남). 엔진을 DeepL로 쓸 때만 필요.",
                "deepl.com → darmowe konto → wklej Auth Key (darmowe klucze kończą się :fx). Potrzebny tylko, gdy silnik to DeepL.",
                "deepl.com → conta grátis → cole a Auth Key (chaves grátis terminam em :fx). Só é necessário se o motor for DeepL.",
                "deepl.com → бесплатный аккаунт → вставьте Auth Key (бесплатные ключи оканчиваются на :fx). Нужно только если движок — DeepL.",
                "deepl.com 注册免费账户 → 把 Auth Key 粘贴到这里（免费 key 通常以 :fx 结尾）。仅当引擎选 DeepL 时才需要。",
                "deepl.com 註冊免費帳戶 → 把 Auth Key 貼到這裡（免費 key 通常以 :fx 結尾）。僅當引擎選 DeepL 時才需要。");

            Add(t, "label.baiduAppId",
                "Baidu APP-ID", "Baidu APP ID", "APP ID de Baidu", "APP ID Baidu", "APP ID Baidu", "Baidu APP ID",
                "Baidu APP ID", "Baidu APP ID", "APP ID Baidu", "Baidu APP ID", "百度 APP ID", "百度 APP ID");

            Add(t, "desc.baiduAppId",
                "api.fanyi.baidu.com → registrieren → allgemeine Textübersetzung (Standard, kostenlos) aktivieren → APP-ID hier eintragen. Nur nötig, wenn die Engine auf Baidu steht.",
                "api.fanyi.baidu.com → register → enable general text translation (standard, free) → fill the APP ID here. Only needed when the engine is set to Baidu.",
                "api.fanyi.baidu.com → regístrate → activa la traducción de texto general (estándar, gratis) → llena aquí el APP ID. Solo es necesario si el motor está en Baidu.",
                "api.fanyi.baidu.com → inscrivez-vous → activez la traduction de texte générale (standard, gratuit) → remplissez l’APP ID ici. Utile seulement si le moteur est sur Baidu.",
                "api.fanyi.baidu.com → registrati → attiva la traduzione di testo generale (standard, gratis) → inserisci qui l’APP ID. Serve solo se il motore è su Baidu.",
                "api.fanyi.baidu.com → 登録 → 一般テキスト翻訳（標準・無料）を有効化 → APP IDをここに入力。エンジンをBaiduにした場合のみ必要。",
                "api.fanyi.baidu.com → 가입 → 일반 텍스트 번역(표준, 무료) 활성화 → APP ID를 여기에 입력. 엔진을 Baidu로 쓸 때만 필요.",
                "api.fanyi.baidu.com → zarejestruj → włącz ogólne tłumaczenie tekstu (standard, gratis) → wypełnij tu APP ID. Potrzebny tylko, gdy silnik to Baidu.",
                "api.fanyi.baidu.com → registre → ative a tradução de texto geral (padrão, grátis) → preencha aqui o APP ID. Só é necessário se o motor for Baidu.",
                "api.fanyi.baidu.com → регистрация → включите общий перевод текста (стандарт, бесплатно) → заполните APP ID здесь. Нужно только если движок — Baidu.",
                "api.fanyi.baidu.com 注册 → 开通通用文本翻译（标准版免费）→ 把 APP ID 填到这里。仅当引擎选百度时才需要。",
                "api.fanyi.baidu.com 註冊 → 開通通用文字翻譯（標準版免費）→ 把 APP ID 填到這裡。僅當引擎選百度時才需要。");

            Add(t, "label.baiduKey",
                "Baidu Geheimschlüssel", "Baidu secret key", "Clave secreta Baidu", "Clé secrète Baidu", "Chiave segreta Baidu", "Baidu 秘密鍵",
                "Baidu 비밀 키", "Tajny klucz Baidu", "Chave secreta Baidu", "Секретный ключ Baidu", "百度密钥", "百度金鑰");

            Add(t, "desc.baiduKey",
                "Geheimschlüssel der Baidu-Übersetzungsplattform (gehört zur APP-ID). Nur nötig, wenn die Engine auf Baidu steht.",
                "Secret key of the Baidu translate open platform (paired with the APP ID). Only needed when the engine is set to Baidu.",
                "Clave secreta de la plataforma abierta de traducción Baidu (junto con el APP ID). Solo es necesario si el motor está en Baidu.",
                "Clé secrète de la plateforme ouverte Baidu (associée à l’APP ID). Utile seulement si le moteur est sur Baidu.",
                "Chiave segreta della piattaforma aperta Baidu (abbinata all’APP ID). Serve solo se il motore è su Baidu.",
                "Baidu翻訳オープンプラットフォームの秘密鍵（APP IDと対）。エンジンをBaiduにした場合のみ必要。",
                "Baidu 번역 오픈 플랫폼 비밀 키(APP ID와 쌍). 엔진을 Baidu로 쓸 때만 필요.",
                "Tajny klucz otwartej platformy tłumaczeń Baidu (w parze z APP ID). Potrzebny tylko, gdy silnik to Baidu.",
                "Chave secreta da plataforma aberta de tradução Baidu (junto com o APP ID). Só é necessário se o motor for Baidu.",
                "Секретный ключ открытой платформы переводов Baidu (в паре с APP ID). Нужно только если движок — Baidu.",
                "百度翻译开放平台的密钥（与 APP ID 配对使用）。仅当引擎选百度时才需要。",
                "百度翻譯開放平台的金鑰（與 APP ID 配對使用）。僅當引擎選百度時才需要。");

            // ===== v0.31 新增引擎的凭据（需求1/2）=====
            // 必填项的「*」由 SettingsLocale 统一拼 label.required，标签正文里不写星号；选填项在括号里写明「选填」。
            AddEnZh(t, "label.mymemoryEmail", "MyMemory email (optional)", "MyMemory 邮箱（选填）", "MyMemory 信箱（選填）");
            AddEnZh(t, "desc.mymemoryEmail",
                "Optional. Without it the free quota is 5,000 chars/day; a valid email raises it to 50,000 chars/day. Only used when the engine is MyMemory.",
                "选填。不填是每天 5000 字符，填一个有效邮箱可提到每天 5 万字符。仅当引擎选 MyMemory 时才用得上。",
                "選填。不填是每天 5000 字元，填一個有效信箱可提到每天 5 萬字元。僅當引擎選 MyMemory 時才用得上。");

            AddEnZh(t, "label.yandexKey", "Yandex IAM token", "Yandex IAM 令牌", "Yandex IAM 權杖");
            AddEnZh(t, "desc.yandexKey",
                "console.cloud.yandex.ru → create an IAM token → paste it here. Yandex has shut down its keyless web channel, so a token is now required. Only used when the engine is Yandex.",
                "console.cloud.yandex.ru 建 IAM 令牌 → 粘贴到这里。Yandex 已经关掉了免 key 的网页通道，所以现在必须填令牌才用得了。仅当引擎选 Yandex 时才需要。",
                "console.cloud.yandex.ru 建 IAM 權杖 → 貼到這裡。Yandex 已經關掉了免 key 的網頁通道，所以現在必須填權杖才用得了。僅當引擎選 Yandex 時才需要。");

            AddEnZh(t, "label.geminiKey", "Google Gemini key", "Google Gemini key", "Google Gemini key");
            AddEnZh(t, "desc.geminiKey",
                "aistudio.google.com → get an API key → paste it here. Has a free tier and reads game context best of all engines. Only needed when the engine is Google Gemini.",
                "aistudio.google.com 申请 API key → 粘贴到这里。有免费额度，是所有引擎里最懂游戏语境的。仅当引擎选 Google Gemini 时才需要。",
                "aistudio.google.com 申請 API key → 貼到這裡。有免費額度，是所有引擎裡最懂遊戲語境的。僅當引擎選 Google Gemini 時才需要。");

            AddEnZh(t, "label.groqKey", "Groq Cloud key", "Groq Cloud key", "Groq Cloud key");
            AddEnZh(t, "desc.groqKey",
                "console.groq.com → create an API key → paste it here. Has a free tier and is the fastest AI option. Only needed when the engine is Groq Cloud.",
                "console.groq.com 建 API key → 粘贴到这里。有免费额度，是 AI 引擎里最快的。仅当引擎选 Groq Cloud 时才需要。",
                "console.groq.com 建 API key → 貼到這裡。有免費額度，是 AI 引擎裡最快的。僅當引擎選 Groq Cloud 時才需要。");

            AddEnZh(t, "label.openRouterKey", "OpenRouter key", "OpenRouter key", "OpenRouter key");
            AddEnZh(t, "desc.openRouterKey",
                "openrouter.ai → Keys → paste it here. After clicking “Fetch model list” every model your key can use is listed, with the free ones (names ending in :free) first; as long as you don't pick a paid model nothing is charged. Free models get delisted or run out of upstream capacity from time to time. Only needed when the engine is OpenRouter.",
                "openrouter.ai → Keys → 粘贴到这里。点「获取模型列表」后会列出你这个 key 能用的全部模型，免费档（名字以 :free 结尾）排在最前面；只要不选付费模型就不会产生费用。免费档也会被平台下架或挤满，换一个即可。仅当引擎选 OpenRouter 时才需要。",
                "openrouter.ai → Keys → 貼到這裡。按「取得模型清單」後會列出你這個 key 能用的全部模型，免費檔（名字以 :free 結尾）排在最前面；只要不選付費模型就不會產生費用。免費檔也會被平台下架或擠滿，換一個即可。僅當引擎選 OpenRouter 時才需要。");

            AddEnZh(t, "label.siliconFlowKey", "SiliconFlow key", "硅基流动 key", "矽基流動 key");
            AddEnZh(t, "desc.siliconFlowKey",
                "cloud.siliconflow.cn → API keys → paste it here. Several nominally free models, and it connects directly in mainland China — but even those are billed against your account balance. Only needed when the engine is SiliconFlow.",
                "cloud.siliconflow.cn → API 密钥 → 粘贴到这里。有多个标称免费的模型，中国大陆可直连；不过这些模型照样按账户余额扣费，余额为 0 就一直回「余额不足」。仅当引擎选硅基流动时才需要。",
                "cloud.siliconflow.cn → API 金鑰 → 貼到這裡。有多個標稱免費的模型，中國大陸可直連；不過這些模型照樣按帳戶餘額扣費，餘額為 0 就一直回「餘額不足」。僅當引擎選矽基流動時才需要。");

            // Cloudflare Workers AI 要两样：Account ID（定位到你的账号）+ API Token（鉴权）。
            AddEnZh(t, "label.cloudflareAccount", "Cloudflare Account ID", "Cloudflare 账户 ID", "Cloudflare 帳戶 ID");
            AddEnZh(t, "desc.cloudflareAccount",
                "dash.cloudflare.com → the Account ID on the right of the overview page. Only needed when the engine is Cloudflare Workers AI.",
                "dash.cloudflare.com 概览页右侧的 Account ID。仅当引擎选 Cloudflare Workers AI 时才需要。",
                "dash.cloudflare.com 概覽頁右側的 Account ID。僅當引擎選 Cloudflare Workers AI 時才需要。");

            AddEnZh(t, "label.cloudflareToken", "Cloudflare API token", "Cloudflare API 令牌", "Cloudflare API 權杖");
            AddEnZh(t, "desc.cloudflareToken",
                "dash.cloudflare.com → Profile → API Tokens → create one with Workers AI permission. A free daily allowance applies. Only needed when the engine is Cloudflare Workers AI.",
                "dash.cloudflare.com → 个人资料 → API 令牌 → 建一个带 Workers AI 权限的令牌。每天有免费额度。仅当引擎选 Cloudflare Workers AI 时才需要。",
                "dash.cloudflare.com → 個人資料 → API 權杖 → 建一個帶 Workers AI 權限的權杖。每天有免費額度。僅當引擎選 Cloudflare Workers AI 時才需要。");

            // ===== 自定义 AI 模型（需求1/2 + 反馈8）=====
            // 八家主流大模型的接口格式已内置（CustomAi.DefaultBaseUrl / Body / Parse），玩家只填自己的 key；
            // 第九项「自定义」则地址、模型名、接口格式全由玩家填，用来接任何 OpenAI/Anthropic/Gemini 兼容的服务。
            AddEnZh(t, "label.customVendor", "AI vendor", "AI 厂商", "AI 廠商");
            AddEnZh(t, "desc.customVendor",
                "Which vendor’s API format to speak. For the eight built-in vendors the request layout, auth header and address are already known, so you only need your own key. Pick “Custom” to fill in everything yourself.",
                "选按哪家的接口格式发请求。前八家的请求结构、鉴权头和地址都已内置，你只需要填自己的 key；选「自定义」则地址、模型名和接口格式全部自己填。",
                "選按哪家的介面格式發請求。前八家的請求結構、驗證標頭和位址都已內建，你只需要填自己的 key；選「自訂」則位址、模型名稱和介面格式全部自己填。");

            AddEnZh(t, "enum.vendor.gpt", "OpenAI GPT", "OpenAI GPT", "OpenAI GPT");
            AddEnZh(t, "enum.vendor.claude", "Anthropic Claude", "Anthropic Claude", "Anthropic Claude");
            AddEnZh(t, "enum.vendor.kimi", "Kimi (Moonshot)", "Kimi（月之暗面）", "Kimi（月之暗面）");
            AddEnZh(t, "enum.vendor.qwen", "Qwen (Alibaba)", "通义千问", "通義千問");
            AddEnZh(t, "enum.vendor.deepseek", "DeepSeek", "DeepSeek（深度求索）", "DeepSeek（深度求索）");
            AddEnZh(t, "enum.vendor.gemini", "Google Gemini", "Google Gemini", "Google Gemini");
            AddEnZh(t, "enum.vendor.grok", "xAI Grok", "xAI Grok", "xAI Grok");
            AddEnZh(t, "enum.vendor.glm", "GLM (Zhipu)", "智谱 GLM", "智譜 GLM");
            AddEnZh(t, "enum.vendor.custom", "Custom (fill in everything)", "自定义（地址、模型名自己填）", "自訂（位址、模型名稱自己填）");

            AddEnZh(t, "label.customProtocol", "API format", "接口格式", "介面格式");
            AddEnZh(t, "desc.customProtocol",
                "The shape of the request your endpoint expects. OpenAI-compatible covers most relays and local servers; pick Anthropic or Google Gemini only if your endpoint natively speaks one of those.",
                "你填的地址吃哪种请求格式。绝大多数中转站和本地服务都选 OpenAI 兼容；只有对方本身就是 Anthropic 或 Google Gemini 原生接口时才改选后两项。",
                "你填的位址吃哪種請求格式。絕大多數中轉站和本機服務都選 OpenAI 相容；只有對方本身就是 Anthropic 或 Google Gemini 原生介面時才改選後兩項。");

            AddEnZh(t, "enum.protocol.openai", "OpenAI-compatible", "OpenAI 兼容", "OpenAI 相容");
            AddEnZh(t, "enum.protocol.anthropic", "Anthropic Messages", "Anthropic Messages", "Anthropic Messages");
            AddEnZh(t, "enum.protocol.gemini", "Google Gemini", "Google Gemini", "Google Gemini");

            AddEnZh(t, "label.customBaseUrl", "API base URL", "接口地址", "介面位址");
            AddEnZh(t, "desc.customBaseUrl",
                "For the eight built-in vendors this is optional: fill it only when going through a proxy or a relay site, and leave it empty to use the vendor’s official address. For “Custom” it is required — it is the only address the mod has. Include the API prefix, e.g. https://example.com/v1. Every vendor keeps its own address; switching vendor will not carry this one over.",
                "前八家厂商选填：只有走代理或第三方中转站时才需要填，留空就用该厂商的官方地址。厂商选「自定义」时必填，这是模组唯一能拿到的地址。要填到接口前缀那一层，例如 https://example.com/v1。每家厂商各存一格，切到别家不会共用这一格的内容。",
                "前八家廠商選填：只有走代理或第三方中轉站時才需要填，留空就用該廠商的官方位址。廠商選「自訂」時必填，這是模組唯一能拿到的位址。要填到介面前綴那一層，例如 https://example.com/v1。每家廠商各存一格，切到別家不會共用這一格的內容。");

            AddEnZh(t, "label.customKey", "Your own API key", "自备 API key", "自備 API key");
            AddEnZh(t, "desc.customKey",
                "The key from your own account with the vendor chosen above. Quota and cost follow that account’s plan. Only needed when the engine is Custom AI model. Every vendor keeps its own key, and that vendor’s token counter restarts when you change it.",
                "你在上面所选厂商那边自己的 key，额度与费用按你账号的套餐算。仅当引擎选自定义AI模型时才需要。每家厂商各存一格，互不共用；改动这一家的 key，它已消耗的 token 数就从 0 重新计。",
                "你在上面所選廠商那邊自己的 key，額度與費用依你帳號的方案算。僅當引擎選自訂AI模型時才需要。每家廠商各存一格，互不共用；改動這一家的 key，它已消耗的 token 數就從 0 重新計。");

            AddEnZh(t, "label.customModelName", "Model name", "模型名称", "模型名稱");
            AddEnZh(t, "desc.customModelName",
                "The exact model name your endpoint expects, e.g. gpt-4o-mini or a local model’s id. Typed by hand here because a custom address does not always offer a model list to pick from.",
                "对方接口认的模型名，比如 gpt-4o-mini 或本地模型的 id。这里手填是因为自定义地址不一定提供模型列表可选。",
                "對方介面認得的模型名稱，例如 gpt-4o-mini 或本機模型的 id。這裡手填是因為自訂位址不一定提供模型清單可選。");

            AddEnZh(t, "label.customThinking", "Reasoning effort", "思考强度", "思考強度");
            // 用户定稿：这一句到此为止。原先还写了「下拉框只列这一家真正支持的档位」「关不掉的模型 Off＝最低档」
            // 「认不出模型代际就不发字段」「每档引擎各存自己那一格」「通义千问没有这一行」五条机制说明，
            // 他判定太啰嗦全部删掉。这些都仍然成立，只是不再对玩家解释（逐家真值表见交接文档 §10 第 58 条）。
            AddEnZh(t, "desc.customThinking",
                "How long the model thinks before it answers; the higher the effort, the slower it is.",
                "模型回答前先想多久，强度越高速度越慢。",
                "模型回答前先想多久，強度越高速度越慢。");

            // 「获取模型列表」按钮 + 结果显示行（需求2）。框架一行只放一个控件，所以按钮排在下拉框的上一行。
            AddEnZh(t, "label.fetchModels", "Fetch model list", "获取模型列表", "取得模型清單");
            AddEnZh(t, "desc.fetchModels",
                "Asks the platform which models your key may use and fills the dropdown below. Runs in the background, so the interface stays usable while it works.",
                "向平台查询你这个 key 能用哪些模型，取到后填进下面的下拉框。全程后台进行，不会卡住界面。",
                "向平台查詢你這個 key 能用哪些模型，取得後填進下面的下拉框。全程背景進行，不會卡住介面。");

            AddEnZh(t, "label.fetchModelsStatus", "Model list result", "模型列表获取结果", "模型清單取得結果");

            // 模型下拉框：平台的模型名一直在变，写死一个不靠谱 —— 首项给默认值，其余靠「获取模型列表」现拉。
            // 反馈9：这里不再写「哪几个引擎共用本项」。那是我们的内部实现，玩家既用不上也管不着。
            AddEnZh(t, "label.aiModel", "Model", "模型", "模型");
            AddEnZh(t, "desc.aiModel",
                "Which model to translate with. The default is preselected; “Fetch model list” then lists every model your key can use, free ones at the top, and within the free ones the ones that suit UI strings come first. If the top free model keeps failing, just pick another entry marked free.",
                "用哪个模型来翻译。已预选好默认值；点「获取模型列表」会列出你这个 key 能用的全部模型，免费档排在最前面，免费档里又以适合翻界面文字的排在最前。若排在最前面那个免费档一直失败，在下拉框里换一条标着「免费」的即可。",
                "用哪個模型來翻譯。已預選好預設值；按「取得模型清單」會列出你這個 key 能用的全部模型，免費檔排在最前面，免費檔裡又以適合翻介面文字的排在前面。若排在最前面那個免費檔一直失敗，在下拉框裡換一條標著「免費」的即可。");

            // 需求2：需注册的引擎在测试按钮旁边多一个「跳转注册页面」，用系统默认浏览器打开。
            AddEnZh(t, "label.openRegisterPage", "Open registration page", "跳转注册页面", "前往註冊頁面");
            AddEnZh(t, "desc.openRegisterPage",
                "Opens the current engine’s sign-up / API-key page in your system’s default browser. Hidden for engines that need no registration.",
                "用系统默认浏览器打开当前引擎的注册／申请 key 页面。免注册的引擎不会显示本按钮。",
                "用系統預設瀏覽器開啟目前引擎的註冊／申請 key 頁面。免註冊的引擎不會顯示本按鈕。");

            // ===== 日志与声明（需求 1/6）=====
            Add(t, "label.logPath",
                "Protokollpfad", "Log path", "Ruta del registro", "Chemin du journal", "Percorso del registro", "ログのパス",
                "로그 경로", "Ścieżka dziennika", "Caminho do log", "Путь к журналу", "日志路径", "日誌路徑");

            Add(t, "label.openLogFolder",
                "Protokollordner öffnen", "Open log folder", "Abrir carpeta del registro", "Ouvrir le dossier du journal", "Apri cartella del registro", "ログフォルダを開く",
                "로그 폴더 열기", "Otwórz folder dziennika", "Abrir pasta do log", "Открыть папку журнала", "打开日志文件夹", "打開日誌資料夾");

            Add(t, "desc.openLogFolder",
                "Öffnet den Ordner mit dem mod-eigenen Protokoll im Dateimanager — praktisch, um es bei Problemen zu teilen.",
                "Opens the folder containing this mod’s own log in the file manager — handy for sharing it when reporting problems.",
                "Abre en el explorador la carpeta con el registro propio de este mod, útil para compartirlo al reportar problemas.",
                "Ouvre dans l’explorateur le dossier contenant le journal propre de ce mod — pratique pour le partager en cas de problème.",
                "Apre nella gestione file la cartella con il registro di questa mod — utile per condividerlo quando segnali problemi.",
                "本Mod専用のログがあるフォルダをファイル管理で開きます。問題報告時の共有に便利。",
                "이 모드 전용 로그가 있는 폴더를 파일 관리자에서 엽니다. 문제 제보 시 공유에便利です.",
                "Otwiera w menedżerze plików folder z własnym dziennikiem tego moda — przydatne przy zgłaszaniu problemów.",
                "Abre no gerenciador de arquivos a pasta com o log próprio deste mod — útil para compartilhá-lo ao relatar problemas.",
                "Открывает в проводнике папку с собственным журналом этого мода — удобно делиться им при сообщении о проблемах.",
                "在系统文件管理器里打开本模组专属日志所在文件夹，方便反馈问题时取用。",
                "在系統檔案管理員裡打開本模組專屬日誌所在資料夾，方便回饋問題時取用。");

            Add(t, "label.clearCache",
                "Cache leeren", "Clear cache", "Borrar caché", "Vider le cache", "Svuota cache", "キャッシュを消去",
                "캐시 지우기", "Wyczyść pamięć podręczną", "Limpar cache", "Очистить кэш", "清除缓存", "清除快取");

            Add(t, "desc.clearCache",
                "Entfernt nur die Übersetzungsspuren – Einstellungen, Keys und andere Daten bleiben erhalten. Die Oberfläche kehrt vollständig zum Originaltext zurück, und der Hauptschalter „Übersetzung aktivieren“ wird ausgeschaltet, nach einem Neustart wird also nicht automatisch wieder übersetzt – zum Erneuten einfach wieder einschalten.",
                "Only clears translation traces — it does not delete settings, keys or any other data. The interface fully reverts to the original text, and Enable translation is switched off as well, so nothing starts translating again by itself after a restart. Turn the switch back on whenever you want to translate.",
                "Solo elimina los rastros de traducción; no borra ajustes, claves ni otros datos. La interfaz vuelve totalmente al texto original y además se apaga «Activar traducción», así que tras reiniciar no se volverá a traducir sola: vuelve a encenderla cuando quieras traducir.",
                "Supprime uniquement les traces de traduction ; ne supprime ni les réglages, ni les clés, ni d’autres données. L’interface revient entièrement au texte d’origine et « Activer la traduction » est éteint : rien ne se retraduit tout seul après un redémarrage. Rallumez-le quand vous voulez traduire.",
                "Rimuove solo le tracce di traduzione; non cancella impostazioni, chiavi né altri dati. L’interfaccia torna del tutto al testo originale e «Attiva traduzione» viene spento: dopo il riavvio nulla ricomincia a tradursi da solo. Riaccendilo quando vuoi tradurre.",
                "翻訳の痕跡のみを消去し、設定項目・Key などのデータは消しません。界面はすべて原文に戻り、「翻訳を有効化」も同時にオフになるため、再起動後に自動で翻訳が始まることはありません。翻訳し直したいときはスイッチを入れ直してください。",
                "번역 흔적만 지우고 설정 항목, Key 등 데이터는 지우지 않습니다. 인터페이스가 모두 원문으로 돌아가고 '번역 활성화'도 함께 꺼지므로 재시작 후에 자동으로 번역이 시작되지 않습니다. 다시 번역하려면 스위치를 켜면 됩니다.",
                "Usuwa wyłącznie ślady tłumaczenia; nie kasuje ustawień, kluczy ani innych danych. Interfejs wraca do oryginalnego tekstu, a „Włącz tłumaczenie” zostaje wyłączone, więc po restarcie nic nie zacznie tłumaczyć się samo. Włącz to z powrotem, gdy chcesz tłumaczyć.",
                "Remove apenas os vestígios de tradução; não apaga definições, chaves nem outros dados. A interface volta totalmente ao texto original e «Ativar tradução» é desligado, então nada recomeça a traduzir sozinho após reiniciar: ligue de novo quando quiser traduzir.",
                "Удаляет только следы перевода; настройки, ключи и прочие данные не стираются. Интерфейс целиком возвращается к исходному тексту, а «Включить перевод» выключается, поэтому после перезапуска ничего не начнёт переводиться само: включите обратно, когда снова захотите перевод.",
                "只清除翻译痕迹，不会清除设置项、Key等数据。界面全部回到原文，同时关掉「启用翻译」总开关，重启游戏后也不会自动开始翻译。想重新翻译，把总开关再打开就行。",
                "只清除翻譯痕跡，不會清除設定項、Key 等資料。介面全部回到原文，同時關閉「啟用翻譯」總開關，重啟遊戲後也不會自動開始翻譯。想重新翻譯，把總開關再打開就行。");

            // 需求7：声明文案由用户直接给定，逐字照抄，不改写不润色。
            // 旧的 label.disclaimer 已无引用（ReadEntries 把 Disclaimer 的标题直接映射到 text.disclaimer 全文），一并删掉。
            AddEnZh(t, "text.disclaimer",
                "This mod is in beta. If you run into problems, send the log to the discussion area. All keys are stored only on your own computer, and every engine defaults to its free tier — if you pick a paid model from the list, that is billed to your own account. Thanks to the local cache no text is ever translated twice, so actual usage stays very low. No translation can be guaranteed accurate — AI translation is usually more accurate than machine translation, but it can still mistranslate. All copyright in the original text remains with its owner.",
                "本模组处于beta测试阶段，如遇问题可发送日志到讨论区反馈。所有key都只保存在本地，且各引擎默认都只用免费额度；若在模型列表里选了付费模型，则按你自己的账户结算。由于本地缓存机制不需要二次重复翻译，实际额度消耗量也很低。所有翻译结果无法保证准确性，AI翻译通常比机器翻译准确，但同样可能出现错译的情况。原文版权均归原作者。",
                "本模組處於beta測試階段，如遇問題可發送日誌到討論區回饋。所有key都只保存在本機，且各引擎預設都只用免費額度；若在模型清單裡選了付費模型，則按你自己的帳戶結算。由於本地快取機制不需要二次重複翻譯，實際額度消耗量也很低。所有翻譯結果無法保證準確性，AI翻譯通常比機器翻譯準確，但同樣可能出現錯譯的情況。原文版權均歸原作者。");

            return t;
        }
    }

    // ===== 本模组界面文字的多语言兜底（需求 12）=====
    // 官方 12 种语言走 L10n 硬编码（精确、离线、即时）；
    // 若游戏切到不在这 12 种里的语言，则用当前配置的引擎把本模组文字机翻到该语言，
    // 结果写进 TranslationCache（命名空间 "self"，持久化、重启复用），并在翻好后刷新一次选项界面。
    internal static class SelfL10n
    {
        private const string CacheEngine = "self";
        private static readonly Dictionary<string, string> Machine = new Dictionary<string, string>(); // key -> 机翻文字
        private static string _machineLocale;      // Machine 里的文字翻成了哪个 locale
        private static int _running;               // 防止并发重复触发

        // 取本模组界面文字，优先级：该语言的硬编码手写译文 → 机翻结果 → 英文兜底。
        // v0.31 起新键只手写「英文 + 简繁中文」，所以官方语言也会落到机翻这一档（不再是「官方语言一律硬编码」）。
        public static string T(string locale, string key)
        {
            if (L10n.Has(locale, key)) return L10n.T(locale, key);
            lock (Machine)
            {
                if (string.Equals(_machineLocale, locale, StringComparison.OrdinalIgnoreCase) && Machine.TryGetValue(key, out var m) && !string.IsNullOrEmpty(m))
                    return m;
            }
            return L10n.T("en-US", key); // 机翻未就绪：先用英文兜底
        }

        // 这个 locale 还缺哪些键（硬编码没写、而英文源非空）。官方语言通常只缺 v0.31 新增的那批；非官方语言是全部。
        // 英文自己不翻（它就是机翻的输入源）；纯符号（如必填标记 " *"）也不值得花一次请求。
        internal static string[] MissingKeys(string locale)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(locale) || string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase)) return list.ToArray();
            foreach (string key in L10n.AllKeys)
            {
                if (L10n.Has(locale, key)) continue;
                string en = L10n.T("en-US", key);
                if (string.IsNullOrEmpty(en) || en == key || !HasLetter(en)) continue;
                list.Add(key);
            }
            return list.ToArray();
        }

        private static bool HasLetter(string s)
        {
            for (int i = 0; i < s.Length; i++) if (char.IsLetter(s[i])) return true;
            return false;
        }

        // 后台把本模组【缺译文】的界面文字机翻到当前游戏语言（只触发一次，之后走缓存）。
        // 触发条件不再是「非官方语言」，而是「这个语言还缺键」—— 官方语言也缺 v0.31 新增的那批。
        public static void EnsureSelfTranslation(string locale)
        {
            if (MissingKeys(locale).Length == 0) return;
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;

            new Thread(() =>
            {
                try { RunSelfTranslation(locale); }
                catch (Exception ex) { ModLog.Error("[自翻] 本模组界面机翻异常: " + ex.Message); }
                finally { Interlocked.Exchange(ref _running, 0); }
            }) { IsBackground = true, Name = "Cs2SelfL10n" }.Start();
        }

        private static void RunSelfTranslation(string locale)
        {
            // 等设置就绪（需要引擎/key）。
            TranslatorSetting setting = null;
            for (int i = 0; i < 60; i++)
            {
                setting = TranslatorSetting.Instance;
                if (setting != null) break;
                Thread.Sleep(500);
            }
            if (setting == null) return;

            ITranslationEngine engine = TranslationEngines.Get(setting.TranslationEngine);
            if (!engine.IsConfigured(setting))
            {
                ModLog.Info("[自翻] 当前引擎未配置 key，本模组界面文字暂用英文兜底（语言：" + locale + "）。");
                return;
            }

            int count = 0, fromCache = 0;
            // 只翻【缺】的那些键：官方语言通常就是 v0.31 新增的几十条，非官方语言才等于全表。
            foreach (string key in MissingKeys(locale))
            {
                string en = L10n.T("en-US", key);

                string val = TranslationCache.Get(CacheEngine, en, locale);
                if (val != null) { fromCache++; }
                else
                {
                    try { val = engine.Translate(setting, en, "auto", locale); }
                    catch (Exception ex) { ModLog.Error("[自翻] 翻译键失败 " + key + ": " + ex.Message); val = null; }
                    if (string.IsNullOrEmpty(val)) val = en; // 失败兜底英文
                    else TranslationCache.Put(CacheEngine, en, locale, val);
                    count++;
                    Thread.Sleep(80); // 轻微节流
                }

                lock (Machine) { _machineLocale = locale; Machine[key] = val; }
            }
            TranslationCache.Save();
            ModLog.Info($"[自翻] 本模组界面文字已机翻到 {locale}：新翻 {count} 条、命中缓存 {fromCache} 条。");

            // 翻好后刷新一次选项界面，让本模组文字立即变成该语言。
            try { Colossal.Core.MainThreadDispatcher.RunOnMainThread(Patches.RefreshVisibleText); } catch { }
        }
    }

    // 把上面表格里的文字，按官方 locale ID 喂给游戏的本地化系统。
    // 每个受支持的 locale 都注册一份，选项界面就会跟随游戏语言显示。
    public class SettingsLocale : IDictionarySource
    {
        private readonly TranslatorSetting m_Setting;
        private readonly string m_Locale;

        public SettingsLocale(TranslatorSetting setting, string locale)
        {
            m_Setting = setting;
            m_Locale = locale;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors, Dictionary<string, int> indexCounts)
        {
            // 用 SelfL10n.T：有手写译文的语言走硬编码，缺译文的键走机翻兜底（需求 12 / v0.31）。
            string L(string key) => SelfL10n.T(m_Locale, key);

            // 需求2：必填项名称末尾标「*」。后缀统一拼，不必为每种语言把标签文案各改一遍。
            string R(string key) => L(key) + L("label.required");

            // 需求2：引擎名 + 括号标注 —— 免注册的加「直接使用」，自定义 AI 加「消耗token」，其余不加括号。
            string EngineName(string name)
            {
                string n = L("enum." + name.ToLowerInvariant());
                if (EngineKit.IsNoSignup(name)) return n + L("enum.tag.direct");
                return name == "Custom" ? n + L("enum.tag.token") : n;
            }

            // 需求5 / 反馈6：引擎下拉框正下方那行常驻「说明：…」。tooltip 会随鼠标移开消失且没有滚动条，
            // 所以额度/限流/补充说明这类长文案改放这里。
            // 反馈6 的坑：以前只有一个 EngineNotes 属性、文案按【当前选中的引擎】在 ReadEntries 里现算，
            // 而一张 locale 字典只建一次 —— 算出来那一句当场冻住，玩家换引擎界面纹丝不动，
            // 只有「清除缓存」触发 ReloadActiveLocale 重建字典时才碰巧对上（所以用户说清了缓存就正常）。
            // 现在改成 13 个引擎各一行静态文案，配 [SettingsUIHideByCondition] 实时显隐：
            // RefreshPage() 每次都会重跑显隐判定，于是换引擎立刻换说明，不依赖任何字典重建。
            string Notes(string engine)
            {
                string note = L("notes." + engine);
                return string.IsNullOrEmpty(note) ? string.Empty : L("label.engineNotes") + note;
            }

            var d = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), L("name") },

                // 需求1：三个标签页（SettingsUISection 的第一个参数就是标签页 id）。
                { m_Setting.GetOptionTabLocaleID(TranslatorSetting.kTabTranslate), L("tab.translate") },
                { m_Setting.GetOptionTabLocaleID(TranslatorSetting.kTabEngine), L("tab.engine") },
                { m_Setting.GetOptionTabLocaleID(TranslatorSetting.kTabMisc), L("tab.misc") },

                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupMain), L("group.main") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupTarget), L("group.target") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupScope), L("group.scope") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupEngine), L("group.engine") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupTest), L("group.test") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupHotkeys), L("group.hotkeys") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupLog), L("group.log") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupDisclaimer), L("group.disclaimer") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.Enabled)), L("label.enabled") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.Enabled)), L("desc.enabled") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TranslationEngine)), L("label.engine") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.TranslationEngine)), L("desc.engine") },

                // 引擎下拉：14 项，顺序 = Engine 枚举声明顺序（免注册 → 需注册有免费额度 → 自定义AI）。
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Google), EngineName("Google") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.DuckDuckGo), EngineName("DuckDuckGo") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.MyMemory), EngineName("MyMemory") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Yandex), EngineName("Yandex") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Microsoft), EngineName("Microsoft") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.DeepL), EngineName("DeepL") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Baidu), EngineName("Baidu") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Gemini), EngineName("Gemini") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Groq), EngineName("Groq") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.OpenRouter), EngineName("OpenRouter") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.SiliconFlow), EngineName("SiliconFlow") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Cloudflare), EngineName("Cloudflare") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Custom), EngineName("Custom") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TargetLocale)), L("label.target") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.TargetLocale)), L("desc.target") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeModOptions)), L("label.scope.modOptions") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeModOptions)), L("desc.scope.modOptions") + L("note.modOptionsLimit") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeAssetNames)), L("label.scope.assetNames") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeAssetNames)), L("desc.scope.assetNames") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeAssetDescriptions)), L("label.scope.assetDesc") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeAssetDescriptions)), L("desc.scope.assetDesc") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeGameCore)), L("label.scope.gameCore") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeGameCore)), L("desc.scope.gameCore") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeModName)), L("label.scope.modName") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeModName)), L("desc.scope.modName") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeWorldLabels)), L("label.scope.worldLabels") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeWorldLabels)), L("desc.scope.worldLabels") },

                // 反馈6：13 行「说明：…」，标题一律注册成空串（只留右侧小字），文案按引擎名取。
                // 顺序 = Engine 枚举声明顺序，与 Setting.cs 里的属性声明顺序一致。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesGoogle)), Notes("google") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesDuckDuckGo)), Notes("duckduckgo") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesMyMemory)), Notes("mymemory") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesYandex)), Notes("yandex") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesMicrosoft)), Notes("microsoft") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesDeepL)), Notes("deepl") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesBaidu)), Notes("baidu") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesGemini)), Notes("gemini") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesGroq)), Notes("groq") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesOpenRouter)), Notes("openrouter") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesSiliconFlow)), Notes("siliconflow") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesCloudflare)), Notes("cloudflare") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.NotesCustom)), Notes("custom") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TestTranslation)), L("label.test") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.TestTranslation)), L("desc.test") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TestResult)), L("label.testResult") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.SaveConfig)), L("label.save") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.SaveConfig)), L("desc.save") },

                // 凭据：顺序与 Setting.cs 里的声明顺序一致（界面顺序即声明顺序），必填的用 R() 加「*」。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.MyMemoryEmail)), L("label.mymemoryEmail") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.MyMemoryEmail)), L("desc.mymemoryEmail") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.YandexKey)), R("label.yandexKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.YandexKey)), L("desc.yandexKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.MicrosoftKey)), R("label.msKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.MicrosoftKey)), L("desc.msKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.MicrosoftRegion)), L("label.microsoftRegion") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.MicrosoftRegion)), L("desc.microsoftRegion") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.DeepLKey)), R("label.deeplKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.DeepLKey)), L("desc.deeplKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.BaiduAppId)), R("label.baiduAppId") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.BaiduAppId)), L("desc.baiduAppId") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.BaiduKey)), R("label.baiduKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.BaiduKey)), L("desc.baiduKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.GeminiKey)), R("label.geminiKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.GeminiKey)), L("desc.geminiKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.GroqKey)), R("label.groqKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.GroqKey)), L("desc.groqKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.OpenRouterKey)), R("label.openRouterKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.OpenRouterKey)), L("desc.openRouterKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.SiliconFlowKey)), R("label.siliconFlowKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.SiliconFlowKey)), L("desc.siliconFlowKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.CloudflareAccountId)), R("label.cloudflareAccount") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.CloudflareAccountId)), L("desc.cloudflareAccount") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.CloudflareToken)), R("label.cloudflareToken") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.CloudflareToken)), L("desc.cloudflareToken") },

                // 自定义 AI：厂商下拉（八家内置接口格式 + 「自定义」）→ 接口格式 → 接口地址 → 自备 key
                // → 模型名称 → 思考强度。注册顺序 = Setting.cs 声明顺序 = 界面顺序。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.CustomVendor)), L("label.customVendor") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.CustomVendor)), L("desc.customVendor") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.gpt), L("enum.vendor.gpt") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.claude), L("enum.vendor.claude") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.kimi), L("enum.vendor.kimi") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.qwen), L("enum.vendor.qwen") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.deepseek), L("enum.vendor.deepseek") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.gemini), L("enum.vendor.gemini") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.grok), L("enum.vendor.grok") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.glm), L("enum.vendor.glm") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiVendor.custom), L("enum.vendor.custom") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.CustomProtocol)), L("label.customProtocol") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.CustomProtocol)), L("desc.customProtocol") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiProtocol.openai), L("enum.protocol.openai") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiProtocol.anthropic), L("enum.protocol.anthropic") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.AiProtocol.gemini), L("enum.protocol.gemini") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.CustomModelName)), L("label.customModelName") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.CustomModelName)), L("desc.customModelName") },

                // 思考强度的标题/说明按行注册见下方 thinkingRows（第八轮拆成 13 行，共用同一套文案）；
                // 档位选项本身见下方 ThinkLevelRows：第九轮一套档位集合一个枚举类型，且 12 种语言一律显示英文原词。

                // 需求2：「获取模型列表」按钮 + 结果行 + 模型下拉框（五个 AI 引擎共用）。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.FetchModels)), L("label.fetchModels") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.FetchModels)), L("desc.fetchModels") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.FetchModelsStatus)), L("label.fetchModelsStatus") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.AiModel)), L("label.aiModel") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.AiModel)), L("desc.aiModel") },

                // 需求2：「跳转注册页面」按钮，紧挨测试按钮。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.OpenRegisterPage)), L("label.openRegisterPage") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.OpenRegisterPage)), L("desc.openRegisterPage") },

                // 需求2：已消耗 token 数。整句话作为标题显示（bare string 的正文位置太窄）。
                // 反馈3：改成每家一行，循环注册见下方 VendorSuffixes。

                // 需求8：日志路径（bare string，值由 ModLog.FilePath 提供）+「打开日志文件夹」按钮。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.LogPath)), L("label.logPath") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.OpenLogFolder)), L("label.openLogFolder") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.OpenLogFolder)), L("desc.openLogFolder") },

                // 需求10：「清除缓存」按钮。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ClearCache)), L("label.clearCache") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ClearCache)), L("desc.clearCache") },

                // 需求9：[SettingsUIMultilineText] 不渲染正文，故把免责【全文放进标题】——标题即完整说明，用户能看到内容。
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.Disclaimer)), L("text.disclaimer") },
            };

            // 反馈5：改回框架内置的 [SettingsUIKeyboardBinding] 控件（玩家点中那行、直接按一个键就设好）。
            // 该控件自带「请按键」提示条，文案由框架给，我们这边只需注册标题与说明两个 ID。
            d[m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ToggleTranslationBinding))] = L("label.hotkey.toggle");
            d[m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ToggleTranslationBinding))] = L("desc.hotkey.toggle");
            d[m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.RetranslateBinding))] = L("label.hotkey.retranslate");
            d[m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.RetranslateBinding))] = L("desc.hotkey.retranslate");

            // 反馈3：AI 厂商的接口地址 / key / 已用 token 每家一行（界面上只有当前厂商那三行可见）。
            // 三行共用同一套文案，只有 token 数字按厂商取，所以在这里循环注册，不写 27 条字面量。
            // 后缀顺序必须与 CustomAi.Vendors、AiVendor 枚举以及 TranslatorSetting 里 UrlX/KeyX/TokX 的声明顺序一致。
            string[] vendorSuffixes = { "Gpt", "Claude", "Kimi", "Qwen", "Deepseek", "Gemini", "Grok", "Glm", "Custom" };
            for (int i = 0; i < vendorSuffixes.Length && i < CustomAi.Vendors.Length; i++)
            {
                string urlRow = "Url" + vendorSuffixes[i];
                string keyRow = "Key" + vendorSuffixes[i];
                string tokRow = "Tok" + vendorSuffixes[i];
                d[m_Setting.GetOptionLabelLocaleID(urlRow)] = L("label.customBaseUrl");
                d[m_Setting.GetOptionDescLocaleID(urlRow)] = L("desc.customBaseUrl");
                d[m_Setting.GetOptionLabelLocaleID(keyRow)] = R("label.customKey");
                d[m_Setting.GetOptionDescLocaleID(keyRow)] = L("desc.customKey");
                // 反馈5：数字【不再】写进标题 —— 这张字典一整局只建一次，写进来就冻在 0（就是这次报的 bug）。
                // 标题固定一句话，数字改由 Setting.cs 的 Tok* getter 每次界面刷新现读（同一机制见 Feedback 6 那段注释）。
                d[m_Setting.GetOptionLabelLocaleID(tokRow)] = L("label.tokens");
            }

            // 第八轮：思考强度拆成每档引擎、每家厂商各一行（四档直连大模型引擎 + 九家自定义厂商），界面上永远只有一行可见，
            // 文案完全相同，所以一次循环注册完。属性名用 nameof 取 —— 手打字符串写错一个字母，
            // 那行就会退回框架的默认标题（英文属性名），而这在编译期什么都不会说。
            // 第九轮：千问那一格取消（阿里云非流式调用不允许开思考，没有任何可调档位），所以从 13 行变 12 行。
            string[] thinkingRows =
            {
                nameof(TranslatorSetting.ThinkEngineGemini), nameof(TranslatorSetting.ThinkEngineGroq),
                nameof(TranslatorSetting.ThinkEngineOpenRouter), nameof(TranslatorSetting.ThinkEngineSiliconFlow),
                nameof(TranslatorSetting.ThinkGpt), nameof(TranslatorSetting.ThinkClaude),
                nameof(TranslatorSetting.ThinkKimi),
                nameof(TranslatorSetting.ThinkDeepseek), nameof(TranslatorSetting.ThinkVendorGemini),
                nameof(TranslatorSetting.ThinkGrok), nameof(TranslatorSetting.ThinkGlm),
                nameof(TranslatorSetting.ThinkVendorCustom),
            };
            foreach (string row in thinkingRows)
            {
                d[m_Setting.GetOptionLabelLocaleID(row)] = L("label.customThinking");
                d[m_Setting.GetOptionDescLocaleID(row)] = L("desc.customThinking");
            }

            // 档位选项词：第九轮按各家真实支持集合拆成七套枚举（一套集合一个类型），成员名就是玩家要看到的词。
            // ⚠ 这里【一律不经过 L()】：L() 在缺手写译文时会走 SelfL10n 机翻，把 Low/High 翻成「低/高」，
            //   而玩家对 off/low/medium/high/max 这些原词的识别度远高于当地语言的对应词，机翻还会让同一家在不同
            //   语言下词长不一样。所以直接钉成员名字面量 —— 12 种语言同一份。
            foreach (TranslatorSetting.AiThinkOpenAi v in Enum.GetValues(typeof(TranslatorSetting.AiThinkOpenAi)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();
            foreach (TranslatorSetting.AiThinkAnthropic v in Enum.GetValues(typeof(TranslatorSetting.AiThinkAnthropic)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();
            foreach (TranslatorSetting.AiThinkGemini v in Enum.GetValues(typeof(TranslatorSetting.AiThinkGemini)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();
            foreach (TranslatorSetting.AiThinkGrok v in Enum.GetValues(typeof(TranslatorSetting.AiThinkGrok)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();
            foreach (TranslatorSetting.AiThinkKimi v in Enum.GetValues(typeof(TranslatorSetting.AiThinkKimi)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();
            foreach (TranslatorSetting.AiThinkPlain v in Enum.GetValues(typeof(TranslatorSetting.AiThinkPlain)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();
            foreach (TranslatorSetting.AiThinkSilicon v in Enum.GetValues(typeof(TranslatorSetting.AiThinkSilicon)))
                d[m_Setting.GetEnumValueLocaleID(v)] = v.ToString();

            return d;
        }

        public void Unload() { }
    }
}
