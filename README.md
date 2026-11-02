# Home Assistant add-on: Church Service Transcriber

[![Open your Home Assistant instance and show the add add-on repository dialog with this repository pre-filled.](https://my.home-assistant.io/badges/supervisor_add_addon_repository.svg)](https://my.home-assistant.io/redirect/supervisor_add_addon_repository/?repository_url=https%3A%2F%2Fgithub.com%2Fshotgn16%2Fha-church-transcriber)

![Version](https://img.shields.io/badge/version-1.1.5-blue.svg) ![amd64](https://img.shields.io/badge/amd64-yes-green.svg) ![aarch64](https://img.shields.io/badge/aarch64-yes-green.svg)

A self-hosted web app, installed as a Home Assistant add-on, that turns YouTube church services into organised, editable and translatable notes. Speech-to-text runs inside the add-on and the AI work runs on your own Ollama server - nothing is sent to cloud services.

## Features
- **Transcribe** any YouTube service (paste links, or watch your church's channel so new services are picked up automatically - scheduled and live streams wait until they've finished).
- **Organised** into Welcome, Worship, Prayer, Announcements, Scripture Reading, Sermon, Offering, Communion, Benediction… using the video's chapters, your local AI model, or a built-in detector.
- **Sermon notes** - overview, sermon title, main scripture and key points - plus detected **Bible references**.
- **Rich-text editor** with audio playback that follows the text, find & replace, and split/merge/rename sections.
- **Translate** into 45+ languages and edit the translation.
- **Export** to Word, PDF, Markdown, text, HTML and subtitles.
- **Automations** - Home Assistant events when a service is ready, and a secured API to fetch the whole transcript or chosen parts (e.g. just the sermon).
- Works on **Windows, macOS, Android and iPhone/iPad** through the Home Assistant sidebar or app.

## Requirements
- Home Assistant OS or Supervised (add-ons required), on 64-bit x86 with **AVX2** or ARM64.
- **4+ vCPUs** recommended.
- **RAM** depends on the Whisper model - **6 GB for the VM** with the default model:

| `whisper_model` | Download | RAM while transcribing | Recommended VM RAM | Notes |
|---|---|---|---|---|
| `tiny` | 76 MB | ~0.6 GB | 3 GB | Rough drafts only |
| `base` | 145 MB | ~0.7 GB | 3 GB | |
| `small` | 484 MB | ~1.2 GB | 4 GB | Good for clear speech |
| `medium` | 1.53 GB | ~2.3 GB | 5 GB | Very good |
| `distil-large-v3` | 1.51 GB | ~2.3 GB | 5 GB | Fast, English only |
| **`large-v3-turbo`** (default) | 1.62 GB | ~2.5 GB | **6 GB** | Excellent, recommended |
| `large-v3` | 3.09 GB | ~4.3 GB | 8 GB | Best, slowest |

  RAM figures are approximate peaks for a 1½-hour service with `int8`; see the [documentation](church_transcriber/DOCS.md#choosing-a-whisper-model) for how they were derived.
- Optional: an [Ollama](https://ollama.com) server on your network with an instruct model (Qwen recommended) for sections, sermon notes and translation.

## Install
1. Click the **Add repository** button above (or in Home Assistant go to **Settings → Add-ons → Add-on Store → ⋮ → Repositories** and add `https://github.com/shotgn16/ha-church-transcriber`).
2. Find **Church Service Transcriber** in the Add-on Store and click **Install** (it builds on your machine - about 5-15 minutes).
3. On the **Configuration** tab set `ollama_url` to your Ollama server (and pick a smaller `whisper_model` if the VM has less than 6 GB RAM), then **Start**.
4. Open **Transcriber** in the sidebar.

Full documentation - configuration, model choice, automations/API, backups and troubleshooting - is in [`church_transcriber/DOCS.md`](church_transcriber/DOCS.md), also shown on the add-on's **Documentation** tab. Release notes: [`CHANGELOG.md`](church_transcriber/CHANGELOG.md).

## How it works
```
YouTube link / watched channel
        │  yt-dlp (waits for scheduled & live streams to finish; retries network errors)
        ▼
   audio (AAC, kept for playback)
        │  ffmpeg → faster-whisper (runs inside the add-on)
        ▼
 timed transcript ──► sections: YouTube chapters → Ollama (Qwen) → built-in detector
        │                         + overview, sermon notes, scripture references
        ▼
 rich-text editor (any device) ──► translate with Ollama ──► export .docx / PDF / .md / .txt / .html / .srt
        │
        └──► Home Assistant events / webhook / REST API (/api/v1) for automations
```

**Stack:** ASP.NET Core (.NET 10 LTS) minimal API + vanilla JS front end (no build step), yt-dlp + Deno, ffmpeg, faster-whisper 1.2.1 (CTranslate2), Ollama native API. Data is stored as JSON files in the add-on's `/data` folder and included in Home Assistant backups (the Whisper model is excluded).

## Project layout
```
repository.yaml                 add-on repository metadata
church_transcriber/
  config.yaml, build.yaml       add-on manifest and base images
  Dockerfile, run.sh            container build (multi-stage .NET build + Python tools)
  DOCS.md                       user documentation (shown in Home Assistant)
  CHANGELOG.md                  release notes (shown in Home Assistant)
  translations/en.yaml          option labels in the HA UI
  app/
    Program.cs                  web host, auth, UI API and automation API (/api/v1)
    Models.cs, AddonOptions.cs  data model and add-on options
    scripts/transcribe.py       faster-whisper runner (ffmpeg decoding, model caching)
    Services/
      YouTube.cs                yt-dlp: probe, download, channel listing, error explanations
      Transcriber.cs            runs transcribe.py, progress and failure explanations
      Sectioner.cs              chapters / Ollama / heuristic church-service sectioning
      Ollama.cs                 local model client (model auto-selection, JSON mode)
      Translator.cs             HTML-preserving translation
      Scripture.cs              Bible reference detection
      DocumentBuilder.cs        segments → timestamped paragraphs
      Exporter.cs               .docx (OpenXML), .md, .txt, .html, .srt
      ContentApi.cs             part selection for the API and notifications
      Notifier.cs               Home Assistant events and webhook
      Network.cs                transient-error detection and DNS checks
      Jobs.cs                   job queue, workers, retries, scheduler, channel watches
      Store.cs                  JSON persistence
    wwwroot/                    single-page app (index.html, app.js, app.css, PWA files)
```

## Local development
```bash
cd church_transcriber/app
DATA_DIR=./data dotnet run      # needs yt-dlp, ffmpeg and python3 with faster-whisper on PATH
```
Put an `options.json` (same keys as `config.yaml` options) in `./data` to point at your Ollama server. To release a change, bump `version` in `config.yaml` (and `<Version>` in the `.csproj`) and add a `CHANGELOG.md` entry - Home Assistant offers the update when the version changes.
