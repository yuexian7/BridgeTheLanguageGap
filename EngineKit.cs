using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Cs2AutoTranslator
{
    // ===== 翻译引擎纯函数层（v0.31）=====
    // 为什么单独一个文件：13 个引擎每个都有自己的语言码规则、请求体形状、响应形状，
    // 这些全是纯字符串活；而 Mod.cs 里的引擎类在声明处就引用了游戏类型，CLR 加载类型即失败，
    // 离线壳（tests\t3）碰不到。搬到这里之后，「响应解析写错」这类历史上真出过四次的 bug
    // 可以在 3 秒内被断言抓住，不必开一局游戏。
    // 铁律：本文件【不得】引用 Unity / Game / Colossal 里的任何类型，也不得碰网络与磁盘。

    // ===== 引擎元数据 =====
    internal static class EngineKit
    {
        // 下拉框顺序 = TranslatorSetting.Engine 的枚举声明顺序。
        // 这里存一份镜像，纯粹为了让离线断言能校验「两边顺序一致」——枚举改了忘改文案是很容易的事。
        public static readonly string[] Order =
        {
            // 完全免费、不需要注册（显示名带「直接使用」）
            "Google", "DuckDuckGo", "MyMemory",
            // 需要注册、有免费额度
            "Yandex", "Microsoft", "DeepL", "Baidu", "Gemini", "Groq", "OpenRouter", "SiliconFlow", "Cloudflare",
            // 玩家自备 API（显示名带「消耗token」）
            "Custom",
        };

        // 不需要注册即可用的引擎：显示名后面加「直接使用」。
        // Yandex 原本在这一档，但它的免 key 网页通道已被官方关闭（详见 FreeApi.YandexHome 的注释），
        // 现在必须填 IAM token，所以移到「需注册」档，否则会骗玩家选一个注定失败的选项。
        public static bool IsNoSignup(string engineName)
        {
            return engineName == "Google" || engineName == "DuckDuckGo" || engineName == "MyMemory";
        }

        // 「跳转注册页面」按钮只对【需要注册】的引擎显示；免注册的和自定义 AI 都不显示（需求2）。
        public static string RegisterUrl(string engineName)
        {
            switch (engineName)
            {
                case "Microsoft": return "https://portal.azure.com/#create/Microsoft.CognitiveServicesTranslator";
                case "DeepL": return "https://www.deepl.com/pro-api";
                case "Baidu": return "https://api.fanyi.baidu.com/product/11";
                case "Gemini": return "https://aistudio.google.com/app/apikey";
                case "Groq": return "https://console.groq.com/keys";
                case "OpenRouter": return "https://openrouter.ai/settings/keys";
                case "SiliconFlow": return "https://cloud.siliconflow.cn/account/ak";
                case "Cloudflare": return "https://dash.cloudflare.com/profile/api-tokens";
                case "Yandex": return "https://console.cloud.yandex.ru/";
                default: return null;   // 免注册引擎与 Custom：没有注册页
            }
        }

        // 磁盘缓存命名空间。换引擎后旧缓存不该被误用，所以每个引擎一个独立前缀（沿用 v0.30 的 ms/deepl/baidu/google）。
        public static string CacheName(string engineName)
        {
            switch (engineName)
            {
                case "Microsoft": return "ms";
                case "DeepL": return "deepl";
                case "Baidu": return "baidu";
                case "Google": return "google";
                case "DuckDuckGo": return "ddg";
                case "MyMemory": return "mymemory";
                case "Yandex": return "yandex";
                case "Gemini": return "gemini";
                case "Groq": return "groq";
                case "OpenRouter": return "openrouter";
                case "SiliconFlow": return "siliconflow";
                case "Cloudflare": return "cfworkers";
                case "Custom": return "customai";
                default: return "unknown";
            }
        }

        // 该引擎的必填项（界面标签末尾的「*」由本地化文案自己带，这里只给「缺了就不能用」的判定）。
        // 返回值：0=可用，否则返回缺的字段名，用于日志与测试结果回显。
        // hasBaseUrl 默认 true：只有反馈8 那个「厂商=自定义」才让接口地址变成必填（没有官方地址可兜底），
        // 其余引擎一律不查它，所以默认值必须保持旧行为，调用方只在自由填模式下传真实值。
        public static string MissingField(string engineName, bool hasKey, bool hasSecond, bool hasModel, bool hasAccount, bool hasBaseUrl = true)
        {
            switch (engineName)
            {
                case "Google": case "DuckDuckGo": return null;              // 完全免配置
                case "MyMemory": return null;                               // 邮箱只是提额，选填
                case "Microsoft": case "DeepL": case "Gemini": case "Groq":
                case "Yandex": case "OpenRouter": case "SiliconFlow":
                    return hasKey ? null : "key";
                case "Baidu": return !hasKey ? "key" : (!hasSecond ? "appid" : null);
                case "Cloudflare": return !hasAccount ? "accountid" : (!hasKey ? "token" : null);
                case "Custom": return !hasBaseUrl ? "baseurl" : (!hasKey ? "key" : (!hasModel ? "model" : null));
                default: return null;
            }
        }

        // ===== 界面显隐真值表（需求2）=====
        // Setting.cs 里每个 SettingsUIHideByCondition 谓词都只读这两条，不要在设置类里散写 if ——
        // 散写了就脱离离线断言，而「选了 A 引擎却露出 B 引擎的 key 框」纯粹是查表错，壳里 3 秒能抓。

        // 「跳转注册页面」按钮：只对【需要注册】的引擎显示。
        // Yandex 现在也在这一档（免 key 网页通道已死），按钮指向 Yandex Cloud 控制台去申请 IAM token。
        public static bool ShowRegister(string engineName)
        {
            return !IsNoSignup(engineName) && RegisterUrl(engineName) != null;
        }

        // field ∈ key / second / model / thinking / account / email / vendor / baseurl。
        public static bool ShowField(string engineName, string field)
        {
            switch (field)
            {
                case "email": return engineName == "MyMemory";     // 选填，只为把 5000 字符/天提到 50000
                case "account": return engineName == "Cloudflare"; // Workers AI 要 Account ID + API Token 两样
                case "vendor":
                case "baseurl": return engineName == "Custom";     // 厂商/中转地址只有自定义 AI 才有
                case "model": return engineName == "Custom" || IsHostedAi(engineName);
                // 反馈4：思考强度从「只有自定义厂商能设」放开给全部大模型引擎。Cloudflare 是 m2m100 这类
                // 直翻小模型、没有思考预算这个概念；其余机翻引擎同理，都得藏起来。
                case "thinking": return engineName == "Custom" || engineName == "Gemini" || IsHostedAi(engineName);
                case "second": return engineName == "Baidu";       // 百度是 APP ID + 密钥
                case "key":
                    // 只有 Google / DuckDuckGo / MyMemory 完全不用填 key（MyMemory 那个框是 email，走上面的分支）。
                    // Yandex 的 IAM token 选填，框照样显示。
                    return engineName != "Google" && engineName != "DuckDuckGo" && engineName != "MyMemory";
                default: return false;
            }
        }

        // ===== 第八轮：思考强度按「哪档引擎 / 哪一家厂商」分格 =====
        // 以前全部引擎共用一个 AiThinking，切到别家就沿用上一家的档位 —— 与反馈3 那次的「9 家共用一个 key 框」同一类毛病。
        // 格位表放这里而不是 Setting.cs：谁占第几格是纯查表，跟上面那张显隐表同一性质，离线断言（t3 的 thinkingSlot）
        // 直接能抓；Setting.cs 只负责按格位存数组。
        // 前 9 格 = CustomAi.Vendors（自定义 AI 的九家），后 13 格 = EngineKit.Order（13 档引擎，只有显示这一行的那四档用得上）。
        // 铺满 13 格而不是只给四档：省掉「再维护一份四档名单」这条会过期的平行表，多花的 88 字节不算成本。
        // 落盘用的键名带引擎名/厂商名（EngineThinking_Gemini / VendorThinking_gpt）而不写下标 ——
        // 枚举与 Order 将来重排，老玩家的设置不会凭空挪到别家名下。
        public const int ThinkingSlots = 9 + 13;

        // 返回 -1 = 这一格界面上没有这一行，落盘也不必给它写键。两种原因：
        // 机翻引擎与 Cloudflare 根本没有思考字段（ShowField）；通义千问有字段但只允许「关」（第九轮，ThinkingLevels 回空串）。
        public static int ThinkingSlot(string engineName, string vendor)
        {
            if (!ShowField(engineName, "thinking")) return -1;
            if (engineName == "Custom")
            {
                string n = CustomAi.Normalize(vendor);
                for (int i = 0; i < CustomAi.Vendors.Length; i++)
                    if (CustomAi.Vendors[i] == n)
                        return CustomAi.ThinkingLevels(engineName, vendor).Length == 0 ? -1 : i;
                return 0;       // 认不出（游戏版本改了枚举）时回第 0 格，与 TranslatorSetting.VendorSlot 的兜底一致
            }
            if (CustomAi.ThinkingLevels(engineName, vendor).Length == 0) return -1;
            for (int i = 0; i < Order.Length; i++) if (Order[i] == engineName) return CustomAi.Vendors.Length + i;
            return -1;          // 引擎名不在 Order 里：这档引擎我们不认识，别给它编一个格位
        }

        // ===== 托管 AI 平台（需求1 的 Groq / OpenRouter / SiliconFlow）=====
        // 三家都是 OpenAI 兼容的 chat/completions，只有 base url 与「默认挑哪个模型」不同 ——
        // 所以 Mod.cs 里一个 OpenAiCompatEngine 类带参数实例化三次，不抄三份代码。
        // Gemini 不在这张表里：它的模型名走 URL 查询参数、鉴权也不同，单独一个引擎类。

        public static readonly string[] HostedAi = { "Groq", "OpenRouter", "SiliconFlow" };

        public static bool IsHostedAi(string engineName)
        {
            foreach (string x in HostedAi) if (x == engineName) return true;
            return false;
        }

        // ===== 反馈7：一次请求翻多条 =====
        // 只有「对话式」引擎开这个：它们本来就要包一层自然语言提示词，多带几行几乎不多一次往返。
        // Google / DuckDuckGo / MyMemory 是免费网页通道，接口一次只吃一条；
        // Microsoft / DeepL / Yandex / Baidu / Cloudflare 按字符配额计费，合并后不好对账，也一律单条。
        public static bool Batchable(string engineName)
        {
            switch (engineName)
            {
                case "Groq": case "OpenRouter": case "SiliconFlow": case "Custom": case "Gemini": return true;
                default: return false;
            }
        }

        public static string HostedBase(string engineName)
        {
            switch (engineName)
            {
                case "Groq": return "https://api.groq.com/openai/v1";
                case "OpenRouter": return "https://openrouter.ai/api/v1";
                case "SiliconFlow": return "https://api.siliconflow.cn/v1";
                default: return null;
            }
        }

        // 默认模型名。平台会下架模型，失效时只改这一处。
        // OpenRouter / SiliconFlow 的「永久免费」档玩家可自己改（需求1：可选永久免费模型），
        // 带 :free 后缀的是 OpenRouter 免费档的命名约定。
        // OpenRouter 会整档下架免费模型（v0.31 实测 llama-3.3-70b-instruct:free 已 404），
        // 玩家看到的就是「测试服务不可达、但获取模型列表正常」——因为 /models 不需要模型名。
        // 不用 key 就能复核：curl https://openrouter.ai/api/v1/models，挑一条带 :free 后缀、仍在列表里的。
        // 反馈1：光在列表里还不够 —— gemma-4-31b-it:free 名字有效、却在实机一直回 429
        // 「Provider returned error」（免费档共用一条上游产能通道，越热门越容易满）。
        // 反馈2（2026-09-11）：换成同族更小的 26b-a4b 也没用 —— 玩家实机反馈「排最前面那两条 google
        // 免费模型都不能用」。热门的不只是模型名，而是整条 google Gemma 通道，所以默认值要【离开这一家】。
        // ⚠ 不变式：默认值 = FreeFirst(OpenRouter, 当日免费列表) 的队首。改判据时必须跟着改这一格，
        // 否则「获取模型列表」自动改用第一项时会把玩家从默认档悄悄换到另一条。
        public static string HostedModel(string engineName)
        {
            switch (engineName)
            {
                case "Groq": return "llama-3.1-8b-instant";
                case "OpenRouter": return "nvidia/nemotron-3.5-lightning:free";
                case "SiliconFlow": return "Qwen/Qwen2.5-7B-Instruct";
                default: return null;
            }
        }

        // ===== 模型列表里的「免费档」识别（需求9 / 反馈7）=====
        // 平台不在响应里统一标注免费，只能按各家自己的约定判断：
        // OpenRouter 的免费档模型名固定带 ":free" 后缀（428 个模型里得先把免费的顶到前面）；
        // Groq 的 API 目前整个走免费额度，只有速率限制；
        // 硅基流动什么都不标，只能维护下面这张已知免费表 —— 它【会过时】，平台改免费档时改这一处。
        // 判断错的代价不对称：漏判只是没筛（全量列出），误判才会把付费模型当免费的给玩家，所以这张表宁短勿长。
        public static readonly string[] SiliconFlowFree =
        {
            "Qwen/Qwen2.5-7B-Instruct",
            "THUDM/glm-4-9b-chat",
            "internlm/internlm2_5-20b-chat",
            "TeleAI/TeleChat-7B-multimodal",
            "deepseek-ai/DeepSeek-R1-Distill-Qwen-7B",
            "deepseek-ai/DeepSeek-V2.5",
        };

        public static bool IsFreeModel(string engineName, string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return false;
            switch (engineName)
            {
                case "OpenRouter": return modelId.EndsWith(":free", StringComparison.Ordinal);
                case "Groq": return true;
                case "SiliconFlow":
                    foreach (string f in SiliconFlowFree) if (f == modelId) return true;
                    return false;
                default: return false;   // Custom / Gemini：玩家自备 key 或固定别名，不替他们猜
            }
        }

        // 反馈7：以前只把免费档放进下拉框，结果硅基流动 96 个模型只认出 1 个免费、下拉框等于没得选，
        // 玩家报的就是「模型列表还是无法选择」。改成【全量列出、免费的排在前面】：付费模型不再被藏起来，
        // 免费的那几个又一眼就能看到（Setting.cs 的 Item() 还会给它们加「（免费）」后缀）。
        // 付费组保持平台返回的原顺序（那就是它的推荐顺序）；免费组【不再】原样照抄 —— 见 FreeModelPenalty。
        public static List<string> FreeFirst(string engineName, List<string> models)
        {
            var all = new List<string>();
            if (models == null) return all;
            var free = new List<string>();
            foreach (string m in models) if (IsFreeModel(engineName, m)) free.Add(m);
            // 稳定排序：罚分相同的维持平台原顺序，不引入「同分随机」这种每次运行都不一样的下拉框。
            free = free.OrderBy(FreeModelPenalty).ToList();
            all.AddRange(free);
            foreach (string m in models) if (!IsFreeModel(engineName, m)) all.Add(m);
            return all;
        }

        // ===== 免费档内部的「适不适合翻界面文字」排序（反馈1）=====
        // 实机证明跑不通的档（2026-09-11 玩家实测：列表里 google 那两条 Gemma 免费档都不能用；
        // 上一轮日志里 31b 一直是 429）。这里只【降权】不删除 —— 平台哪天产能回来了不必改代码，
        // 玩家想手动试也照样试得到。判据是实测，不是平台文档，所以别拿它当永久事实。
        public static readonly string[] SaturatedFree =
        {
            "google/gemma-4-31b-it:free",
            "google/gemma-4-26b-a4b-it:free",
        };

        // 平台自己的排序按热度/新旧给，对翻译这个任务不一定合适。OpenRouter 实测 18 个免费档里：
        //   · code / content-safety / fin 是专用微调 —— 会把道路名当代码或金融术语翻，或者直接拒答；
        //   · 2.6b / -xs / nano / mini / small 这类超小模型会把批量请求的【行编号】抄错（编号一错整批判废）；
        //   · reasoning 是思考模型，每句多想要几秒，正是玩家反馈的「慢」；
        //   · ultra（550B）单句要等好几秒；preview 随时下架。
        //   · 反过来 -it / instruct / chat 与平台标了低延迟的 lightning / instant / turbo 往前放，
        //     前者最擅长「照编号回行」，后者就是为快而生的。
        // 只排序、【不删】：玩家的 key 能用什么模型由平台和他自己决定，我们替他删就是越权。
        // 分数只用于排序，越大越靠后；可能为负（指令微调的加分）。
        public static int FreeModelPenalty(string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return 0;
            string m = modelId.ToLowerInvariant();
            int p = 0;
            // 实测跑不通的档（不是平台文档说的，是我们自己在实机日志里拿到的 429/404）。
            // 只降权、不删除：玩家仍能在下拉框里手动选到，万一哪天又通了也不必改代码。
            foreach (string bad in SaturatedFree)
                if (string.Equals(m, bad, StringComparison.Ordinal)) p += 150;
            if (m.Contains("code") || m.Contains("safety") || m.Contains("moderat")
                || m.Contains("-fin") || m.Contains("finance")) p += 100;
            if (m.Contains("reasoning") || m.Contains("think") || m.Contains("ultra")) p += 60;
            if (m.Contains("preview")) p += 40;
            if (m.Contains("nano") || m.Contains("-xs") || m.Contains("2.6b") || m.Contains("1.5b")
                || m.Contains("0.5b") || m.Contains("lite") || m.Contains("mini") || m.Contains("small")) p += 30;
            if (m.Contains("omni")) p += 20;
            // 加分项：指令微调档，以及平台专门标出来「低延迟」的那条线（界面文字要的就是快）。
            if (m.Contains("-it") || m.Contains("instruct") || m.Contains("chat")
                || m.Contains("lightning") || m.Contains("instant") || m.Contains("turbo")) p -= 20;
            return p;
        }

        // ===== 失败分类（反馈1/2）=====
        // 状态码决定「这条还要不要再往下试」：429 与超时是排队，退避后重试就行；
        // 401/402/403/404 是账号或模型层面的【硬结论】，再试 12 次也只是把玩家额度白烧一遍、
        // 把整条队列拖住 —— 实测硅基流动的 402「余额不足」和 OpenRouter 的 404「此模型无免费档」
        // 都被旧逻辑当成网络抖动重试了整轮。
        public static bool IsPermanentStatus(int status)
            => status == 401 || status == 402 || status == 403 || status == 404;

        // 状态码 → 给玩家/日志看的人话。返回 null = 这个码没有更具体的说法（沿用通用文案）。
        public static string DescribeStatus(int status)
        {
            switch (status)
            {
                case 401: case 403: return "key 不对，或这个 key 没有调用该模型的权限";
                case 402: return "账户余额不足（硅基流动标称免费的模型也要账户里有余额才跑得动）";
                case 404: return "模型已下架，或这个模型其实没有免费档";
                case 429: return "请求太频繁被平台限流（免费档最容易撞上）";
                default: return null;
            }
        }

        // ===== 三个统一分发入口 =====
        // 「引擎名 → 该用哪个解析器」这张表只存在这一处。Mod.cs 里 14 个引擎类只管发请求与读 key，
        // 解析一律走这里 —— 离线断言于是能拿【真实响应样本】直接测完整条链路，不必开游戏。
        // engineName 用枚举名字符串（与 Order/CacheName/RegisterUrl 同一套），vendor 仅 Custom 有意义。
        // userProtocol 同样只对 Custom 有意义，且只在厂商选「自定义」时才生效（反馈8）：那时请求体形状由玩家定，
        // 响应形状自然也得跟着走，否则会拿 OpenAI 的解析器去读 Anthropic 的响应，永远读不出译文。

        public static string ParseResponse(string engineName, string vendor, string resp, string userProtocol = null)
        {
            if (string.IsNullOrEmpty(resp)) return null;
            switch (engineName)
            {
                case "Google": return FreeApi.GoogleParse(resp);
                case "DuckDuckGo": return FreeApi.DuckDuckGoParse(resp);
                case "MyMemory": return FreeApi.MyMemoryParse(resp);
                case "Yandex": return FreeApi.YandexParse(resp);
                // Azure：[{"translations":[{"text":"…","to":"zh-Hans"}]}]；DeepL：{"translations":[{"text":"…"}]}
                case "Microsoft": case "DeepL": return Json.FirstString(resp, "text");
                case "Baidu": return Json.FirstString(resp, "dst");
                case "Gemini": return GeminiApi.Parse(resp);
                case "Groq": case "OpenRouter": case "SiliconFlow": return OpenAiChat.Parse(resp);
                case "Cloudflare": return CloudflareAi.Parse(resp);
                case "Custom": return CustomAi.ParseFor(CustomAi.Protocol(vendor, userProtocol), resp);
                default: return null;
            }
        }

        // 平台回了 usage 就取真值，回了 -1 表示「这家不给」，调用方转本地估算。
        public static long Usage(string engineName, string vendor, string resp, string userProtocol = null)
        {
            if (string.IsNullOrEmpty(resp)) return -1;
            switch (engineName)
            {
                case "Gemini": return GeminiApi.Usage(resp);
                case "Groq": case "OpenRouter": case "SiliconFlow": return OpenAiChat.Usage(resp);
                case "Custom": return CustomAi.UsageFor(CustomAi.Protocol(vendor, userProtocol), resp);
                default: return -1;   // 机器翻译通道与 Cloudflare 都不报 token
            }
        }

        // 「获取模型列表」：Gemini 与 OpenAI 系响应形状不同，Custom 还要再按厂商分。
        public static List<string> ParseModels(string engineName, string vendor, string resp)
        {
            switch (engineName)
            {
                case "Gemini": return GeminiApi.Models(resp);
                case "Groq": case "OpenRouter": case "SiliconFlow": return OpenAiChat.Models(resp);
                case "Custom": return CustomAi.ParseModels(vendor, resp);
                default: return new List<string>();
            }
        }

        // 该引擎失败时能从响应里捞出的原因（只这几家把错误写在 body 里而不是 HTTP 状态码上）。
        public static string ErrorDetail(string engineName, string resp)
        {
            switch (engineName)
            {
                case "MyMemory": return FreeApi.MyMemoryError(resp);
                case "Yandex": return FreeApi.YandexError(resp);
                case "Cloudflare": return CloudflareAi.Error(resp);
                default: return Json.FirstString(resp, "error");
            }
        }
    }

    // ===== 各引擎语言码映射 =====
    // 从 Mod.cs 的 LangMap 搬来并扩充：搬过来之后才第一次能被离线断言覆盖。
    // 入参 locale 是游戏/本模组的语言标识（zh-HANS、en-US、ja-JP、pt-BR…），大小写与地区后缀都要能吃。
    internal static class Lang
    {
        private static string Norm(string s) => (s ?? "").Trim().ToLowerInvariant();

        // 去掉地区后缀：en-US → en，pt-BR → pt。zh 例外（简繁必须保留）。
        private static string Base(string s)
        {
            string n = Norm(s);
            if (n.StartsWith("zh-", StringComparison.Ordinal)) return n;
            int dash = n.IndexOf('-');
            return dash > 0 ? n.Substring(0, dash) : n;
        }

        // 目标语言的母语名，直接进 AI 提示词（LanguageCatalog 已有现成的，不再手抄一份表）。
        public static string NativeName(string locale)
        {
            string n = Norm(locale);
            foreach (var lang in LanguageCatalog.All)
                if (Norm(lang.code) == n) return lang.name;
            // 游戏当前语言可能是 en-US 这种带地区的写法，目录里只有 en：按 base 再找一次。
            string b = Base(locale);
            foreach (var lang in LanguageCatalog.All)
                if (Norm(lang.code) == b) return lang.name;
            return string.IsNullOrEmpty(locale) ? "English" : locale;
        }

        public static string Microsoft(string locale)
        {
            string n = Norm(locale);
            if (n == "zh-hans" || n == "zh-cn") return "zh-Hans";
            if (n == "zh-hant" || n == "zh-tw") return "zh-Hant";
            if (n == "pt-br") return "pt-br";
            if (n == "pt" || n == "pt-pt") return "pt-pt";
            return Base(locale);
        }

        public static string DeepL(string locale)
        {
            if (Norm(locale) == "pt-br") return "PT-BR";
            switch (Base(locale))
            {
                case "zh-hans": case "zh-hant": return "ZH";
                case "en": return "EN";
                case "ja": return "JA";
                case "ko": return "KO";
                case "fr": return "FR";
                case "de": return "DE";
                case "es": return "ES";
                case "it": return "IT";
                case "pl": return "PL";
                case "pt": return "PT-PT";
                case "ru": return "RU";
                case "uk": return "UK";
                case "nl": return "NL";
                case "sv": return "SV";
                case "da": return "DA";
                case "no": case "nb": return "NB";
                case "fi": return "FI";
                case "tr": return "TR";
                case "cs": return "CS";
                case "sk": return "SK";
                case "hu": return "HU";
                case "ro": return "RO";
                case "bg": return "BG";
                case "sl": return "SL";
                case "lt": return "LT";
                case "lv": return "LV";
                case "et": return "ET";
                case "id": return "ID";
                // hr / sr 不在 DeepL 的目标语言表里，发过去是 400；退回英文至少能出结果。
                default: return "EN";
            }
        }

        public static string Baidu(string locale)
        {
            switch (Base(locale))
            {
                case "zh-hans": return "zh";
                case "zh-hant": return "cht";
                case "en": return "en";
                case "ja": return "jp";
                case "ko": return "kor";
                case "fr": return "fra";
                case "de": return "de";
                case "es": return "spa";
                case "it": return "it";
                case "pt": return "pt";
                case "ru": return "ru";
                case "uk": return "ukr";
                case "pl": return "pl";
                case "nl": return "nl";
                case "sv": return "swe";
                case "da": return "dan";
                case "no": case "nb": return "nor";
                case "fi": return "fin";
                case "tr": return "tr";
                case "cs": return "cs";
                case "hu": return "hu";
                case "ro": return "rom";
                case "bg": return "bul";
                case "hr": return "hrv";
                case "sr": return "srp";
                case "th": return "th";
                case "vi": return "vie";
                case "id": return "id";
                default: return "en";
            }
        }

        // 谷歌 gtx 网页接口：直接吃 ISO 码，简繁要区分，希伯来语用旧码 iw，菲律宾语用 tl 而不是 fil。
        public static string Google(string locale)
        {
            string n = Norm(locale);
            if (n == "zh-hans" || n == "zh-cn") return "zh-CN";
            if (n == "zh-hant" || n == "zh-tw") return "zh-TW";
            if (n == "he" || n == "iw") return "iw";
            if (n == "fil") return "tl";
            return Base(locale);
        }

        // DuckDuckGo：简繁用 zh-Hans / zh-Hant（注意大小写是 ans/ant），其余吃 ISO 639-1。
        public static string DuckDuckGo(string locale)
        {
            string n = Norm(locale);
            if (n == "zh-hans" || n == "zh-cn") return "zh-Hans";
            if (n == "zh-hant" || n == "zh-tw") return "zh-Hant";
            if (n == "fil") return "tl";
            return Base(locale);
        }

        // MyMemory：RFC3066 风格，简繁必须带地区（zh-CN / zh-TW），否则它只回一种。
        public static string MyMemory(string locale)
        {
            string n = Norm(locale);
            if (n == "zh-hans" || n == "zh-cn") return "zh-CN";
            if (n == "zh-hant" || n == "zh-tw") return "zh-TW";
            if (n == "pt-br") return "pt-BR";
            if (n == "pt" || n == "pt-pt") return "pt-PT";
            if (n == "en" || n == "en-us") return "en-US";
            if (n == "en-gb") return "en-GB";
            if (n == "tl" || n == "fil") return "tl-PH";
            return Base(locale);   // 裸码（ja / ru / fr）MyMemory 也认
        }

        // Yandex：只有 zh 一种中文（不分简繁），其余吃 ISO 639-1。
        public static string Yandex(string locale)
        {
            if (Norm(locale).StartsWith("zh", StringComparison.Ordinal)) return "zh";
            if (Norm(locale) == "fil") return "tl";
            return Base(locale);
        }

        // Cloudflare @cf/meta/m2m100-1.2b 的语言码。
        // 名字里有 m2m100，但它【不吃】M2M-100 那套「三字母 + 文字系统」码：填 zho_Hans / jpn_Jpan 会回 422
        // "Invalid data for input"。下面这张表是逐字抄自服务端 422 响应里的 allowed 列表（语言全名 + ISO 639-1 两位码），
        // 不是猜的 —— 它支持 no（挪威语）却不支持 eu（巴斯克语），跟 M2M-100 的覆盖面并不一样。
        // 中文只有一种（zh），服务端不区分简繁；不支持的语言返回 null，调用方必须显式报错，
        // 绝不能悄悄退回英文把巴斯克语翻成英语。
        public static string Cloudflare(string locale)
        {
            switch (Base(locale))
            {
                case "zh-hans": case "zh-cn": case "zh-hant": case "zh-tw": return "zh";
                case "en": return "en";
                case "ja": return "ja";
                case "ko": return "ko";
                case "fr": return "fr";
                case "de": return "de";
                case "es": return "es";
                case "it": return "it";
                case "pt": return "pt";
                case "ru": return "ru";
                case "uk": return "uk";
                case "pl": return "pl";
                case "nl": return "nl";
                case "sv": return "sv";
                case "da": return "da";
                case "no": return "no";
                case "fi": return "fi";
                case "is": return "is";
                case "tr": return "tr";
                case "cs": return "cs";
                case "sk": return "sk";
                case "hu": return "hu";
                case "ro": return "ro";
                case "bg": return "bg";
                case "hr": return "hr";
                case "sr": return "sr";
                case "sl": return "sl";
                case "lt": return "lt";
                case "lv": return "lv";
                case "et": return "et";
                case "vi": return "vi";
                case "id": return "id";
                case "ms": return "ms";
                case "tl": return "tl";
                case "kk": return "kk";
                case "az": return "az";
                case "ca": return "ca";
                case "gl": return "gl";
                case "sw": return "sw";
                case "af": return "af";
                default: return null;   // eu / la 等不在服务端 allowed 列表里：明确失败，不要静默翻错
            }
        }

        // AI 引擎（Gemini/Groq/OpenRouter/SiliconFlow/自定义）不需要语言码，提示词里直接写母语名。
        public static string ForAiPrompt(string locale) => NativeName(locale);
    }

    // ===== AI 翻译提示词 =====
    // 关键约束：只要译文，不要解释、不要引号。占位符已被 TransGuard 掩成私有区哨兵，
    // 所以提示词要求「私有区字符原样保留」，这样 Unmask 才还原得回来。
    internal static class AiPrompt
    {
        public static string System(string langName)
        {
            return "You are the translation engine of a city-building game. "
                 + "Translate the user's text into " + (string.IsNullOrEmpty(langName) ? "English" : langName) + ". "
                 + "Output ONLY the translation itself: no quotes, no notes, no explanations, no language labels. "
                 + "Keep every character in the Unicode Private Use Area (U+E000-U+F8FF) exactly where it is, unchanged. "
                 + "Keep line breaks. If the text is already in the target language, return it unchanged.";
        }

        public static string User(string text) => text ?? "";

        // 反馈7：批量请求正文。刻意做成「一条 user 消息里自带指令 + 编号块」，这样三种接口的
        // Body/Parse 全都复用现成的单条通道，不必给每个引擎再加一套批量实现（改一处忘三处的风险直接消掉）。
        // 编号是唯一的对齐依据，所以下面把「不许增删/合并/改序/翻译编号」反复说了三遍；
        // 模型只要没照做，BatchKit.Split 就整批作废、退回单条，绝不猜哪句属于哪条。
        public static string BatchUser(string[] texts, string langName)
        {
            string lang = string.IsNullOrEmpty(langName) ? "English" : langName;
            var sb = new StringBuilder();
            sb.Append("Translate each numbered line below into ").Append(lang).Append(". ")
              .Append("Reply with exactly ").Append(texts.Length).Append(" lines, in the same order. ")
              .Append("Every reply line must start with the same number and \". \" prefix as its input line, ")
              .Append("followed only by the translation of that line. ")
              .Append("Do not translate, add, drop, merge, split or reorder the numbered lines. ")
              .Append("No quotes, no notes, no explanations, no blank lines.\n")
              .Append(BatchKit.Join(texts));
            return sb.ToString();
        }
    }

    // ===== OpenAI 兼容 chat/completions =====
    // 适用：Groq、OpenRouter、SiliconFlow，以及自定义 AI 里的 6 家。
    internal static class OpenAiChat
    {
        public static string Url(string baseUrl, string path)
        {
            string b = (baseUrl ?? "").Trim().TrimEnd('/');
            if (b.Length == 0) b = "https://api.openai.com/v1";
            return b + "/" + (string.IsNullOrEmpty(path) ? "chat/completions" : path.TrimStart('/'));
        }

        public static string Body(string model, string text, string langName) => Body(model, text, langName, null);

        // 第九轮：思考强度这段 JSON 由 CustomAi.ThinkingField 按各家口径拼好，这里只负责插进 messages 之前。
        // 传空串 = 【完全不写任何思考字段】，请求体与第九轮之前逐字一致 —— 不认识这些字段的网关会直接 400。
        public static string Body(string model, string text, string langName, string thinkingFragment)
        {
            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Json.Escape(model)).Append("\",");
            sb.Append("\"temperature\":0,\"stream\":false,");
            if (!string.IsNullOrEmpty(thinkingFragment))
                sb.Append(thinkingFragment).Append(',');
            sb.Append("\"messages\":[");
            sb.Append("{\"role\":\"system\",\"content\":\"").Append(Json.Escape(AiPrompt.System(langName))).Append("\"},");
            sb.Append("{\"role\":\"user\",\"content\":\"").Append(Json.Escape(AiPrompt.User(text))).Append("\"}");
            sb.Append("]}");
            return sb.ToString();
        }

        // {"choices":[{"message":{"role":"assistant","content":"译文"}}]}
        public static string Parse(string resp)
        {
            string choices = Json.FindContainer(resp, "choices");
            string content = choices != null ? Json.FirstString(choices, "content") : null;
            if (string.IsNullOrEmpty(content)) content = Json.FirstString(resp, "content");
            return string.IsNullOrEmpty(content) ? null : content.Trim();
        }

        // {"usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
        public static long Usage(string resp)
        {
            long total = Json.ReadLong(resp, "total_tokens", -1);
            if (total >= 0) return total;
            long a = Json.ReadLong(resp, "prompt_tokens", -1);
            long b = Json.ReadLong(resp, "completion_tokens", -1);
            return a >= 0 && b >= 0 ? a + b : -1;
        }

        // {"data":[{"id":"model-a"},{"id":"model-b"}]}
        public static List<string> Models(string resp)
        {
            var list = Json.AllStrings(resp, "id");
            // Gemini 的列表形状不同（models/{name}），OpenRouter 会带 "canonical_slug"，都已在各自分支处理。
            return list;
        }
    }

    // ===== Google Gemini（generateContent）=====
    internal static class GeminiApi
    {
        public const string DefaultBase = "https://generativelanguage.googleapis.com/v1beta";
        // 「最新的免费模型」用别名，别写死版本号：谷歌会把别名指向当前免费档的最新 Flash。
        public const string DefaultModel = "gemini-flash-latest";

        public static string TranslateUrl(string baseUrl, string model, string apiKey)
        {
            string b = string.IsNullOrEmpty(baseUrl) ? DefaultBase : baseUrl.Trim().TrimEnd('/');
            return b + "/models/" + Uri.EscapeDataString(model) + ":generateContent?key=" + Uri.EscapeDataString(apiKey);
        }

        public static string ModelsUrl(string baseUrl, string apiKey)
        {
            string b = string.IsNullOrEmpty(baseUrl) ? DefaultBase : baseUrl.Trim().TrimEnd('/');
            return b + "/models?key=" + Uri.EscapeDataString(apiKey) + "&pageSize=200";
        }

        public static string Body(string text, string langName) => Body(text, langName, null, 0);

        // 第九轮：thinkingFragment 是 generationConfig 里那一段（thinkingBudget 数字 / thinkingLevel 字符串），
        // 由 CustomAi.ThinkingField 按代际决定形状，空串 = 不写。thinkingExtraTokens 是同档位的思考额度：
        // 谷歌把思考 token 计进输出额度，所以 2048 要跟着抬高，否则「思考吃光额度 → 译文被截断」。
        public static string Body(string text, string langName, string thinkingFragment, int thinkingExtraTokens)
        {
            int extra = thinkingExtraTokens > 0 ? thinkingExtraTokens : 0;
            var sb = new StringBuilder();
            sb.Append("{\"systemInstruction\":{\"parts\":[{\"text\":\"")
              .Append(Json.Escape(AiPrompt.System(langName))).Append("\"}]},");
            sb.Append("\"contents\":[{\"role\":\"user\",\"parts\":[{\"text\":\"")
              .Append(Json.Escape(AiPrompt.User(text))).Append("\"}]}],");
            sb.Append("\"generationConfig\":{\"temperature\":0,\"maxOutputTokens\":")
              .Append((2048 + extra).ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrEmpty(thinkingFragment)) sb.Append(',').Append(thinkingFragment);
            sb.Append("}}");
            return sb.ToString();
        }

        // {"candidates":[{"content":{"parts":[{"text":"译文"}],"role":"model"}}]}
        public static string Parse(string resp)
        {
            string candidates = Json.FindContainer(resp, "candidates");
            string text = candidates != null ? Json.FirstString(candidates, "text") : null;
            return string.IsNullOrEmpty(text) ? null : text.Trim();
        }

        // {"usageMetadata":{"promptTokenCount":10,"candidatesTokenCount":5,"totalTokenCount":15}}
        public static long Usage(string resp) => Json.ReadLong(resp, "totalTokenCount", -1);

        // {"models":[{"name":"models/gemini-flash-latest","displayName":"…"}]}
        public static List<string> Models(string resp)
        {
            var raw = Json.AllStrings(resp, "name");
            var list = new List<string>();
            foreach (string n in raw)
            {
                // 只收能生成文本的模型名，且去掉 "models/" 前缀（调用时再拼回去）。
                string id = n.StartsWith("models/", StringComparison.Ordinal) ? n.Substring(7) : n;
                if (id.Length > 0) list.Add(id);
            }
            return list;
        }
    }

    // ===== Anthropic Messages（自定义 AI 里的 claude）=====
    internal static class AnthropicApi
    {
        public const string DefaultBase = "https://api.anthropic.com/v1";
        public const string Version = "2023-06-01";

        public static string Body(string model, string text, string langName) => Body(model, text, langName, null, 0);

        // 第九轮：thinkingFragment = thinking{...}，可能还带平级的 output_config{...}（4.7 起的 effort 挂在这里，
        // 官方文档明写放进 thinking 内部会 ValidationException）。形状由 CustomAi.ClaudeField 按代际决定。
        // 平台要求 budget_tokens【严格小于】max_tokens，且思考本身也占输出额度，所以 thinkingExtraTokens 用来抬高 max_tokens
        // （译文本身很短，2048 的输出额度从来没用满过）。两者都为 0 时请求体与改动前逐字一致。
        public static string Body(string model, string text, string langName, string thinkingFragment, int thinkingExtraTokens)
        {
            int extra = thinkingExtraTokens > 0 ? thinkingExtraTokens : 0;
            var sb = new StringBuilder();
            sb.Append("{\"model\":\"").Append(Json.Escape(model)).Append("\",\"max_tokens\":")
              .Append((2048 + extra).ToString(CultureInfo.InvariantCulture)).Append(',');
            if (!string.IsNullOrEmpty(thinkingFragment))
                sb.Append(thinkingFragment).Append(',');
            sb.Append("\"system\":\"").Append(Json.Escape(AiPrompt.System(langName))).Append("\",");
            sb.Append("\"messages\":[{\"role\":\"user\",\"content\":\"")
              .Append(Json.Escape(AiPrompt.User(text))).Append("\"}]}");
            return sb.ToString();
        }

        // {"content":[{"type":"text","text":"译文"}],"usage":{"input_tokens":10,"output_tokens":5}}
        public static string Parse(string resp)
        {
            string text = Json.FirstString(resp, "text");
            return string.IsNullOrEmpty(text) ? null : text.Trim();
        }

        public static long Usage(string resp)
        {
            long a = Json.ReadLong(resp, "input_tokens", -1);
            long b = Json.ReadLong(resp, "output_tokens", -1);
            return a >= 0 && b >= 0 ? a + b : -1;
        }
    }

    // ===== Cloudflare Workers AI（@cf/meta/m2m100-1.2b，机器翻译，不是 AI 对话）=====
    internal static class CloudflareAi
    {
        public const string DefaultModel = "@cf/meta/m2m100-1.2b";

        public static string Url(string accountId, string model)
        {
            return "https://api.cloudflare.com/client/v4/accounts/" + Uri.EscapeDataString((accountId ?? "").Trim())
                 + "/ai/run/" + (string.IsNullOrEmpty(model) ? DefaultModel : model.Trim());
        }

        // m2m100 不吃自动检测：source_lang / target_lang 都必须是 M2M-100 码。
        public static string Body(string text, string srcM2M, string dstM2M)
        {
            var sb = new StringBuilder();
            sb.Append("{\"text\":\"").Append(Json.Escape(text)).Append("\",");
            sb.Append("\"source_lang\":\"").Append(Json.Escape(srcM2M)).Append("\",");
            sb.Append("\"target_lang\":\"").Append(Json.Escape(dstM2M)).Append("\"}");
            return sb.ToString();
        }

        // {"result":{"translated_text":"译文"},"success":true,"errors":[],"messages":[]}
        public static string Parse(string resp)
        {
            string result = Json.FindContainer(resp, "result");
            string text = result != null ? Json.FirstString(result, "translated_text") : null;
            if (string.IsNullOrEmpty(text)) text = Json.FirstString(resp, "translated_text");
            return string.IsNullOrEmpty(text) ? null : text.Trim();
        }

        // 失败时平台把原因放在 errors 里：{"errors":[{"code":10000,"message":"…"}]}，捞出来给用户看。
        public static string Error(string resp)
        {
            string errors = Json.FindContainer(resp, "errors");
            return errors == null ? null : Json.FirstString(errors, "message");
        }
    }

    // ===== 免注册机器翻译通道 =====
    internal static class FreeApi
    {
        // ---- 谷歌网页接口 ----
        // 2026-09-08 实测：client=gtx 已被反爬拦截 —— translate.googleapis.com 与 translate.google.com 都回
        // 429 + 一张「Sorry…」的机器人拦截页，补上 Accept / Accept-Language / Referer 等浏览器头【仍然 429】，
        // 所以不是缺头的问题；translate.google.cn 则是 404（该域名不再提供这个端点），已从备选里删掉。
        // 同一个端点把 client 换成 dict-chrome-ex 就正常回 200，且响应还是那套嵌套数组，
        // Json.ConcatGtxSegments 原样能解析 —— 于是 client 也做成候选表轮询，gtx 留着兜底。
        public static readonly string[] GoogleHosts =
        {
            "translate.googleapis.com",
            "translate.google.com",
        };

        public static readonly string[] GoogleClients =
        {
            "dict-chrome-ex",
            "gtx",
        };

        public static string GoogleUrl(string host, string client, string text, string targetLang)
        {
            return "https://" + (string.IsNullOrEmpty(host) ? GoogleHosts[0] : host)
                 + "/translate_a/single?client=" + Uri.EscapeDataString(string.IsNullOrEmpty(client) ? GoogleClients[0] : client)
                 + "&sl=auto&tl=" + Uri.EscapeDataString(targetLang)
                 + "&dt=t&q=" + Uri.EscapeDataString(text ?? "");
        }

        public static string GoogleParse(string resp) => Json.ConcatGtxSegments(resp);

        // ---- DuckDuckGo ----
        // 两步：先 GET /translate 拿 vqd-4 令牌，再带 x-vqd-4 头 POST。令牌是会话级的，拿到后可以复用。
        // 2026-09-08 实测这条通道【在当前网络下已经不通】：GET /translate 302 跳到 /?q=translate&&rpl=1（随后 202，无 vqd），
        // 首页 200/311 KB 里整页找不到 vqd，POST /translate 回 405 nginx —— 正是游戏日志里那条
        // 「请求失败: (405) Not Allowed | 服务端：<html>…nginx…」。换过的其它端点也全灭：
        // /translate/vqd= 410 Gone、api.duckduckgo.com/translate 301、/t/iaui/translate 502。
        // 代码先留着（拦截策略可能因网络/地区而异），但界面说明不许把它写成「稳定可用」。
        public const string DuckDuckGoTranslateUrl = "https://duckduckgo.com/translate";

        // 页面里两种写法都出现过："vqd-4":"4-xxx" 与 vqd=4-xxx，都捞一下。
        public static string DuckDuckGoVqd(string html)
        {
            string v = Json.FirstString(html, "vqd-4");
            if (!string.IsNullOrEmpty(v)) return v;
            if (string.IsNullOrEmpty(html)) return null;
            int i = html.IndexOf("vqd=4-", StringComparison.Ordinal);
            if (i < 0) i = html.IndexOf("vqd%3D4-", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i = html.IndexOf("4-", i, StringComparison.Ordinal);
            if (i < 0) return null;
            int end = i;
            while (end < html.Length && (char.IsLetterOrDigit(html[end]) || html[end] == '-')) end++;
            string tok = html.Substring(i, end - i);
            return tok.Length > 2 ? tok : null;
        }

        public static string DuckDuckGoBody(string text, string fromLang, string toLang)
        {
            return "{\"from_lang\":\"" + Json.Escape(fromLang) + "\",\"to_lang\":\"" + Json.Escape(toLang)
                 + "\",\"text\":\"" + Json.Escape(text) + "\"}";
        }

        // {"translation":"译文","source_language":"en","target_language":"zh-Hans"}
        public static string DuckDuckGoParse(string resp)
        {
            string t = Json.FirstString(resp, "translation");
            return string.IsNullOrEmpty(t) ? null : t.Trim();
        }

        // ---- MyMemory（邮箱选填，填了额度从 5000 字符/天提到 50000）----
        // 它【不支持自动检测源语言】，langpair 两端都必须给。源语言由调用方按游戏当前语言传入，
        // 写死 en 会让日文/俄文原文全部翻错。
        public static string MyMemoryUrl(string text, string sourceLang, string targetLang, string email)
        {
            string src = string.IsNullOrEmpty(sourceLang) ? "en-US" : sourceLang;
            // 两端映射成同一个码时，MyMemory 不报错，而是把一句英文 'PLEASE SELECT TWO DISTINCT LANGUAGES'
            // 当【译文】回给你 —— 实测它就这么上过屏。真实翻译路径撞不到（游戏语言==目标语言时模组根本不翻），
            // 会撞到的是「测试」按钮：探针文本固定是英文，源语言却取游戏当前显示语言。
            // 所以两端相同就把源钉成 en-US，这对英文探针恰好是对的。
            if (string.Equals(src, targetLang, StringComparison.OrdinalIgnoreCase)) src = "en-US";
            string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text ?? "")
                       + "&langpair=" + Uri.EscapeDataString(src) + "%7C" + Uri.EscapeDataString(targetLang);
            string de = (email ?? "").Trim();
            if (de.Length > 0) url += "&de=" + Uri.EscapeDataString(de);
            return url;
        }

        // {"responseData":{"translatedText":"译文","match":1},"responseStatus":200}
        public static string MyMemoryParse(string resp)
        {
            string t = Json.FirstString(resp, "translatedText");
            if (string.IsNullOrEmpty(t)) return null;
            // 超额/异常时它回一句英文说明而不是译文，必须挡掉，否则那句话会被当成译文上屏。
            if (t.IndexOf("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (t.IndexOf("QUERY LENGTH", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (t.IndexOf("INVALID", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            // 源与目标都是英语时 URL 里那道守卫救不回来（en-US 换成 en-US 还是相同），只能在这里挡掉。
            if (t.IndexOf("DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return t.Trim();
        }

        public static string MyMemoryError(string resp)
        {
            string d = Json.FirstString(resp, "responseDetails");
            return string.IsNullOrEmpty(d) ? null : d;
        }

        // ---- Yandex ----
        // 用户选的方案：免 key 网页通道先试，填了 IAM token 就走官方 API。
        // 实测（2026-09-08）：免 key 网页通道【已经死了】—— translate.yandex.net 直接 404
        // （游戏日志里的「抓首页失败: (404) Not Found」就是它），换到 translate.yandex.com 虽然回 200，
        // 但整页 41 KB 里一个 sid 都找不到（改成了纯 JS 壳，sid 由前端运行时生成）。
        // 更早的结论仍然成立：/api/v1/tr.json/uuid 已下线，/translate 直接 POST 回 {"code":405,"message":"Session is invalid"}。
        // 所以这里保留抓取代码只为「万一哪天又露出来」，实际可用的只有下面填 IAM token 的官方 API 分支；
        // 界面侧已把 Yandex 归到「需注册」档（EngineKit.IsNoSignup / MissingField）。抓不到就明确失败，不要瞎猜。
        public const string YandexHome = "https://translate.yandex.com/";

        // 首页里 sid 的两种写法："sid":"1234.5678.9012" 与 sid: '…'
        public static string YandexSid(string html)
        {
            string v = Json.FirstString(html, "sid");
            if (!string.IsNullOrEmpty(v)) return v;
            if (string.IsNullOrEmpty(html)) return null;
            foreach (string marker in new[] { "sid:'", "sid: '", "sid=\"" })
            {
                int i = html.IndexOf(marker, StringComparison.Ordinal);
                if (i < 0) continue;
                int start = i + marker.Length;
                int end = start;
                while (end < html.Length && (char.IsLetterOrDigit(html[end]) || html[end] == '.' || html[end] == '-')) end++;
                if (end > start) return html.Substring(start, end - start);
            }
            return null;
        }

        public static string YandexUcid(string html)
        {
            string v = Json.FirstString(html, "ucid");
            if (!string.IsNullOrEmpty(v)) return v;
            if (string.IsNullOrEmpty(html)) return null;
            int i = html.IndexOf("ucid=", StringComparison.Ordinal);
            if (i < 0) return null;
            int start = i + 5;
            int end = start;
            while (end < html.Length && (char.IsLetterOrDigit(html[end]) || html[end] == '-')) end++;
            return end > start ? html.Substring(start, end - start) : null;
        }

        public static string YandexWebUrl(string sid, int counter)
        {
            string id = Uri.EscapeDataString((sid ?? "") + "-" + counter.ToString(CultureInfo.InvariantCulture) + "-0");
            return "https://translate.yandex.net/api/v1/tr.json/translate?id=" + id + "&srv=android&format=text";
        }

        public static string YandexWebForm(string text, string targetLang, string ucid)
        {
            var sb = new StringBuilder();
            sb.Append("text=").Append(Uri.EscapeDataString(text ?? ""));
            sb.Append("&lang=").Append(Uri.EscapeDataString(targetLang));
            sb.Append("&options=4&format=text");
            if (!string.IsNullOrEmpty(ucid)) sb.Append("&ucid=").Append(Uri.EscapeDataString(ucid));
            return sb.ToString();
        }

        // 官方 v1.5.1（IAM token / API key）
        public static string YandexOfficialUrl(string apiKey, string text, string targetLang)
        {
            return "https://translate.api.yandex.net/translate/v1.5.1.json/translate?key=" + Uri.EscapeDataString(apiKey)
                 + "&lang=" + Uri.EscapeDataString(targetLang)
                 + "&format=plain&text=" + Uri.EscapeDataString(text ?? "");
        }

        // {"code":200,"lang":"en-zh","text":["译文"]}
        public static string YandexParse(string resp)
        {
            long code = Json.ReadLong(resp, "code", 200);
            if (code != 200) return null;
            string t = Json.FirstStringInArray(resp, "text");
            return string.IsNullOrEmpty(t) ? null : t.Trim();
        }

        public static string YandexError(string resp)
        {
            string m = Json.FirstString(resp, "message");
            if (!string.IsNullOrEmpty(m)) return m;
            long code = Json.ReadLong(resp, "code", 0);
            return code == 0 ? null : "code=" + code.ToString(CultureInfo.InvariantCulture);
        }
    }

    // ===== 自定义 AI 模型：8 家主流大模型的接口格式 + 1 个完全自填的「自定义」 =====
    internal static class CustomAi
    {
        // 反馈8：末尾第 9 项 custom = 8 家之外的任意接口（自架网关、别的厂商、校内代理）。
        // 它没有官方地址也没有默认模型名，两样都得玩家自己填；请求体形状改由玩家在「接口格式」里选。
        public static readonly string[] Vendors = { "gpt", "claude", "kimi", "qwen", "deepseek", "gemini", "grok", "glm", "custom" };

        public const string Custom = "custom";
        public const string ProtocolOpenAi = "openai";
        public const string ProtocolAnthropic = "anthropic";
        public const string ProtocolGemini = "gemini";

        public static bool IsVendor(string v)
        {
            string n = (v ?? "").Trim().ToLowerInvariant();
            foreach (string x in Vendors) if (x == n) return true;
            return false;
        }

        public static string Normalize(string v) => (v ?? "").Trim().ToLowerInvariant();

        // 各家的默认 base url。玩家可以改（走代理/中转站时必需），留空就用默认。
        // custom 返回【空串】：没有官方地址可兜底，留空等于没告诉模组往哪发，由 EngineKit.MissingField 当必填项拦下。
        public static string DefaultBaseUrl(string vendor)
        {
            switch (Normalize(vendor))
            {
                case "gpt": return "https://api.openai.com/v1";
                case "claude": return AnthropicApi.DefaultBase;
                case "kimi": return "https://api.moonshot.cn/v1";
                case "qwen": return "https://dashscope.aliyuncs.com/compatible-mode/v1";
                case "deepseek": return "https://api.deepseek.com/v1";
                case "gemini": return GeminiApi.DefaultBase;
                case "grok": return "https://api.x.ai/v1";
                case "glm": return "https://open.bigmodel.cn/api/paas/v4";
                case Custom: return string.Empty;
                default: return "https://api.openai.com/v1";
            }
        }

        // 各家一个「大概率能用」的默认模型名，玩家可改可拉列表。custom 同样没有默认值：模型名只有玩家自己知道。
        public static string DefaultModel(string vendor)
        {
            switch (Normalize(vendor))
            {
                case "gpt": return "gpt-4o-mini";
                case "claude": return "claude-3-5-haiku-latest";
                case "kimi": return "moonshot-v1-8k";
                case "qwen": return "qwen-turbo";
                case "deepseek": return "deepseek-chat";
                case "gemini": return GeminiApi.DefaultModel;
                case "grok": return "grok-3-mini";
                case "glm": return "glm-4-flash";
                case Custom: return string.Empty;
                default: return "gpt-4o-mini";
            }
        }

        public static string Base(string vendor, string userBase)
        {
            string b = (userBase ?? "").Trim();
            return b.Length > 0 ? b : DefaultBaseUrl(vendor);
        }

        // 接口格式。8 家的格式是固定的（gemini 一家、claude 一家、其余六家都是 OpenAI 兼容），
        // 只有 custom 才看玩家在选项里选的那一项。userProtocol 传 TranslatorSetting.AiProtocol 的枚举名，
        // 8 家一律忽略它 —— 玩家选了 deepseek 又把格式改成 anthropic，只会得到一个必然失败的请求。
        public static string Protocol(string vendor, string userProtocol)
        {
            string v = Normalize(vendor);
            if (v != Custom) return v == "gemini" ? ProtocolGemini : (v == "claude" ? ProtocolAnthropic : ProtocolOpenAi);
            string p = Normalize(userProtocol);
            return p == ProtocolAnthropic || p == ProtocolGemini ? p : ProtocolOpenAi;
        }

        // ===== 第九轮：思考强度按【各家真实口径】发送 =====
        // 2026-09-11 逐家核实（各家官方文档 / 官方 SDK / 官方 OpenAPI，逐条结论见交接文档 §10 第 58 条）：
        // 旧写法「Off = 一个字段都不发」在 13 格里只有 2 格真等于不思考。DeepSeek 默认 enabled+high、
        // GLM 默认 enabled+max、Kimi 默认 enabled、Gemini 默认动态思考、Claude 5 系默认 adaptive（且照样计费）、
        // OpenAI 默认 medium —— 不发送就是按那一家的默认档在思考、在花钱。
        // 但反过来，这些字段发给不认识它的模型会直接 400（阿里云有专门的 NotSupportEnableThinking，
        // Groq 原文「超出该模型支持集合的值会被 400 拒掉」），所以每一家还要按【玩家选的模型名】判断该不该发；
        // 剩下认不准的那部分由 Mod.cs 的「带思考字段拿到 400/422 就剥掉该字段重发一次」兜底。

        // 档位的唯一口径。存盘存的就是这里的词，所以老档的 Off/Low/Medium/High 照读不误。
        public enum ThinkLevel { Off, Minimal, Low, Medium, High, Xhigh, Max }

        public static ThinkLevel ParseLevel(string level)
        {
            switch (Normalize(level))
            {
                case "minimal": return ThinkLevel.Minimal;
                case "low": return ThinkLevel.Low;
                case "medium": return ThinkLevel.Medium;
                case "high": return ThinkLevel.High;
                case "xhigh": return ThinkLevel.Xhigh;
                case "max": return ThinkLevel.Max;
                default: return ThinkLevel.Off;      // 脏值也走这里：认不出来就当关，宁可少花钱
            }
        }

        // 这一格界面上该出现哪几档 = 这一家真实支持的档位集合，~ 分隔。认不出的组合返回空串 = 这一格没有这一行。
        // ⚠ Setting.cs 里那几个「行枚举」必须逐项对得上这张表：多一档玩家能选却发不出去，少一档存过的值读回来会被挤掉。
        public static string ThinkingLevels(string engineName, string vendor)
        {
            const string SEVEN = "Off~Minimal~Low~Medium~High~Xhigh~Max";
            if (Normalize(engineName) == "custom")
            {
                switch (Normalize(vendor))
                {
                    case "gpt": return SEVEN;                                  // 官方枚举：none/minimal/low/medium/high/xhigh/max
                    case "claude": return "Off~Low~Medium~High~Xhigh~Max";      // effort 这一族没有 minimal
                    case "kimi": return "Off~Low~High~Max";                     // K3 只有 low/high/max，没有 medium
                    case "qwen": return string.Empty;                           // 非流式调用不许开思考 → 这一行直接不给，见 QwenField
                    case "deepseek": return "Off~Low~High~Max";                 // medium 会被平台折成 high，干脆不列
                    case "gemini": return "Off~Minimal~Low~Medium~High";
                    case "grok": return "Off~Low~Medium~High~Xhigh";            // 无 minimal、无 max
                    case "glm": return SEVEN;
                    case Custom: return "Off~Low~Medium~High";                  // 玩家自填端点，只给最通用那四档
                }
                return string.Empty;
            }
            switch (Normalize(engineName))
            {
                case "gemini": return "Off~Minimal~Low~Medium~High";
                case "groq": return SEVEN;
                case "openrouter": return SEVEN;
                case "siliconflow": return "Off~High~Max";                      // 平台的 effort 只收 high/max
            }
            return string.Empty;
        }

        // 把越界的档位挤回这一家真实支持的那一档。levelSet 里的词一律按低→高排，所以平手时先撞到的就是更低那档。
        private static ThinkLevel ClampLevel(ThinkLevel want, string levelSet)
        {
            string[] words = (levelSet ?? "").Split('~');
            int best = -1;
            int bestDist = int.MaxValue;
            for (int i = 0; i < words.Length; i++)
            {
                if (words[i].Length == 0) continue;
                ThinkLevel cand = ParseLevel(words[i]);
                int dist = Math.Abs((int)cand - (int)want);
                if (dist < bestDist) { bestDist = dist; best = (int)cand; }
            }
            return best < 0 ? ThinkLevel.Off : (ThinkLevel)best;
        }

        // 这一格这一档最终拼进 body 的那一段 JSON（不带尾随逗号）。返回空串 = 一个字段都不发。
        public static string ThinkingField(string engineName, string vendor, string protocol, string model, string level)
        {
            int ignored;
            return ThinkingField(engineName, vendor, protocol, model, level, out ignored);
        }

        // out outputTokens：这一档的思考要吃掉多少【输出额度】。谷歌与 Anthropic 都把思考 token 计在输出 token 里，
        // 所以这两家的 body 有 maxOutputTokens / max_tokens 字段要跟着抬高；OpenAI 兼容那一族没有这个字段，恒返回 0。
        public static string ThinkingField(string engineName, string vendor, string protocol, string model, string level, out int outputTokens)
        {
            string en = Normalize(engineName);
            string v = Normalize(vendor);
            string m = Normalize(model);
            ThinkLevel lv = ClampLevel(ParseLevel(level), ThinkingLevels(en, v));
            string shape = en == "custom" ? Protocol(v, protocol)
                        : en == "gemini" ? ProtocolGemini
                        : ProtocolOpenAi;
            outputTokens = (shape == ProtocolGemini || shape == ProtocolAnthropic) ? Budget(lv) : 0;
            if (en == "custom") return CustomField(v, Protocol(v, protocol), m, lv);
            switch (en)
            {
                case "gemini": return GeminiField(m, lv);
                case "groq": return GroqField(m, lv);
                case "openrouter": return RouterField(m, lv);
                case "siliconflow": return SiliconField(m, lv);
            }
            return string.Empty;      // 机翻引擎与 Cloudflare：ThinkingLevels 给的就是空串
        }

        private static string CustomField(string vendor, string protocol, string model, ThinkLevel lv)
        {
            switch (vendor)
            {
                case "gpt": return OpenaiField(model, lv);
                case "claude": return ClaudeField(model, lv);
                case "kimi": return KimiField(model, lv);
                case "qwen": return QwenField(model, lv);
                case "deepseek": return DeepSeekField(model, lv);
                case "gemini": return GeminiField(model, lv);
                case "grok": return GrokField(model, lv);
                case "glm": return GlmField(model, lv);
            }
            // custom 那一格：对面是谁只有玩家知道，只能按所选格式的最通用写法发，认不出模型代际就一个字段都不发。
            if (protocol == ProtocolAnthropic) return ClaudeField(model, lv);
            if (protocol == ProtocolGemini) return GeminiField(model, lv);
            return OpenaiField(model, lv);
        }

        // ---- 各家字段形状 ----
        private static string Str(string name, string val) => "\"" + name + "\":\"" + val + "\"";
        private static string Num(string name, int val) => "\"" + name + "\":" + val.ToString(CultureInfo.InvariantCulture);
        private static string ThinkType(string type) => "\"thinking\":{\"type\":\"" + type + "\"}";

        // OpenAI：只有推理模型认 reasoning_effort，gpt-4o 那一类多写一个字段就是 400。
        // 官方 spec 的枚举同时列了 none 与 max，且默认值是 medium —— 所以「关」必须显式发 none。
        private static string OpenaiField(string model, ThinkLevel lv)
        {
            if (!IsOpenaiReasoning(model)) return string.Empty;
            switch (lv)
            {
                case ThinkLevel.Minimal: return Str("reasoning_effort", "minimal");
                case ThinkLevel.Low: return Str("reasoning_effort", "low");
                case ThinkLevel.Medium: return Str("reasoning_effort", "medium");
                case ThinkLevel.High: return Str("reasoning_effort", "high");
                case ThinkLevel.Xhigh: return Str("reasoning_effort", "xhigh");
                case ThinkLevel.Max: return Str("reasoning_effort", "max");
                default: return Str("reasoning_effort", "none");
            }
        }

        // Anthropic 分两代：4.7 起只认 thinking.type=adaptive + 平级 output_config.effort，
        // 再发 enabled+budget_tokens 官方文档明写 400；4.5/4.6 那一代反过来，只有 enabled+budget_tokens。
        private static string ClaudeField(string model, ThinkLevel lv)
        {
            if (!IsClaudeThinking(model)) return string.Empty;
            if (IsClaudeAdaptive(model))
            {
                if (lv == ThinkLevel.Off) return ThinkType("disabled");
                string effort;
                switch (lv)
                {
                    case ThinkLevel.Max: effort = "max"; break;
                    case ThinkLevel.Xhigh: effort = "xhigh"; break;
                    case ThinkLevel.Medium: effort = "medium"; break;
                    case ThinkLevel.Low: case ThinkLevel.Minimal: effort = "low"; break;
                    default: effort = "high"; break;
                }
                return ThinkType("adaptive") + ",\"output_config\":" + Str2("effort", effort);
            }
            if (lv == ThinkLevel.Off) return string.Empty;    // 这一代不发就是不思考，不发才是对的
            return "\"thinking\":{\"type\":\"enabled\"," + Num("budget_tokens", Budget(lv)) + "}";
        }

        private static string Str2(string key, string val) => "{\"" + key + "\":\"" + val + "\"}";

        // 思考要占输出额度，Anthropic 还要求 budget_tokens 严格小于 max_tokens，所以开思考时把 max_tokens 抬高同样的量。
        private static int Budget(ThinkLevel lv)
        {
            switch (lv)
            {
                case ThinkLevel.Minimal: return 1024;
                case ThinkLevel.Low: return 1024;
                case ThinkLevel.Medium: return 2048;
                case ThinkLevel.High: return 4096;
                case ThinkLevel.Xhigh: return 8192;
                case ThinkLevel.Max: return 8192;
                default: return 0;
            }
        }

        // Kimi：不发＝默认开启。K2.6 只有开/关；K3 只有 low/high/max 且关不掉（官方：默认 max）。
        private static string KimiField(string model, ThinkLevel lv)
        {
            if (!StartsWith(model, "kimi")) return string.Empty;   // moonshot-v1 那一代不认这个参数
            if (ContainsAny(model, "k3", "k2.7", "k2-7"))
            {
                switch (lv)
                {
                    case ThinkLevel.Max: return Str("reasoning_effort", "max");
                    case ThinkLevel.High: return Str("reasoning_effort", "high");
                    default: return Str("reasoning_effort", "low");   // 含 Off：这一族关不掉，最低就是 low
                }
            }
            return lv == ThinkLevel.Off ? ThinkType("disabled") : ThinkType("enabled");
        }

        // 千问：阿里云错误码页原文「parameter.enable_thinking must be set to false for non-streaming calls」。
        // 本模组走的是 stream=false，所以给它选任何非关档位都只会得到一个 400 —— 这一格界面上不给档位，
        // 这里恒定发 enable_thinking:false（对默认就开思考的开源版/3.5+ 才是真关掉，也顺手修掉了原来不发就 400 的隐患）。
        private static string QwenField(string model, ThinkLevel lv)
        {
            if (!IsQwenThinkModel(model)) return string.Empty;
            return "\"enable_thinking\":false";
        }

        // DeepSeek：官方原文「Thinking mode is enabled by default, with the default effort being high」。
        // V4 系认 thinking + reasoning_effort(low/high/max)；reasoner 恒定思考关不掉；chat 本来就是非思考。
        private static string DeepSeekField(string model, ThinkLevel lv)
        {
            if (!ContainsAny(model, "v4", "reasoner")) return string.Empty;
            if (lv == ThinkLevel.Off) return Contains(model, "v4") ? ThinkType("disabled") : string.Empty;
            if (!Contains(model, "v4")) return string.Empty;   // reasoner 没有可调档位，多发一个 effort 只是风险
            string effort = lv == ThinkLevel.Max ? "max" : lv == ThinkLevel.High ? "high" : "low";
            return ThinkType("enabled") + "," + Str("reasoning_effort", effort);
        }

        // Grok：顶层 reasoning_effort，枚举是 none/low/medium/high/xhigh（无 minimal、无 max）。默认档官方未记录，所以关要显式发 none。
        private static string GrokField(string model, ThinkLevel lv)
        {
            if (!IsGrokReasoning(model)) return string.Empty;
            switch (lv)
            {
                case ThinkLevel.Low: return Str("reasoning_effort", "low");
                case ThinkLevel.Medium: return Str("reasoning_effort", "medium");
                case ThinkLevel.Xhigh: return Str("reasoning_effort", "xhigh");
                case ThinkLevel.High: case ThinkLevel.Max: return Str("reasoning_effort", "high");
                default: return Str("reasoning_effort", "none");
            }
        }

        // GLM：默认就是 thinking enabled + effort max。effort 只有 5.2 及以后认；5.3 系强制思考、传 disabled 会报错。
        private static string GlmField(string model, ThinkLevel lv)
        {
            if (!IsGlmThinking(model)) return string.Empty;
            if (!IsGlmEffort(model)) return lv == ThinkLevel.Off ? ThinkType("disabled") : ThinkType("enabled");
            bool five3 = ContainsAny(model, "5.3", "5-3");
            if (lv == ThinkLevel.Off) return five3 ? Str("reasoning_effort", "low") : ThinkType("disabled");
            string effort;
            switch (lv)
            {
                case ThinkLevel.Max: effort = "max"; break;
                case ThinkLevel.Xhigh: effort = five3 ? "max" : "xhigh"; break;
                case ThinkLevel.Medium: effort = five3 ? "high" : "medium"; break;
                case ThinkLevel.Minimal: effort = "low"; break;
                default: effort = five3 ? "high" : "low"; break;
            }
            return ThinkType("enabled") + "," + Str("reasoning_effort", effort);
        }

        // Gemini：2.5 认 thinkingBudget（数字），3.x 改认 thinkingLevel（官方专门有一页讲迁移）。
        // 2.5 Pro 与 3.x 全系「思考关不掉」，那一档只能落到该代最低值上。
        // ⚠ 认不出代际的 gemini-* 名字一律走数字形状：引擎那格发出去的是别名 gemini-flash-latest（不带版本号），
        // 走 thinkingLevel 那一支会给 2.5 系换来一个 INVALID_ARGUMENT；反过来 3.x 遇到 thinkingBudget 只是不生效，不会 400。
        private static string GeminiField(string model, ThinkLevel lv)
        {
            if (!StartsWith(model, "gemini")) return string.Empty;   // 玩家自填的怪名字：不是谷歌的形状，一个字段都不发
            if (StartsWith(model, "gemini-3"))
            {
                bool noMinimal = ContainsAny(model, "3.8", "3-8");    // 官方：3.8 flash 不支持 minimal
                string floor = noMinimal ? "low" : "minimal";
                switch (lv)
                {
                    case ThinkLevel.Low: return Str("thinkingLevel", "low");
                    case ThinkLevel.Medium: return Str("thinkingLevel", "medium");
                    case ThinkLevel.High: case ThinkLevel.Xhigh: case ThinkLevel.Max: return Str("thinkingLevel", "high");
                    default: return Str("thinkingLevel", floor);      // 含 Off/Minimal
                }
            }
            return GeminiBudgetField(model, lv);
        }

        private static string GeminiBudgetField(string model, ThinkLevel lv)
        {
            int min = ContainsAny(model, "pro", "2.5pro", "2.5-pro") ? 128 : 0;   // 2.5 Pro 的预算下限不是 0
            switch (lv)
            {
                case ThinkLevel.Minimal: return Num("thinkingBudget", System.Math.Max(256, min));
                case ThinkLevel.Low: return Num("thinkingBudget", 1024);
                case ThinkLevel.Medium: return Num("thinkingBudget", 2048);
                case ThinkLevel.Off: return Num("thinkingBudget", min);
                default: return Num("thinkingBudget", 4096);      // High/Xhigh/Max
            }
        }

        // Groq：只有顶层 reasoning_effort，且「超出该模型支持集合的值直接 400」。gpt-oss 那一族没有关闭档 → Off 落 low。
        private static string GroqField(string model, ThinkLevel lv)
        {
            if (!IsGroqReasoning(model)) return string.Empty;
            if (lv == ThinkLevel.Off) return IsGroqNoOff(model) ? Str("reasoning_effort", "low") : Str("reasoning_effort", "none");
            switch (lv)
            {
                case ThinkLevel.Minimal: return Str("reasoning_effort", "minimal");
                case ThinkLevel.Medium: return Str("reasoning_effort", "medium");
                case ThinkLevel.Xhigh: return Str("reasoning_effort", "xhigh");
                case ThinkLevel.Max: return Str("reasoning_effort", "max");
                default: return Str("reasoning_effort", "low");       // Low，以及认不出的一律往低走
            }
        }

        // OpenRouter：平台自己的标准字段是 reasoning.effort，不认识的值「provider 会收到请求但忽略未知参数」，
        // 所以这一家可以照发不误；只有 :auto 自动路由那一格官方要求省略。
        private static string RouterField(string model, ThinkLevel lv)
        {
            if (ContainsAny(model, "auto", ":openrouter")) return string.Empty;
            if (lv == ThinkLevel.High || lv == ThinkLevel.Xhigh || lv == ThinkLevel.Max) return "\"reasoning\":" + Str2("effort", "max");
            switch (lv)
            {
                case ThinkLevel.Minimal: return "\"reasoning\":" + Str2("effort", "minimal");
                case ThinkLevel.Low: return "\"reasoning\":" + Str2("effort", "low");
                case ThinkLevel.Medium: return "\"reasoning\":" + Str2("effort", "medium");
                default: return "\"reasoning\":" + Str2("effort", "none");
            }
        }

        // 硅基流动：关＝enable_thinking:false；effort 只有 high/max，且只对官方点名的那三类模型开放。
        private static string SiliconField(string model, ThinkLevel lv)
        {
            if (lv != ThinkLevel.Off)
            {
                if (!ContainsAny(model, "deepseek-v4", "deepseek_v4", "glm-5.2", "glm-52")) return string.Empty;
                return Str("reasoning_effort", lv == ThinkLevel.Max ? "max" : "high");
            }
            return "\"enable_thinking\":false";
        }

        // ---- 模型名判据：认不出代际就退成「不发」，让 400 兜底去管漏网的 ----
        private static bool StartsWith(string s, string p) => (s ?? "").StartsWith(p, StringComparison.Ordinal);
        private static bool Contains(string s, string p) => (s ?? "").IndexOf(p ?? "", StringComparison.Ordinal) >= 0;
        private static bool ContainsAny(string s, params string[] any)
        {
            for (int i = 0; i < any.Length; i++) if (Contains(s, any[i])) return true;
            return false;
        }

        private static bool IsOpenaiReasoning(string m)
        {
            return StartsWith(m, "o1") || StartsWith(m, "o3") || StartsWith(m, "o4") || StartsWith(m, "o5")
                || StartsWith(m, "gpt-5") || StartsWith(m, "gpt-6") || StartsWith(m, "gpt-7")
                || StartsWith(m, "codex") || StartsWith(m, "chatgpt");
        }

        private static bool IsClaudeThinking(string m)
        {
            if (ContainsAny(m, "fable", "mythos")) return true;
            int major, minor;
            if (!ClaudeVersion(m, out major, out minor)) return false;
            return major >= 4 || (major == 3 && minor >= 7);
        }

        // 4.7 及以后、以及 5/6 全系走 adaptive；4.5/4.6 仍走 enabled+budget_tokens。
        private static bool IsClaudeAdaptive(string m)
        {
            if (ContainsAny(m, "fable", "mythos")) return true;
            int major, minor;
            if (!ClaudeVersion(m, out major, out minor)) return false;
            return major >= 5 || (major == 4 && minor >= 7);
        }

        // claude 的名字有两代写法：版本在前的 claude-3-5-sonnet-20240620，家族名在前的 claude-opus-4-7 /
        // claude-sonnet-4-20250514。只看 claude- 后面的前缀会把后一整代漏掉 —— 第九轮的门禁就是在这里抓出来的：
        // opus-4-7 认不出代际 → 一个字段都不发 → 玩家选了「关」而平台照样按默认 adaptive 思考、照样计费。
        // 所以取「claude 之后头两段数字」当主/次版本号；第三段是发布日（20250514），超过 99 一律不当档位号。
        private static bool ClaudeVersion(string m, out int major, out int minor)
        {
            major = -1; minor = -1;
            string s = m ?? "";
            int at = s.IndexOf("claude", StringComparison.Ordinal);
            if (at < 0) return false;
            int run = 0;
            for (int i = at + 6; i < s.Length && run < 2; i++)
            {
                if (s[i] < '0' || s[i] > '9') continue;
                int v = 0;
                while (i < s.Length && s[i] >= '0' && s[i] <= '9') { v = v * 10 + (s[i] - '0'); i++; }
                i--;
                if (run == 0) major = v; else minor = v;
                run++;
            }
            if (minor > 99) minor = -1;      // 日期尾巴：sonnet-4-20250514 是 4.0，不是 4.20250514
            return major >= 0;
        }

        private static bool IsQwenThinkModel(string m)
        {
            return ContainsAny(m, "qwen3", "qwen-3", "qwq", "qwen-plus", "qwen-max", "qwen-flash");
        }

        private static bool IsGrokReasoning(string m)
        {
            return StartsWith(m, "grok-3-mini") || StartsWith(m, "grok-4") || StartsWith(m, "grok-3-mini-beta")
                || ContainsAny(m, "reasoning", "grok-4", "grok-3.5", "grok-3-5");
        }

        private static bool IsGlmThinking(string m)
        {
            return StartsWith(m, "glm-4-5") || StartsWith(m, "glm-4.5") || StartsWith(m, "glm-4-6")
                || StartsWith(m, "glm-4.6") || StartsWith(m, "glm-4-7") || StartsWith(m, "glm-4.7")
                || StartsWith(m, "glm-5") || StartsWith(m, "glm-6") || StartsWith(m, "autoglm");
        }

        private static bool IsGlmEffort(string m)
        {
            return ContainsAny(m, "glm-5.2", "glm-5-2", "5.3", "5-3", "glm-6");
        }

        private static bool IsGroqReasoning(string m)
        {
            return ContainsAny(m, "gpt-oss", "qwen3", "qwen/qwen3", "compound", "deepseek-r1", "kimi-k2");
        }

        private static bool IsGroqNoOff(string m)
        {
            return Contains(m, "gpt-oss");   // 官方 SDK：gpt-oss 只支持 low/medium/high
        }

        // ---- 按【接口格式】分发的一组入口（反馈8 之后 Mod.cs 只走这一组）----
        // 鉴权方式：anthropic 走 x-api-key + 版本头，gemini 的 key 在查询参数里（这个头它直接忽略），其余一律 Bearer。
        public static string AuthHeaderNameFor(string protocol)
        {
            return protocol == ProtocolAnthropic ? "x-api-key" : "Authorization";
        }

        public static string AuthHeaderValueFor(string protocol, string apiKey)
        {
            string k = (apiKey ?? "").Trim();
            return protocol == ProtocolAnthropic ? k : "Bearer " + k;
        }

        // Gemini 的模型名在 URL 里而不在 body 里，anthropic 的端点叫 messages，其余一律 chat/completions。
        public static string TranslateUrl(string protocol, string vendor, string userBase, string model, string apiKey)
        {
            string b = Base(vendor, userBase);
            if (protocol == ProtocolGemini) return GeminiApi.TranslateUrl(b, model, apiKey);
            return OpenAiChat.Url(b, protocol == ProtocolAnthropic ? "messages" : "chat/completions");
        }

        public static string BodyFor(string protocol, string model, string text, string langName)
            => BodyFor(protocol, model, text, langName, null, 0);

        // ⚠ 最后一个参数是【拼好的 JSON 片段】，不是档位词。调用方一律先 CustomAi.ThinkingField(...) 再传进来 ——
        // 档位词直接传到这里会被当成片段塞进 body，得到一个语法上合法、语义上是垃圾的请求体。
        public static string BodyFor(string protocol, string model, string text, string langName, string thinkingFragment, int thinkingExtraTokens)
        {
            if (protocol == ProtocolGemini) return GeminiApi.Body(text, langName, thinkingFragment, thinkingExtraTokens);
            if (protocol == ProtocolAnthropic) return AnthropicApi.Body(model, text, langName, thinkingFragment, thinkingExtraTokens);
            return OpenAiChat.Body(model, text, langName, thinkingFragment);
        }

        public static string ParseFor(string protocol, string resp)
        {
            if (protocol == ProtocolGemini) return GeminiApi.Parse(resp);
            if (protocol == ProtocolAnthropic) return AnthropicApi.Parse(resp);
            return OpenAiChat.Parse(resp);
        }

        public static long UsageFor(string protocol, string resp)
        {
            if (protocol == ProtocolGemini) return GeminiApi.Usage(resp);
            if (protocol == ProtocolAnthropic) return AnthropicApi.Usage(resp);
            return OpenAiChat.Usage(resp);
        }

        // ---- 按【厂商】分发的旧入口：一律先解析出接口格式再转交上面那组。----
        // 8 家内置厂商的格式是固定的，所以这些封装的行为与改动前逐字一致（离线断言钉着这条）。
        public static string AuthHeaderName(string vendor) => AuthHeaderNameFor(Protocol(vendor, null));

        public static string AuthHeaderValue(string vendor, string apiKey) => AuthHeaderValueFor(Protocol(vendor, null), apiKey);

        public static string GeminiTranslateUrl(string userBase, string model, string apiKey)
            => GeminiApi.TranslateUrl(Base("gemini", userBase), model, apiKey);

        public static string OpenAiTranslateUrl(string vendor, string userBase)
            => OpenAiChat.Url(Base(vendor, userBase), Protocol(vendor, null) == ProtocolAnthropic ? "messages" : "chat/completions");

        public static string Body(string vendor, string model, string text, string langName)
            => BodyFor(Protocol(vendor, null), model, text, langName);

        public static string Parse(string vendor, string resp) => ParseFor(Protocol(vendor, null), resp);

        public static long Usage(string vendor, string resp) => UsageFor(Protocol(vendor, null), resp);

        // 「获取模型列表」按钮：各家的列表端点与响应形状都不一样。
        public static string ModelsUrl(string vendor, string userBase, string apiKey) => ModelsUrl(vendor, null, userBase, apiKey);

        public static string ModelsUrl(string vendor, string userProtocol, string userBase, string apiKey)
        {
            string b = Base(vendor, userBase);
            if (Protocol(vendor, userProtocol) == ProtocolGemini) return GeminiApi.ModelsUrl(b, apiKey);
            return OpenAiChat.Url(b, "models");
        }

        public static List<string> ParseModels(string vendor, string resp)
        {
            if (string.IsNullOrEmpty(resp)) return new List<string>();
            if (Protocol(vendor, null) == ProtocolGemini) return GeminiApi.Models(resp);
            // Anthropic: {"data":[{"id":"claude-3-5-haiku-latest",…}]}；OpenAI 系同形状。
            return Json.AllStrings(resp, "id");
        }
    }

    // ===== token 计数 =====
    internal static class Tokens
    {
        // 平台回了 usage 就用真值；没回（Cloudflare m2m100 就不回）才用本地估算。
        // 估算规则：CJK / 假名 / 谚文按 1 字符 1 token，其余按 4 字符 1 token 向上取整。
        // 这是各家 BPE 分词器的粗略共同点，误差常在 ±30% 内 —— 所以界面文案必须写「与实际消耗量可能会有出入」。
        public static long Estimate(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int cjk = 0, other = 0;
            foreach (char c in text)
            {
                if (IsWide(c)) cjk++;
                else other++;
            }
            return cjk + (other + 3) / 4;
        }

        private static bool IsWide(char c)
        {
            return (c >= 0x3040 && c <= 0x30FF)   // 假名
                || (c >= 0x4E00 && c <= 0x9FFF)   // 汉字
                || (c >= 0xAC00 && c <= 0xD7AF)   // 谚文
                || (c >= 0x3400 && c <= 0x4DBF)   // 汉字扩展A
                || (c >= 0xFF00 && c <= 0xFFEF);  // 全角
        }

        // 一次调用的总消耗 = 请求（提示词 + 原文）+ 响应（译文）。平台给了真值就返回真值。
        public static long Charge(long reportedUsage, string promptText, string userText, string resultText)
        {
            if (reportedUsage >= 0) return reportedUsage;
            return Estimate(promptText) + Estimate(userText) + Estimate(resultText) + 8; // 8 = JSON 结构开销
        }
    }
}
