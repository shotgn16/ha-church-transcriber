# Home Assistant add-on: Church Service Transcriber

A self-hosted web app, installed as a Home Assistant add-on, that turns YouTube church services into organised, editable and translatable notes.

```
YouTube link / watched channel
        │  yt-dlp (waits for scheduled & live streams to finish)
        ▼
   audio (AAC, kept for playback)
        │  faster-whisper (runs inside the add-on)
        ▼
 timed transcript ──► sections: YouTube chapters → Ollama (Qwen) → built-in detector
        │                         + overview, sermon notes, scripture references
        ▼
 rich-text editor (any device) ──► translate with Ollama ──► export .docx / PDF / .md / .txt / .html / .srt
```

**Stack:** ASP.NET Core (.NET 10 LTS) minimal API + vanilla JS front end (no build step), yt-dlp + Deno, faster-whisper, Ollama native API. Data is stored as JSON files in the add-on's `/data` folder (included in Home Assistant backups).

## Install
See [`church_transcriber/DOCS.md`](church_transcriber/DOCS.md). In short: copy `church_transcriber/` into Home Assistant's `/addons` folder, refresh the Add-on Store, install, set `ollama_url`, start.

## Project layout
```
repository.yaml                 add-on repository metadata
church_transcriber/
  config.yaml, build.yaml       add-on manifest and base images
  Dockerfile, run.sh            container build (multi-stage .NET build + Python tools)
  DOCS.md                       user documentation (shown in Home Assistant)
  translations/en.yaml          option labels in the HA UI
  app/
    Program.cs                  web host, auth, REST API
    Models.cs, AddonOptions.cs  data model and add-on options
    Services/
      YouTube.cs                yt-dlp: probe, download, channel listing
      Transcriber.cs            faster-whisper runner (+ scripts/transcribe.py)
      Sectioner.cs              chapters / Ollama / heuristic church-service sectioning
      Ollama.cs                 local model client (model auto-selection, JSON mode)
      Translator.cs             HTML-preserving translation
      Scripture.cs              Bible reference detection
      DocumentBuilder.cs        segments → timestamped paragraphs
      Exporter.cs               .docx (OpenXML), .md, .txt, .html, .srt
      Jobs.cs                   job queue, workers, scheduler, channel watches
      Store.cs                  JSON persistence
    wwwroot/                    single-page app (index.html, app.js, app.css, PWA files)
```

## Local development
```bash
cd church_transcriber/app
DATA_DIR=./data dotnet run      # needs yt-dlp, ffmpeg and python3 with faster-whisper on PATH
```
Put an `options.json` (same keys as `config.yaml` options) in `./data` to point at your Ollama server.
