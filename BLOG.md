# LingoFluence: Language Learning That Flows

*Building a beautiful, AI-powered companion for mastering a new language — starting with German, growing toward fluency everywhere.*

---

## Why LingoFluence?

Learning a language is really two problems wearing one coat. The first is **memory**: thousands of words and grammar patterns that have to move from "I've seen that" to "I know that." The second is **use**: actually speaking, responding, and thinking in the language under real pressure.

Most tools solve one and ignore the other. Flashcard apps drill memory but never make you talk. Conversation apps get you talking but leave retention to chance.

LingoFluence is being built to close that gap — a single app that takes you from your first flashcard all the way to a mock interview conducted entirely in your target language.

Today it's a focused, beautiful Windows desktop app for German vocabulary. Here's where it stands, and where it's going.

---

## What LingoFluence Does Today

LingoFluence is a Windows desktop app (WPF, .NET 8) inspired by Anki, with a clean, modern interface and a few things Anki doesn't give you out of the box.

### 📦 Import your existing decks
Bring in any standard Anki `.apkg` package — cards, media, and audio all come along. Re-importing the same file replaces the old deck instead of duplicating it, so your library stays tidy.

### ✍️ Spell study mode
Instead of passively flipping cards, you *type* the answer. Hear the German audio, spell the word, and get it checked and scored. Stuck? **Hint mode** reveals a few random letters (different each time) so you finish the rest yourself — active recall with a safety net.

### 🤖 AI card generation
This is the feature that changes the workflow. If you have the [Claude CLI](https://github.com/anthropics/claude-code) installed, just *describe* the deck you want:

> "Create 20 A1-level German vocabulary flashcards about travel."

LingoFluence asks the AI to generate the cards — German word, English meaning, grammar notes, and example sentences — and builds the deck for you. Generated decks are **cached** (so re-importing is instant) and marked with a purple 🤖 **AI badge** so you always know their origin.

### 🔄 Flip-card AI study with speech
AI decks study as flip cards: the German word on the front, and on the back the English explanation, grammar note, and example sentences — each with a 🔊 text-to-speech button so you hear correct pronunciation every time.

### 🧠 Spaced repetition that actually schedules
An SM-2 based algorithm decides when each card comes back. Cards you find hard reappear more often; cards you know fade into the background. This is the engine that fights the forgetting curve.

### 🔊 Text-to-speech everywhere
German audio playback via Google Translate TTS, cached locally so it's fast and works offline after the first play.

### 📋 Copyable fields
Select and copy the German word, example sentence, or explanation — handy when you're taking notes or cross-referencing.

No login. No account. No subscription. Import a deck and start learning.

---

## The Roadmap: From Words to Fluency

Vocabulary is the foundation, not the goal. The next chapters of LingoFluence are about **producing** the language, not just recognizing it.

### 🎙️ Speaking Practice *(coming soon)*

Reading a word and *saying* it are different skills. The upcoming speaking practice mode will let you:

- **Speak your answers aloud** instead of typing them, with speech recognition checking your pronunciation.
- **Get feedback on accuracy** — which sounds landed, which drifted, and how to fix them.
- **Practice example sentences** out loud, building the muscle memory that makes speech automatic.
- **Track pronunciation progress** over time, so you can hear yourself improve.

The goal: close the loop between "I know this word" and "I can say this word so a native speaker understands me."

### 💼 Mock Interview *(coming soon)*

The ultimate test of fluency is holding a real conversation with stakes. Mock Interview mode will simulate exactly that:

- **AI-driven conversations** in your target language, scaled to your level.
- **Scenario-based practice** — job interviews, apartment viewings, doctor visits, casual small talk.
- **Real-time responses** where the AI plays the other side and keeps the conversation moving.
- **Post-session feedback** on vocabulary range, grammar, and fluency, with specific things to work on next.

This turns LingoFluence from a study tool into a **rehearsal space** — a place to be nervous and make mistakes *before* the moment that counts.

### 🌍 Every Platform *(coming soon)*

LingoFluence started on Windows, but language learning happens everywhere — on the couch, on the train, waiting in line. The plan is to bring the full experience to:

- **📱 iOS** — study and speaking practice in your pocket.
- **🤖 Android** — the same, for the other half of the world.
- **💻 macOS** — a first-class desktop app for Mac users.

One account, one synced library, the same beautiful experience whether you're at your desk or on the go.

---

## Built in the Open

LingoFluence is **open source under the AGPL-3.0 license**. The code is on GitHub, contributions are welcome, and the roadmap is public. If you want to shape how a modern language app should work — or just learn from the code — dive in.

**Repository:** [github.com/nononowow/lingo_fluence](https://github.com/nononowow/lingo_fluence)

### Try it now (Windows)

```powershell
# Build a self-contained executable
.\build.ps1

# Or build + install with a Start Menu shortcut
.\install.ps1
```

Then import an Anki deck, or point it at the Claude CLI and let the AI build one for you.

---

## What's Next

The vision is simple: **one app that carries you from your very first word to a confident conversation** — with the memory science of spaced repetition, the leverage of AI-generated content, and soon, the practice space of speaking and mock interviews, on whatever device you're holding.

Vocabulary today. Speaking and interviews next. Every platform after that.

Language learning that flows. 🌊

*Star the repo to follow along, and open an issue if there's a feature you want to see.*
