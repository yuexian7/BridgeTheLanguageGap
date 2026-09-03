using System;
using System.Collections.Generic;
using System.Threading;
using Colossal;
using Colossal.Localization;

namespace Cs2AutoTranslator
{
    // 本模组自身界面文字的多语言表。官方 12 种语言全部硬编码：不依赖网络/key，离线即用，
    // 选项界面会跟随游戏当前语言显示对应文字。
    // 若游戏切到「非官方 12 种」的语言，由 SelfL10n（见文件末尾）用当前引擎机翻本模组文字兜底（需求 12）。
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

        internal static bool IsOfficial(string locale)
        {
            if (string.IsNullOrEmpty(locale)) return false;
            for (int i = 0; i < Locales.Length; i++)
                if (string.Equals(Locales[i], locale, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

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

        private static void Add(Dictionary<string, Dictionary<string, string>> t, string key, params string[] vals)
        {
            var d = new Dictionary<string, string>();
            for (int i = 0; i < Locales.Length && i < vals.Length; i++) d[Locales[i]] = vals[i];
            t[key] = d;
        }

        private static Dictionary<string, Dictionary<string, string>> Build()
        {
            var t = new Dictionary<string, Dictionary<string, string>>();

            // 标题（需求 6）：改名为「拯救语言不通」。
            Add(t, "name",
                "Sprachbarriere überbrücken", "Bridge the Language Gap", "Salva la barrera del idioma", "Combler la barrière de la langue", "Colma la barriera linguistica", "言葉の壁を救う",
                "언어 장벽 해소", "Pokonaj barierę językową", "Supere a barreira do idioma", "Преодолей языковой барьер", "拯救语言不通", "拯救語言不通");

            Add(t, "tab.main",
                "Haupt", "Main", "Principal", "Général", "Generale", "メイン",
                "메인", "Główne", "Principal", "Основное", "主设置", "主設定");

            // ===== 总开关（需求 8）=====
            Add(t, "group.main",
                "Hauptschalter", "Master switch", "Interruptor principal", "Commutateur principal", "Interruttore principale", "メインスイッチ",
                "마스터 스위치", "Przełącznik główny", "Interruptor principal", "Главный переключатель", "总开关", "總開關");

            Add(t, "label.enabled",
                "Übersetzung aktivieren", "Enable translation", "Activar traducción", "Activer la traduction", "Attiva traduzione", "翻訳を有効化",
                "번역 활성화", "Włącz tłumaczenie", "Ativar tradução", "Включить перевод", "启用翻译", "啟用翻譯");

            Add(t, "desc.enabled",
                "Standardmäßig AUS. Beim Einschalten beginnt die Übersetzung sofort, ohne Speichern; beim Ausschalten wird die Originalsprache sofort wiederhergestellt.",
                "OFF by default. Turning it on starts translating immediately — no save needed; turning it off restores the source language at once.",
                "Desactivado de forma predeterminada. Al activarlo, la traducción comienza de inmediato sin guardar; al desactivarlo, se restaura el idioma original al instante.",
                "Désactivé par défaut. L’activer lance la traduction immédiatement, sans enregistrement ; le désactiver restaure aussitôt la langue d’origine.",
                "Disattivato per impostazione predefinita. Attivandolo la traduzione inizia subito senza salvare; disattivandolo la lingua originale viene ripristinata immediatamente.",
                "既定はオフ。オンにすると保存なしでただちに翻訳を開始。オフにすると即座に元の言語へ復帰します。",
                "기본값은 끔. 켜면 저장 없이 즉시 번역이 시작됩니다. 끄면 곧바로 원본 언어로 복원됩니다.",
                "Domyślnie WYŁĄCZONE. Włączenie od razu uruchamia tłumaczenie bez zapisywania; wyłączenie natychmiast przywraca język źródłowy.",
                "Desligado por padrão. Ao ativá-lo, a tradução começa imediatamente sem salvar; ao desativá-lo, o idioma original é restaurado na hora.",
                "По умолчанию ВЫКЛ. При включении перевод начинается сразу, без сохранения; при выключении исходный язык немедленно восстанавливается.",
                "默认关闭。打开后立即开始翻译，无需再点保存；关闭后界面立即恢复源语言。",
                "預設關閉。開啟後立即開始翻譯，無需再按保存；關閉後介面立即恢復來源語言。");

            // ===== 目标语言与保存分组（需求 7：置于翻译范围下方）=====
            Add(t, "group.target",
                "Zielsprache & Speichern", "Target language & save", "Idioma de destino y guardado", "Langue cible et enregistrement", "Lingua di destinazione e salvataggio", "翻訳先言語と保存",
                "번역 대상 언어 및 저장", "Język docelowy i zapis", "Idioma de destino e salvamento", "Целевой язык и сохранение", "翻译语言与保存", "翻譯語言與保存");

            Add(t, "label.engine",
                "Übersetzungs-Engine", "Translation engine", "Motor de traducción", "Moteur de traduction", "Motore di traduzione", "翻訳エンジン",
                "번역 엔진", "Silnik tłumaczenia", "Motor de tradução", "Движок перевода", "翻译引擎", "翻譯引擎");

            Add(t, "desc.engine",
                "Microsoft (offizielle Bing-API): genau, großes Gratis-Kontingent (F0 = 2 Mio. Zeichen/Monat), braucht Schlüssel; Standard-Engine dieses Mods. DeepL: am genauesten, braucht Schlüssel und oft VPN. Baidu: in Festlandchina am stabilsten, braucht APP-ID + Schlüssel. Google: Die Keyless-Web-API ist gesperrt (oft 429) und nur eine Notlösung – nicht empfohlen.",
                "Microsoft (official Bing API): accurate, large free quota (F0 = 2M chars/month), needs a key; this mod’s default engine. DeepL: most accurate, needs a key and often a VPN. Baidu: most stable in mainland China, needs APP ID + key. Google: the keyless web API is blocked (often 429) and is only a last-resort fallback — not recommended.",
                "Microsoft (API oficial de Bing): preciso, amplia cuota gratis (F0 = 2M caracteres/mes), requiere clave; motor predeterminado de este mod. DeepL: el más preciso, requiere clave y a menudo VPN. Baidu: el más estable en China continental, requiere APP ID + clave. Google: la API web sin clave está bloqueada (suele dar 429) y solo es un último recurso; no recomendado.",
                "Microsoft (API officielle Bing) : précis, large quota gratuit (F0 = 2M caractères/mois), nécessite une clé ; moteur par défaut de ce mod. DeepL : le plus précis, nécessite une clé et souvent un VPN. Baidu : le plus stable en Chine continentale, nécessite APP ID + clé. Google : l’API web sans clé est bloquée (souvent 429) et ne sert que de dernier recours — non recommandé.",
                "Microsoft (API ufficiale Bing): preciso, ampia quota gratuita (F0 = 2M caratteri/mese), serve una chiave; motore predefinito di questa mod. DeepL: il più preciso, serve una chiave e spesso una VPN. Baidu: il più stabile in Cina continentale, serve APP ID + chiave. Google: l’API web senza chiave è bloccata (spesso 429) ed è solo un’ultima risorsa — non consigliato.",
                "Microsoft（Bing公式API）：正確・無料枠大（F0=200万文字/月）・キーが必要。本Modの既定エンジン。DeepL：最も正確だがキーとVPNが必要なことが多い。Baidu：中国本土で最も安定、APP ID+キーが必要。Google：キー不要のWeb APIは封鎖済み（多くは429）で最終手段のみ、非推奨。",
                "Microsoft(공식 Bing API): 정확·무료 한도 큼(F0=200만 글자/월)·키 필요. 이 모드의 기본 엔진. DeepL: 가장 정확하나 키와 VPN이 필요한 경우 많음. Baidu: 중국 본토에서 가장 안정적, APP ID+키 필요. Google: 키 없는 웹 API는 차단되어(대개 429) 최후 수단일 뿐, 권장하지 않음.",
                "Microsoft (oficjalne API Bing): dokładny, duży darmowy limit (F0 = 2 mln znaków/mies.), wymaga klucza; domyślny silnik tego moda. DeepL: najdokładniejszy, wymaga klucza i często VPN. Baidu: najstabilniejszy w Chinach kontynentalnych, wymaga APP ID + klucza. Google: bezkluczowe API sieciowe jest zablokowane (często 429) i służy tylko jako ostateczność — niezalecane.",
                "Microsoft (API oficial do Bing): preciso, ampla cota grátis (F0 = 2M caracteres/mês), precisa de chave; motor padrão deste mod. DeepL: o mais preciso, precisa de chave e muitas vezes de VPN. Baidu: o mais estável na China continental, precisa de APP ID + chave. Google: a API web sem chave está bloqueada (geralmente 429) e serve apenas como último recurso — não recomendado.",
                "Microsoft (официальный API Bing): точный, большая бесплатная квота (F0 = 2 млн симв./мес.), нужен ключ; движок этого мода по умолчанию. DeepL: самый точный, нужен ключ и часто VPN. Baidu: самый стабильный в материковом Китае, нужен APP ID + ключ. Google: бесплатный веб-API без ключа заблокирован (часто 429) и годится лишь как крайний вариант — не рекомендуется.",
                "微软（必应官方 API）：准确、免费额度大（F0 层每月 200 万字符），需 key，是本模组默认引擎。DeepL：最准，需 key 且常需 VPN。百度：中国大陆最稳，需 APP ID + 密钥。谷歌：免 key 网页接口已被封（常返回 429），仅作末位备选，不推荐。",
                "微軟（Bing 官方 API）：準確、免費額度大（F0 層每月 200 萬字元），需 key，是本模組預設引擎。DeepL：最準，需 key 且常需 VPN。百度：中國大陸最穩，需 APP ID + 金鑰。谷歌：免 key 網頁介面已被封（常回傳 429），僅作末位備選，不推薦。");

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
                "Mod-Optionen & Beschreibungen", "Mod options & descriptions", "Opciones y descripciones de mods", "Options et descriptions de mods", "Opzioni e descrizioni mod", "Modオプションと説明",
                "모드 옵션 및 설명", "Opcje i opisy modów", "Opções e descrições de mods", "Настройки и описания модов", "模组选项及说明", "模組選項及說明");

            Add(t, "desc.scope.modOptions",
                "Übersetzt die Einstellungs-Labels und -Beschreibungen von Mods im Optionsmenü.",
                "Translates the setting labels and descriptions of mods in the options menu.",
                "Traduce las etiquetas y descripciones de ajustes de los mods en el menú de opciones.",
                "Traduit les libellés et descriptions des réglages des mods dans le menu des options.",
                "Traduce le etichette e le descrizioni delle impostazioni dei mod nel menu opzioni.",
                "オプションメニュー内のMod設定のラベルと説明を翻訳。",
                "옵션 메뉴에서 모드 설정의 레이블과 설명을 번역.",
                "Tłumaczy etykiety i opisy ustawień modów w menu opcji.",
                "Traduz os rótulos e descrições das configurações de mods no menu de opções.",
                "Переводит названия и описания настроек модов в меню параметров.",
                "翻译模组在选项菜单里的设置项标签与说明。",
                "翻譯模組在選項選單裡的設定項標籤與說明。");

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

            Add(t, "label.scope.modName",
                "Mod-Namen (experimentell)", "Mod names (experimental)", "Nombres de mods (experimental)", "Noms de mods (expérimental)", "Nomi dei mod (sperimentale)", "Mod名（実験的）",
                "모드 이름(실험적)", "Nazwy modów (eksperymentalne)", "Nomes de mods (experimental)", "Названия модов (эксперим.)", "模组名称（实验性）", "模組名稱（實驗性）");

            Add(t, "desc.scope.modName",
                "Experimentelle Funktion; da viele Mod-Titel seltsam wirken, wird das Aktivieren nicht empfohlen.",
                "Experimental feature; since many mod titles look odd, enabling it is not recommended.",
                "Función experimental; como muchos títulos de mods son extraños, no se recomienda activarla.",
                "Fonction expérimentale ; de nombreux titres de mods étant bizarres, son activation n’est pas recommandée.",
                "Funzione sperimentale; poiché molti titoli delle mod sono bizzarri, non è consigliabile attivarla.",
                "実験的機能。Mod名は奇妙なものが多いので、オンにするのは推奨しません。",
                "실험적 기능입니다. 모드 제목이 이상한 경우가 많아 켜는 것을 권장하지 않습니다.",
                "Funkcja eksperymentalna; ponieważ wiele nazw modów wygląda dziwnie, nie zaleca się jej włączania.",
                "Recurso experimental; como muitos títulos de mods são estranhos, não é recomendável ativá-lo.",
                "Экспериментальная функция; поскольку названия модов часто выглядят странно, включать не рекомендуется.",
                "实验性功能。由于模组标题很多都奇奇怪怪，所以不推荐打开。",
                "實驗性功能。由於模組標題很多都奇奇怪怪，所以不推薦打開。");

            Add(t, "label.scope.worldLabels",
                "Welt-Ebenen (experimentell)", "World layers (experimental)", "Capas del mundo (experimental)", "Couches monde (expérimental)", "Livelli mondo (sperimentale)", "ワールド表示（実験的）",
                "월드 레이어(실험적)", "Warstwy świata (eksperymentalne)", "Camadas do mundo (experimental)", "Мировые слои (эксперим.)", "游戏画面图层（实验性）", "遊戲畫面圖層（實驗性）");

            Add(t, "desc.scope.worldLabels",
                "Straßennamen, Bezirksnamen und anderer auf das Spielgelände gelegter Text. Experimentell, möglicherweise unwirksam oder instabil. Hinweis: Namen werden erst übersetzt bzw. auf die Originalsprache zurückgesetzt, wenn Sie die Maus über das Objekt bzw. Gebiet bewegen (beschränkt durch den Label-Cache des Spiels).",
                "Road names, district names and other text overlaid on the game terrain. Experimental; may be ineffective or unstable. Note: names only start translating — or revert to the original language — after you move the mouse over that object or area (limited by the game’s label cache).",
                "Nombres de carreteras, distritos y otro texto sobre el terreno. Experimental; puede no funcionar o ser inestable. Nota: los nombres solo se traducen —o vuelven al idioma original— al pasar el ratón sobre ese objeto o zona (limitado por la caché de etiquetas del juego).",
                "Noms de routes, quartiers et autre texte posé sur le terrain. Expérimental ; peut être inopérant ou instable. Remarque : les noms ne commencent à se traduire — ou à revenir à la langue d’origine — qu’après avoir survolé l’objet ou la zone (limité par le cache d’étiquettes du jeu).",
                "Nomi di strade, distretti e altro testo sul terreno di gioco. Sperimentale; potrebbe non funzionare o essere instabile. Nota: i nomi iniziano a essere tradotti (o tornano alla lingua originale) solo dopo aver spostato il mouse sull’oggetto o sull’area (limite della cache delle etichette di gioco).",
                "道路名・区域名など地形上に表示される文字。実験的機能で、効かない・不安定な場合があります。注意：名前はその対象や区域にマウスを乗せて初めて翻訳開始・原文復帰します（ゲームのラベルキャッシュの制約）。",
                "도로·구역 이름 등 지형 위에 표시되는 글. 실험적 기능이라 작동하지 않거나 불안정할 수 있습니다. 주의: 이름은 해당 대상이나 구역에 마우스를 올린 뒤에야 번역이 시작되거나 원문으로 돌아갑니다(게임 라벨 캐시 제한).",
                "Nazwy dróg, dzielnic i inny tekst na terenie gry. Eksperymentalne; może nie działać lub być niestabilne. Uwaga: nazwy zaczynają się tłumaczyć — lub wracać do języka oryginalnego — dopiero po najechaniu myszą na dany obiekt lub obszar (ograniczone pamięcią podręczną etykiet gry).",
                "Nomes de estradas, distritos e outro texto sobre o terreno. Experimental; pode não funcionar ou ser instável. Observação: os nomes só começam a ser traduzidos — ou voltam ao idioma original — depois que você passa o mouse sobre o objeto ou a área (limitado pelo cache de rótulos do jogo).",
                "Названия дорог, районов и другой текст на местности. Экспериментально; может не работать или быть нестабильным. Примечание: названия начинают переводиться — или возвращаться к исходному языку — только после наведения курсора на объект или область (ограничено кэшем меток игры).",
                "路名、区名等叠在游戏地形上的文字。实验性功能，可能不生效或不稳定。注意：需把鼠标移到该资产/区域上，其名称才会开始翻译或恢复原文（受游戏标签缓存限制）。",
                "路名、區名等疊在遊戲地形上的文字。實驗性功能，可能不生效或不穩定。注意：需將滑鼠移到該資產／區域上，其名稱才會開始翻譯或恢復原文（受遊戲標籤快取限制）。");

            Add(t, "label.scope.protect",
                "Schutz eigener Inhalte", "Your-content protection", "Protección de tu contenido", "Protection de votre contenu", "Protezione dei tuoi contenuti", "自作コンテンツの保護",
                "사용자 콘텐츠 보호", "Ochrona Twoich treści", "Proteção do seu conteúdo", "Защита вашего контента", "玩家自定义内容保护", "玩家自訂內容保護");

            Add(t, "desc.scope.protect",
                "Wirkt erst nach dem Speichern der Einstellungen; selbst manuell geänderte Namen werden nicht beeinflusst.",
                "Takes effect only after you save the settings; it does not affect names you’ve changed manually.",
                "Solo surte efecto tras guardar los ajustes; no afecta a los nombres que hayas cambiado manualmente.",
                "Ne prend effet qu’après l’enregistrement des réglages ; n’affecte pas les noms modifiés manuellement.",
                "Ha effetto solo dopo aver salvato le impostazioni; non influisce sui nomi modificati manualmente.",
                "設定を保存してから有効になります。手動で変更した名前には影響しません。",
                "설정을 저장한 뒤에 적용되며, 직접 수정한 이름에는 영향을 주지 않습니다.",
                "Działa dopiero po zapisaniu ustawień; nie wpływa na ręcznie zmienione nazwy.",
                "Só passa a valer após salvar as configurações; não afeta nomes alterados manualmente.",
                "Вступает в силу только после сохранения настроек; не влияет на названия, изменённые вручную.",
                "必须保存设置后才生效，不影响手动修改的名称。",
                "必須保存設定後才生效，不影響手動修改的名稱。");

            // ===== 密钥与测试（需求 6/7）=====
            Add(t, "group.keys",
                "API-Schlüssel & Test", "API Keys & Test", "Claves API y prueba", "Clés API et test", "Chiavi API e test", "APIキーとテスト",
                "API 키 및 테스트", "Klucze API i test", "Chaves API e teste", "API-ключи и тест", "API 密钥与测试", "API 金鑰與測試");

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

            Add(t, "desc.save",
                "Speichert Schalter/Umfang/Zielsprache/Schlüssel auf der Festplatte (übersteht Neustart) und aktualisiert die Oberfläche sofort mit den neuen Einstellungen. Tipp: zum Eingeben eines Schlüssels zur englischen Eingabe wechseln oder einfügen; zuerst außerhalb des Felds klicken, dann speichern.",
                "Persists switches/scope/target language/keys to disk (survives restart) and immediately refreshes the interface with the new settings. Tip: switch to English input or paste when entering a key; click outside the field first, then save.",
                "Guarda interruptores/alcance/idioma de destino/claves en disco (sobrevive al reinicio) y actualiza de inmediato la interfaz con los nuevos ajustes. Consejo: usa entrada en inglés o pega al escribir una clave; haz clic fuera del campo y luego guarda.",
                "Enregistre commutateurs/périmètre/langue cible/clés sur le disque (survit au redémarrage) et actualise immédiatement l’interface avec les nouveaux réglages. Astuce : passez en saisie anglaise ou collez la clé ; cliquez hors du champ puis enregistrez.",
                "Salva interruttori/ambito/lingua di destinazione/chiavi su disco (sopravvivono al riavvio) e aggiorna subito l’interfaccia con le nuove impostazioni. Suggerimento: usa l’input inglese o incolla la chiave; clicca fuori dal campo poi salva.",
                "スイッチ/範囲/翻訳先言語/キーをディスクに保存し（再起動後も保持）、新しい設定で界面を即座に更新。ヒント：キー入力は英語入力に切替えるか貼る。まず欄の外をクリックしてから保存。",
                "스위치/범위/대상 언어/키를 디스크에 저장(재시작 후 유지)하고 새 설정으로 인터페이스를 즉시 새로고침. 팁: 키 입력 시 영어 입력으로 전환하거나 붙여넣기. 먼저 입력창 밖을 클릭한 뒤 저장.",
                "Zapisuje przełączniki/zakres/język docelowy/klucze na dysku (przetrwają restart) i natychmiast odświeża interfejs nowymi ustawieniami. Wskazówka: przełącz na angielskie wprowadzanie lub wklej klucz; kliknij poza polem, potem zapisz.",
                "Salva interruptores/escopo/idioma de destino/chaves em disco (sobrevivem à reinicialização) e atualiza imediatamente a interface com as novas configurações. Dica: use entrada em inglês ou cole a chave; clique fora do campo e depois salve.",
                "Сохраняет переключатели/область/целевой язык/ключи на диск (переживут перезапуск) и сразу обновляет интерфейс с новыми настройками. Совет: переключитесь на английский ввод или вставьте ключ; сначала кликните вне поля, затем сохраните.",
                "把开关/范围/目标语言/key 写入磁盘（重启不丢），并立即按新设置重刷界面。提示：填 key 时切英文输入法或直接粘贴；填完先点输入框外面，再点本按钮。",
                "把開關/範圍/目標語言/key 寫入磁碟（重啟不遺失），並立即依新設定重新整理介面。提示：填 key 時切英文輸入法或直接貼上；填完先點輸入框外面，再按本按鈕。");

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

            // ===== 免责（需求 6）=====
            Add(t, "group.misc",
                "Sonstiges & Haftung", "Misc & Disclaimer", "Varios y aviso", "Divers et avertissement", "Varie e disclaimer", "その他と免責",
                "기타 및 면책", "Różne i zastrzeżenia", "Diversos e aviso", "Прочее и отказ", "其它与免责", "其它與免責");

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
                "Entfernt restlos alle Übersetzungsspuren (Speicher + Festplatten-Cache); die Oberfläche kehrt vollständig zum Originaltext zurück. Ändert KEINE Einstellungen.",
                "Completely removes all translation traces (memory + disk cache); the interface fully reverts to the original text. Does NOT change any settings.",
                "Elimina por completo todos los rastros de traducción (memoria + caché en disco); la interfaz vuelve totalmente al texto original. NO cambia ningún ajuste.",
                "Supprime complètement toutes les traces de traduction (mémoire + cache disque) ; l’interface revient entièrement au texte d’origine. NE modifie AUCUN réglage.",
                "Rimuove completamente tutte le tracce di traduzione (memoria + cache su disco); l’interfaccia torna del tutto al testo originale. NON modifica alcuna impostazione.",
                "翻訳の痕跡（メモリ+ディスクキャッシュ）を完全に消去し、界面はすべて原文に戻ります。設定は一切変更しません。",
                "번역 흔적(메모리+디스크 캐시)을 완전히 제거하여 인터페이스가 모두 원문으로 돌아갑니다. 설정은 전혀 바꾸지 않습니다.",
                "Całkowicie usuwa wszystkie ślady tłumaczenia (pamięć + bufor na dysku); interfejs w pełni wraca do oryginalnego tekstu. NIE zmienia żadnych ustawień.",
                "Remove completamente todos os vestígios de tradução (memória + cache em disco); a interface volta totalmente ao texto original. NÃO altera nenhuma configuração.",
                "Полностью удаляет все следы перевода (память + кэш на диске); интерфейс целиком возвращается к исходному тексту. НЕ меняет никакие настройки.",
                "完全清除所有翻译痕迹（内存+磁盘缓存），界面全部回到原文；不会改动任何设置。",
                "完全清除所有翻譯痕跡（記憶體+磁碟快取），介面全部回到原文；不會改動任何設定。");

            Add(t, "label.disclaimer",
                "Haftungsausschluss", "Disclaimer", "Aviso legal", "Avertissement", "Disclaimer", "免責事項",
                "면책 조항", "Zastrzeżenia", "Aviso legal", "Отказ от ответственности", "免责说明", "免責說明");

            Add(t, "text.disclaimer",
                "Dieses Mod ist eine BETA. Übersetzungen werden maschinell von Drittanbieter-APIs erstellt und sind NICHT garantiert korrekt – Fehler, Auslassungen oder unpassende Begriffe sind möglich; alle Rechte am Originaltext verbleiben beim Urheber. Deine API-Schlüssel werden nur auf diesem Computer im Klartext gespeichert und ausschließlich an den gewählten Übersetzungsdienst gesendet. Alle Engines haben Gratis-Kontingente (nicht unbegrenzt); ein lokaler Cache übersetzt jeden Text nur einmal, sodass der Verbrauch sehr niedrig bleibt. Google und DeepL benötigen in Festlandchina meist ein VPN; Microsoft und Baidu verbinden direkt. Bei Problemen klick in den Mod-Einstellungen auf „Protokollordner öffnen“ und hänge die dort gefundene Protokolldatei an deinen Beitrag im Diskussionsbereich des Mods an.",
                "This mod is in BETA. Translations are machine-generated by third-party APIs and are NOT guaranteed to be correct — errors, omissions or poor terminology may occur; all copyright in the original text remains with its owner. Your API keys are stored in plaintext on this computer only and are sent solely to the translation service you choose. All engines have free quotas (not unlimited); a local cache translates each string only once, so usage stays very low. Google and DeepL usually need a VPN in mainland China; Microsoft and Baidu connect directly. If you hit problems, click “Open log folder” in this mod’s settings and attach the log file you find there to your post in the mod’s discussion area.",
                "Este mod está en BETA. Las traducciones las generan máquinas mediante APIs de terceros y NO se garantiza que sean correctas: puede haber errores, omisiones o terminología inadecuada; los derechos del texto original siguen siendo de su autor. Tus claves API se guardan en texto claro solo en esta computadora y se envían únicamente al servicio de traducción que elijas. Todos los motores tienen cuotas gratis (no ilimitadas); una caché local traduce cada texto una sola vez, así que el uso es muy bajo. Google y DeepL suelen requerir VPN en China continental; Microsoft y Baidu conectan directamente. Si tienes problemas, pulsa «Abrir carpeta del registro» en los ajustes de este mod y adjunta al área de discusión el archivo de registro que encontrarás allí.",
                "Ce mod est en BÊTA. Les traductions sont générées automatiquement par des API tierces et ne sont PAS garanties correctes — des erreurs, omissions ou une terminologie inadaptée peuvent survenir ; les droits du texte original restent ceux de leur auteur. Vos clés API sont stockées en clair uniquement sur cet ordinateur et envoyées seulement au service de traduction choisi. Tous les moteurs ont des quotas gratuits (non illimités) ; un cache local ne traduit chaque texte qu’une fois, l’usage reste donc très faible. Google et DeepL nécessitent généralement un VPN en Chine continentale ; Microsoft et Baidu se connectent directement. En cas de problème, cliquez sur « Ouvrir le dossier du journal » dans les réglages de ce mod et joignez à l’espace de discussion le fichier journal qui s’y trouve.",
                "Questa mod è in BETA. Le traduzioni sono generate automaticamente da API di terze parti e NON è garantito che siano corrette — possono verificarsi errori, omissioni o terminologia inappropriata; i diritti del testo originale restano dell’autore. Le tue chiavi API sono salvate in chiaro solo su questo computer e inviate unicamente al servizio di traduzione scelto. Tutti i motori hanno quote gratuite (non illimitate); una cache locale traduce ogni testo una sola volta, quindi l’uso resta molto basso. Google e DeepL di solito richiedono una VPN in Cina continentale; Microsoft e Baidu si collegano direttamente. In caso di problemi, clicca su “Apri cartella del registro” nelle impostazioni di questa mod e allega all’area di discussione il file di registro che vi trovi.",
                "本Modはベータ版です。訳文はサードパーティAPIによる機械翻訳で、正確さは保証されません。誤訳・訳漏れ・用語の不適切さが出る場合があり、原文の著作権は著作者に残ります。APIキーはこのパソコン内に平文で保存されるだけで、選択した翻訳サービスにのみ送信されます。各エンジンには無料枠（無制限ではない）があり、ローカルキャッシュにより各文は一度だけ翻訳されるため使用量は非常に低く抑えられます。GoogleとDeepLは中国本土では通常VPNが必要、MicrosoftとBaiduは直接接続できます。問題が起きたら、本Modの設定にある「ログフォルダを開く」を押し、そこにあるログファイルをModの議論エリアに投稿してください。",
                "이 모드는 베타입니다. 번역문은 제3자 API의 기계 번역으로 정확성이 보장되지 않습니다. 오역·누락·부적절한 용어가 있을 수 있으며, 원문 저작권은 원작자에게 있습니다. API 키는 이 컴퓨터에 평문으로만 저장되고 선택한 번역 서비스로만 전송됩니다. 모든 엔진은 무료 한도(무제한 아님)가 있고, 로컬 캐시가 각 문장을 한 번만 번역하므로 사용량이 매우 낮게 유지됩니다. Google과 DeepL은 중국 본토에서 보통 VPN이 필요하고, Microsoft와 Baidu는 직접 연결됩니다. 문제가 있으면 이 모드 설정의 「로그 폴더 열기」를 눌러 거기 있는 로그 파일을 모드 토론 구역에 올려 주세요.",
                "Ten mod jest w wersji BETA. Tłumaczenia są generowane maszynowo przez zewnętrzne API i NIE ma gwarancji ich poprawności — mogą wystąpić błędy, pominięcia lub niewłaściwa terminologia; prawa autorskie do oryginału pozostają przy ich właścicielu. Twoje klucze API są przechowywane w postaci jawnej tylko na tym komputerze i wysyłane wyłącznie do wybranej usługi tłumaczeniowej. Wszystkie silniki mają darmowe limity (nie nieograniczone); lokalna pamięć podręczna tłumaczy każdy tekst tylko raz, więc zużycie pozostaje bardzo niskie. Google i DeepL w Chinach kontynentalnych zwykle wymagają VPN; Microsoft i Baidu łączą się bezpośrednio. W razie problemów kliknij „Otwórz folder dziennika” w ustawieniach tego moda i załącz znaleziony tam plik dziennika do wpisu w dyskusji moda.",
                "Este mod está em BETA. As traduções são geradas por máquina por APIs de terceiros e NÃO há garantia de que estejam corretas — podem ocorrer erros, omissões ou terminologia inadequada; os direitos do texto original permanecem com o autor. Suas chaves de API são armazenadas em texto simples apenas neste computador e enviadas somente ao serviço de tradução que você escolher. Todos os motores têm cotas grátis (não ilimitadas); um cache local traduz cada texto uma única vez, então o uso permanece muito baixo. Google e DeepL geralmente precisam de VPN na China continental; Microsoft e Baidu conectam diretamente. Se tiver problemas, clique em “Abrir pasta do log” nas configurações deste mod e anexe à área de discussão o arquivo de log que encontrar lá.",
                "Этот мод в стадии БЕТА. Переводы создаются машиной через сторонние API и НЕ гарантированно точны — возможны ошибки, пропуски или неудачная терминология; авторские права на оригинал сохраняются за их владельцем. Ваши API-ключи хранятся в открытом виде только на этом компьютере и отправляются лишь выбранному сервису перевода. У всех движков есть бесплатные квоты (не безлимитные); локальный кэш переводит каждую строку лишь один раз, поэтому расход остаётся очень низким. Google и DeepL в материковом Китае обычно требуют VPN; Microsoft и Baidu подключаются напрямую. При проблемах нажмите «Открыть папку журнала» в настройках этого мода и приложите к сообщению в разделе обсуждения найденный там файл журнала.",
                "本模组为 beta 测试版。译文由第三方机器翻译接口生成，不保证准确，可能出现错译、漏译或术语不当，原文版权均归原作者。你的 key 仅以明文保存在本机，且只发送给你选择的翻译服务。各引擎均为免费额度（非无限），本地缓存让同一段文字只翻译一次，因此实际用量极低。谷歌和 DeepL 在中国大陆通常需 VPN；微软和百度可直连。如遇问题，请点本模组设置里的「打开日志文件夹」，把里面的日志文件发到本模组的讨论区反馈。",
                "本模組為 beta 測試版。譯文由第三方機器翻譯介面產生，不保證準確，可能出現錯譯、漏譯或術語不當，原文版權均歸原作者。你的 key 僅以明文保存在本機，且只傳送給你選擇的翻譯服務。各引擎均為免費額度（非無限），本地快取讓同一段文字只翻譯一次，因此實際用量極低。谷歌和 DeepL 在中國大陸通常需 VPN；微軟和百度可直連。如遇問題，請點本模組設定裡的「打開日誌資料夾」，把裡面的日誌檔案發到本模組的討論區回饋。");

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

        // 取本模组界面文字：官方语言→硬编码；非官方语言→机翻结果，未就绪时回退英文。
        public static string T(string locale, string key)
        {
            if (L10n.IsOfficial(locale)) return L10n.T(locale, key);
            lock (Machine)
            {
                if (string.Equals(_machineLocale, locale, StringComparison.OrdinalIgnoreCase) && Machine.TryGetValue(key, out var m) && !string.IsNullOrEmpty(m))
                    return m;
            }
            return L10n.T("en-US", key); // 机翻未就绪：先用英文兜底
        }

        // 当前游戏语言不是官方 12 种时，后台把本模组全部界面文字机翻到该语言（只触发一次，之后走缓存）。
        public static void EnsureSelfTranslation(string locale)
        {
            if (L10n.IsOfficial(locale) || string.IsNullOrEmpty(locale)) return;
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
                ModLog.Info("[自翻] 当前引擎未配置 key，本模组界面文字暂用英文兜底（非官方语言：" + locale + "）。");
                return;
            }

            int count = 0, fromCache = 0;
            foreach (string key in L10n.AllKeys)
            {
                string en = L10n.T("en-US", key);
                if (string.IsNullOrEmpty(en) || en == key) continue;

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
            // 用 SelfL10n.T：官方 12 种走硬编码，非官方语言走机翻兜底（需求 12）。
            string L(string key) => SelfL10n.T(m_Locale, key);
            var d = new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), L("name") },
                { m_Setting.GetOptionTabLocaleID(TranslatorSetting.kSection), L("tab.main") },

                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupMain), L("group.main") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupScope), L("group.scope") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupTarget), L("group.target") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupKeys), L("group.keys") },
                { m_Setting.GetOptionGroupLocaleID(TranslatorSetting.kGroupMisc), L("group.misc") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.Enabled)), L("label.enabled") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.Enabled)), L("desc.enabled") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TranslationEngine)), L("label.engine") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.TranslationEngine)), L("desc.engine") },

                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Google), L("enum.google") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Microsoft), L("enum.microsoft") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.DeepL), L("enum.deepl") },
                { m_Setting.GetEnumValueLocaleID(TranslatorSetting.Engine.Baidu), L("enum.baidu") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TargetLocale)), L("label.target") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.TargetLocale)), L("desc.target") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeModOptions)), L("label.scope.modOptions") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.ScopeModOptions)), L("desc.scope.modOptions") },
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
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.ScopeProtect)), L("label.scope.protect") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TestTranslation)), L("label.test") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.TestTranslation)), L("desc.test") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.TestResult)), L("label.testResult") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.SaveConfig)), L("label.save") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.SaveConfig)), L("desc.save") },

                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.MicrosoftKey)), L("label.msKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.MicrosoftKey)), L("desc.msKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.MicrosoftRegion)), L("label.microsoftRegion") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.MicrosoftRegion)), L("desc.microsoftRegion") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.DeepLKey)), L("label.deeplKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.DeepLKey)), L("desc.deeplKey") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.BaiduAppId)), L("label.baiduAppId") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.BaiduAppId)), L("desc.baiduAppId") },
                { m_Setting.GetOptionLabelLocaleID(nameof(TranslatorSetting.BaiduKey)), L("label.baiduKey") },
                { m_Setting.GetOptionDescLocaleID(nameof(TranslatorSetting.BaiduKey)), L("desc.baiduKey") },

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
            return d;
        }

        public void Unload() { }
    }
}
