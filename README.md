# BridgeTheLanguageGap / 拯救语言不通

Machine-translates Cities: Skylines II UI text (game + mods) into your language, on demand, live.

把都市天际线2 的界面文字（游戏本体 + 模组）按需机翻成你选的目标语言，实时生效、无需重启。

## Paradox Mods

**[拯救语言不通 Auto Translator (ModId 157802)](https://mods.paradoxplaza.com/mods/157802/any)**

Subscribe in-game via `Mods → Paradox Mods`, or install through Skyve.

## What it does

Cities: Skylines II leaves a lot of UI text untranslated across languages, and mod UI is almost always English-only. This mod hooks into the game's `UILocalizationManager.Translate` and `NameSystem.GetRenderedLabelName` methods with Harmony patches, then machine-translates the strings you actually see into your chosen target language. Translation happens lazily in the background — you won't notice it working until the text flips to your language.

## Features

- **On-demand translation**: only translates strings you actually see and have enabled in scope; no bulk pre-translation.
- **Local disk cache**: each string is translated once, then cached to `ModsData\Cs2AutoTranslator\translation.cache`. Works offline afterwards. API usage stays very low.
- **Configurable scope**: toggle game-only, mod-only, or both; toggle asset names/descriptions/mod names separately.
- **Multi-engine support**: Microsoft Azure (default), DeepL, Baidu, Google (keyed + keyless fallback).
- **12-language mod UI**: the mod's own settings page ships with translations for 12 languages; falls back to machine translation for others.
- **One-click test / save / clear cache / open log folder**: all inside the mod's settings page.
- **Dedicated log file**: separate from the game's log, easy to attach when reporting issues.

## Privacy

API keys are stored in plaintext **on your own machine only** (`ModsSettings\Cs2AutoTranslator.json`) and are sent solely to the translation service you select. The mod contacts no author-owned server and sends no telemetry.

## Building from source

### Prerequisites

- Cities: Skylines II with the official modding toolchain installed (in-game `Mods → Modding Tools → Install`)
- .NET 8 SDK
- Unity 2022.3.62f2 (or the China build `2022.3.62f2c1` — the toolchain locates Unity via registry, not folder name)

### Build

```bash
dotnet build -c Release
```

Output deploys to `AppData\LocalLow\Colossal Order\Cities Skylines II\Mods\Cs2AutoTranslator\`.

### Known toolchain quirks on this machine

The csproj overrides two official Mod.targets (`RunModPostProcessor` and `RunModPublisher`) to inject `DOTNET_ROLL_FORWARD=Major`. Both tools target net6.0 but only .NET 8 runtime is installed, and their `runtimeconfig.json` files don't declare `rollForward`. Without the override, both fail with "You must install or update .NET to run this application" (exit code -2147450730).

## Project layout

```
Cs2AutoTranslator/
├── Mod.cs              # IMod entry point, Harmony patches, translation pipeline
├── L10n.cs             # 12-language built-in translations for the mod's own UI
├── Setting.cs          # ModSetting + SettingsUI definitions
├── Scope.cs            # Translation scope toggles
├── LanguageCatalog.cs  # Supported target language list
├── Properties/
│   ├── PublishConfiguration.xml   # Paradox Mods publish metadata
│   ├── PublishProfiles/           # PublishNewMod / PublishNewVersion / UpdatePublishedConfiguration
│   ├── Thumbnail.png              # 950x500 8-bit RGBA
│   ├── Screenshot1.png
│   └── Screenshot2.png
└── Cs2AutoTranslator.csproj
```

## Version

Current: **v0.29** · Targets game **1.6.\*** · Platform: Windows (macOS/Linux assemblies are stubs — no Burst code in this mod).

## License

MIT — see [LICENSE](LICENSE).
