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
- **Configurable scope**: six independent toggles on the settings page — mod names, mod settings & in-game panels, asset names, asset descriptions, game core, and world-rendered text (road/district labels; since v1.0 it translates and refreshes automatically when a save loads).
- **Multi-engine support**: 12 engines in `SettingsUI.Engine` — 3 that need nothing (Google, DuckDuckGo, MyMemory), 4 that need a registered key with a free tier (Yandex, Microsoft, DeepL, Baidu), and 5 AI providers (Gemini, Groq, OpenRouter, SiliconFlow, Cloudflare) — plus a **custom** endpoint (openai / anthropic / gemini wire formats) for anything else. Token usage is counted locally and shown in the settings.
- **Google runs on two keyless channels**: since v1.0.4 the web UI's own `batchexecute` endpoint is tried first and the classic `translate_a/single` clients are the fallback — neither needs a key or a quota. When both fail, Google is blocking the exit IP in use, so switching proxy node or engine is what actually helps.
- **DuckDuckGo is usable again without a key (v1.1.0)**: it talks to the endpoint DuckDuckGo's own search page uses — raw UTF-8 text in, JSON out (`translated`), 1000 characters per request. Earlier builds misread it as dead. Same exit-IP caveat as Google: from mainland China it usually needs a proxy, and Japanese source text comes back unreliable.
- **Yandex now uses the live official endpoint (v1.1.0)**: `translate.yandex.net/api/v1.5/tr.json/translate` with an API key from `console.yandex.cloud`. The old `translate.api.yandex.net` host is gone (NXDOMAIN) and the keyless web session is closed by Yandex, so the key is required.
- **Dropdown option lists translate with the page (v1.1.0)**: option lists in the settings UI — the game's own enum/flag fields and other mods' `DropdownField<T>` — are picked up by the same pass that refreshes the rest of the page, so their items get queued without you opening the list first; turning the matching scope toggle off, or switching target language, puts the original text back.
- **Only the settings page you actually have open is queued (v1.1.4)**: the sweep follows the game's own active-page binding (`OptionsUISystem.m_ActivePageBinding`, the same check the game uses in `OnUpdate`), so switching translation on no longer pushes all 56 pages / ~2,000 strings at once — that flood used to hit the free tiers' rate limit within two seconds and leave the page in front of you silent. Each sweep enqueues at most 24 new strings and the rest trickle in; moving to another page wakes the sweep up again, and if the active page can't be read the sweep falls back to scanning everything (never "opened but not translated").
- **Rate-limit waits step 3 → 6 → 10 → 20 s instead of a flat 20 s (v1.1.4)**, and world labels (road/district names) are throttled separately from UI text, so a background flood can no longer freeze what you are looking at.
- **Mod option titles and descriptions translate too (v1.1.3)**: several mods hand their English fallback string straight to `NamedWidget.displayName/description`, which never reaches `UILocalizationManager.Translate`, so those lines stayed English no matter what was ticked.
- **External failures don't mark text as dead (v1.1.2)**: rate limits and connection problems no longer consume each string's retry budget, which is what made "six or seven options, only half translated, and it never catches up" happen.
- **Proxy-aware egress (v1.1.2)**: when requests fail through the system proxy the mod retries over a direct connection (and back again after a couple of minutes), and both the log and the one-click test say which leg of the route failed — so "suddenly nothing translates, whichever engine I pick" is no longer read as a bad key.
- **What deliberately stays untranslated**: the language picker keeps each language's native spelling (`English` / `日本語` / `Tiếng Việt`), value presets like `5 min` / `300dpi` / `16 x 9` / `60 fps` / `#FF8800` are left alone, proper nouns (`Discord`, `Skyve`, `Move It`) keep their names, and key bindings and file paths never enter the pipeline.
- **Reverting is a sweep of its own (v1.1.5)**: text this mod writes into game widget objects is a fourth place, not a cache, so *clear cache*, the master switch and a scope toggle each run one whole-tree revert pass on click — every settings page goes back to the original text, including pages you never opened — while queueing no new requests. Results still in flight when you cleared can no longer refill the cache file you just deleted.
- **Uninstall is clean (v1.1.3)**: `OnDispose` unpatches this mod's own Harmony patches, nothing is written into saves and there is no `ModSystem`/`ModComponent`, so disabling or deleting the mod restores vanilla behaviour with no leftover errors.
- **Hotkeys**: two actions, *toggle translation* and *retranslate what's on screen*. Both ship **unbound** — you assign the key yourself. Bindings are stored in this mod's own JSON rather than the framework's `KeybindingSettings`, because the mod re-registers its actions on every launch and that reset would wipe them.
- **44 target languages** ship in `LanguageCatalog.cs`, plus the current game locale. Text that is already in your target language is skipped locally, as are key bindings and pure `{PLACEHOLDER}` strings.
- **Mod UI in 12 languages**: the mod's own settings page ships with translations for the game's 12 built-in locales; for anything else it machine-translates its own text with the selected engine.
- **One-click test / save / clear cache / open log folder**: all inside the mod's settings page.
- **Dedicated log file**: separate from the game's log, easy to attach when reporting issues.

## Privacy

API keys are stored in plaintext **on your own machine only** (`ModsSettings\Cs2AutoTranslator.json`) and are sent solely to the translation service you select. The mod contacts no author-owned server and sends no telemetry.

Since v0.30 that JSON is the only file this mod reads or writes for settings. Players updating from v0.29 may still have a `Cs2AutoTranslator.coc` in the user-data root, holding a stale snapshot of pre-v0.30 fields; the game's settings store rewrites that file if it already exists but never creates it, so deleting it is safe and permanent — the mod does not read it either way.

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

**The deployed `0Harmony.dll` is 2.3.3 while the project still compiles against 2.2.2.** .NET binds by assembly
*simple name* and ignores the version, so whichever copy the loader resolves first fixes the API surface for
every mod in the session — and `Write Everywhere` calls `HarmonyMethod.op_Implicit(MethodInfo)`, which only
exists from 2.3.0 on. Code compiled against 2.2.2 runs fine on 2.3.3, not the other way round, so shipping
2.3.3 is the one-way-compatible choice. `Cs2AutoTranslator.csproj` `PackageDownload`s 2.3.3 (kept out of the
reference graph) and copies it over `$(OutDir)` **inside** `RunModPostProcessor`, after the post processor's
`Exec`: the official `ILHasher` cannot read that file's merged metadata (`BadImageFormatException: Read out of
bounds`) and hangs, so it must never be the version the tool inspects. Verify with the build log line
`SwapHarmonyRuntime:`, the in-game log line that prints the effective Harmony version, and gate row **T2b**
in `scripts/verify.mjs`, which compares the deployed DLL's sha256 against the NuGet cache copy.

Note this changes the shared Harmony for every player who installs this mod, and that the swapped DLL is the
one file in the package the official post processor did not validate.

## Project layout

```
Cs2AutoTranslator/
├── Mod.cs              # IMod entry point, Harmony patches, translation pipeline, settings store
├── DropdownKit.cs      # Pure-BCL decision rules for dropdown option lists (apply / restore / enqueue / keep)
├── EngineKit.cs        # Every translation engine: free endpoints, paid APIs, custom AI wire formats, token counting
├── TextKit.cs          # Pure-BCL string layer: TransGuard (placeholder masking/validation) + Json reader
├── L10n.cs             # Built-in translations for the mod's own UI (the game's 12 locales)
├── Setting.cs          # ModSetting + SettingsUI definitions
├── Scope.cs            # Key classification + local skip rules
├── LanguageCatalog.cs  # Supported target language list
├── ModInfo.cs          # Mod & author information page (pure-BCL, also reused by the offline test shell)
├── Properties/
│   ├── PublishConfiguration.xml   # Paradox Mods publish metadata — a MIRROR of the live page, not a draft
│   ├── PublishProfiles/           # PublishNewMod / PublishNewVersion / UpdatePublishedConfiguration
│   ├── Thumbnail.jpg              # 1254x1254, the store cover
│   └── Screenshot0.jpg + Screenshot1..5.png   # The six gallery images, in the order they appear on the page
└── Cs2AutoTranslator.csproj
```

`Scope.cs`, `TextKit.cs`, `EngineKit.cs`, `LanguageCatalog.cs`, `ModInfo.cs` and `DropdownKit.cs` deliberately
reference no game type, so an offline test shell (`<Compile Include>`s them into its own assembly — same
assembly, so `internal` members are visible without an `InternalsVisibleTo` in the shipped DLL) can assert
their behaviour without launching the game.
Publishing metadata is guarded by `preflight-publish.mjs` in the handover workspace — run it before any
`Update`/`NewVersion`, because those commands overwrite the live page field-by-field from this xml.

The server caps **each** store image at 2.1 MB (`NewVersion` fails with `Image size should not exceed 2.1 MB`).
`Update` validates nothing about images, so a successful `Update` is not evidence a `NewVersion` will pass.
The five gallery PNGs are the author's originals run through `imgtool optipng`: identical pixels and
dimensions, only the encoding changes (per-row adaptive filter + `SmallestSize` deflate) — the largest one
goes 2,141,333 → 2,089,365 B. The cover is `imgtool jpg q95`, because a photographic poster cannot fit
2.1 MB losslessly (2,238,704 B at best) and JPEG is what the platform itself stores for covers.

When re-uploading binaries use **`NewVersion`**, not `Update`: `Update` pushes `<ModVersion>` along with the
metadata, which burns the version label without uploading anything — the following `NewVersion` then fails
with `User version already exists for this mod`.

## Version

Current source: **v1.1.5** · Published on Paradox Mods: **v1.1.5**, 2026-10-03 (platform `modVersion` 5, package 917,381 B; previously v1.0.4 on 2026-09-25) · Targets game **1.6.\*** · Platform: Windows (macOS/Linux assemblies are stubs — no Burst code in this mod).

## License

MIT — see [LICENSE](LICENSE).
