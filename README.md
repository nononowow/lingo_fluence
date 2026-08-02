# LingoFluence

A simple, beautiful Windows desktop app for studying German vocabulary, inspired by Anki. Built with WPF (.NET 8) using the MVVM pattern.

## Features

- **Import Anki decks** — load standard `.apkg` packages, including media/audio.
- **Spell study mode** — hear the German audio, type the word, and get it checked and scored. Includes a Hint mode that reveals a few random letters.
- **AI card generation** — if the [Claude CLI](https://github.com/anthropics/claude-code) is installed, describe the cards you want (e.g. "create 20 A1-level German vocabulary flashcards") and let the AI build a deck for you. Generated decks are cached and marked with an 🤖 AI badge.
- **Flip-card AI study** — AI decks study as flip cards showing English explanation, grammar notes, and example sentences, each with text-to-speech.
- **Spaced repetition** — SM-2 based review scheduling to prevent forgetting.
- **Text-to-speech** — German audio playback via Google Translate TTS, cached locally.
- **Copyable fields** — select and copy the German word, example sentence, or explanation.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) (for building)
- Optional: [Claude CLI](https://github.com/anthropics/claude-code) (`npm i -g @anthropic-ai/claude-code`) for AI card generation

## Build & Run

```bash
dotnet build LingoFluence.sln
dotnet run --project LingoFluence
```

App data (database, media, caches) is stored under `%LOCALAPPDATA%\LingoFluence`.

## Packaging (Windows)

Two PowerShell scripts are provided for producing a distributable build:

```powershell
# Build a self-contained single-file executable into .\publish
.\build.ps1

# Build + install for the current user (Start Menu shortcut, no admin needed)
.\install.ps1

# Also add a Desktop shortcut
.\install.ps1 -Desktop
```

`build.ps1` publishes a self-contained `win-x64` executable, so the target
machine does not need the .NET runtime installed. `install.ps1` copies it to
`%LOCALAPPDATA%\Programs\LingoFluence` and creates shortcuts. To uninstall,
delete that folder and the shortcut(s).

## License

Licensed under the [GNU Affero General Public License v3.0](LICENSE).
