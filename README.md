# BridgeTheLanguageGap / 拯救语言不通

Machine-translates Cities: Skylines II UI text (game + mods) into your language, on demand, live.

把都市天际线2 的界面文字（游戏本体 + 模组）按需机翻成你选的目标语言，实时生效、无需重启。

## Paradox Mods

**[Bridge the Language Gap (ModId 157802)](https://mods.paradoxplaza.com/mods/157802/windows)**

Use the `/windows` URL: `/mods/157802/any` renders "the mod is corrupted or the selected operating system is not supported".

Subscribe in-game via `Mods → Paradox Mods`, or install through Skyve.

## What it does

Cities: Skylines II leaves a lot of UI text untranslated across languages, and mod UI is almost always English-only. This mod hooks into the game's `UILocalizationManager.Translate` and `NameSystem.GetRenderedLabelName` methods with Harmony patches, then machine-translates the strings you actually see into your chosen target language. Translation happens lazily in the background — you won't notice it working until the text flips to your language.

## Features

- **On-demand translation**: only translates strings you actually see and have enabled in scope; no bulk pre-translation.
- **Local disk cache**: each string is translated once, then cached to `ModsData\Cs2AutoTranslator\translation.cache`. Works offline afterwards. API usage stays very low.
- **Configurable scope**: toggle game-only, mod-only, or both; toggle asset names/descriptions/mod names separately.
- **Multi-engine support**: Microsoft Azure (default), DeepL, Baidu, Google.
- **44 target languages** ship in `LanguageCatalog.cs`, plus the current game locale. Text that is already in your target language is skipped locally, as are key bindings and pure `{PLACEHOLDER}` strings.
- **Mod UI in 12 languages**: the mod's own settings page ships with translations for the game's 12 built-in locales; for anything else it machine-translates its own text with the selected engine.
- **One-click test / save / clear cache / open log folder**: all inside the mod's settings page.
- **Dedicated log file**: separate from the game's log, easy to attach when reporting issues.

## Privacy

API keys are stored in plaintext **on your own machine only** (`ModsSettings\Cs2AutoTranslator.json`) and are sent solely to the translation service you select. The mod contacts no author-owned server and sends no telemetry.

Since v0.30 that JSON is the only file this mod reads or writes for settings. The game's own settings store may still leave a `Cs2AutoTranslator.coc` in the user-data root, holding a stale snapshot of pre-v0.30 fields; the mod neither reads nor writes it, so removing it cannot break the mod. Whether the store re-creates that file on exit has not been verified.

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
├── Mod.cs              # IMod entry point, Harmony patches, translation pipeline, settings store
├── TextKit.cs          # Pure-BCL string layer: TransGuard (placeholder masking/validation) + Json reader
├── L10n.cs             # Built-in translations for the mod's own UI (the game's 12 locales)
├── Setting.cs          # ModSetting + SettingsUI definitions
├── Scope.cs            # Key classification + local skip rules
├── LanguageCatalog.cs  # Supported target language list
├── Properties/
│   ├── PublishConfiguration.xml   # Paradox Mods publish metadata — a MIRROR of the live page, not a draft
│   ├── PublishProfiles/           # PublishNewMod / PublishNewVersion / UpdatePublishedConfiguration
│   ├── Thumbnail.png              # 950x500 8-bit RGBA
│   └── Screenshot1..2.png / Screenshot3..4.jpg   # The four images that are actually live
└── Cs2AutoTranslator.csproj
```

`TextKit.cs` and `Scope.cs` deliberately reference no game type, so an offline test shell
(`<Compile Include>`s them into its own assembly) can assert their behaviour without launching the game.
Publishing metadata is guarded by `preflight-publish.mjs` in the handover workspace — run it before any
`Update`/`NewVersion`, because those commands overwrite the live page field-by-field from this xml.

Screenshots 3 and 4 stay JPEG on purpose: `NewVersion` rejects any image over 2.1 MB, and converting these
to PNG inflates them ~10x (278 KB → 2.8 MB). `Update` validates nothing about images, so a successful
`Update` is not evidence a `NewVersion` will pass.

When re-uploading binaries use **`NewVersion`**, not `Update`: `Update` pushes `<ModVersion>` along with the
metadata, which burns the version label without uploading anything — the following `NewVersion` then fails
with `User version already exists for this mod`.

## Version

Current source: **v0.30** — published on Paradox Mods 2026-09-06 (platform `modVersion 2`) · Targets game **1.6.\*** · Platform: Windows (macOS/Linux assemblies are stubs — no Burst code in this mod).

## License

MIT — see [LICENSE](LICENSE).
