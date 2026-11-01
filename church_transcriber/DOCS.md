# Church Service Transcriber

Turns YouTube church services into organised, editable, translatable notes - all on your own network.

- **Paste a YouTube link** (or several) and the service is downloaded and transcribed.
- **Organised automatically** into Welcome, Worship, Prayer, Announcements, Scripture Reading, Sermon, Offering, Communion, Benediction and more - using the video's own chapters if the church added them, otherwise your local AI model, otherwise a built-in detector.
- **Overview and sermon notes** (title, main scripture, key points) written by your AI model.
- **Scripture references** detected and listed (e.g. "Romans 8:28-39"), each clickable to jump there.
- **Rich-text editor**: bold, italic, underline, highlight, headings, quotes, lists, find & replace, split/merge/rename sections. Autosaves.
- **Click any timestamp** to hear that moment; the text follows the audio as it plays - ideal for correcting.
- **Translate** into 45+ languages with your local AI model, then edit the translation too.
- **Scheduled & live services**: add a link before the service starts; it waits, then processes when the stream ends.
- **Watch your church's channel**: new services are picked up and transcribed automatically.
- **Export** to Word (.docx), PDF (print), Markdown, plain text, web page and subtitles (.srt).
- Works on **Windows, macOS, Android and iPhone/iPad** - in any browser or the Home Assistant app.

Nothing leaves your network except the download from YouTube: speech-to-text (Whisper) runs inside the add-on and all AI work runs on your Ollama server.

## Installation

### Option A - Local add-on (quickest)
1. Install the **Samba share** or **Advanced SSH & Web Terminal** add-on if you don't have one.
2. Copy the `church_transcriber` folder into the `addons` share on Home Assistant, so you have `/addons/church_transcriber/config.yaml`.
3. In Home Assistant go to **Settings → Add-ons → Add-on Store**, open the **⋮** menu and choose **Check for updates**.
4. **Church Service Transcriber** appears under **Local add-ons**. Open it and click **Install** (the first build takes 5-15 minutes).

### Option B - From GitHub
In the Add-on Store choose **⋮ → Repositories**, add `https://github.com/shotgn16/ha-church-transcriber`, then find **Church Service Transcriber** in the store and click **Install**.

## Configuration

Set these on the add-on's **Configuration** tab, then start the add-on.

| Option | What it does |
|---|---|
| `ollama_url` | Your Ollama server, e.g. `http://192.168.1.50:11434`. **Required for AI features.** |
| `ollama_model` | Leave blank to auto-select (prefers a Qwen *instruct* model), or set e.g. `qwen2.5:7b-instruct`. You can also pick from a list on the app's Settings page. |
| `ollama_context` | Context window sent to Ollama (default 16384). Lower to 8192 if Ollama runs out of memory. |
| `whisper_model` | Speech-to-text model. `large-v3-turbo` (default) is the best accuracy/speed balance; `medium`/`small` are faster. `distil-large-v3` is fast but English-only. |
| `whisper_compute` | Leave on `int8` for CPUs. |
| `whisper_threads` | 0 = use all vCPUs. |
| `use_ai_for_sections` | Use Ollama to split services into sections (otherwise a built-in detector). |
| `download_video` | Also save the full MP4 of each service to the media folder. |
| `media_folder` | Where videos are saved (default `/share/church_transcriber`). |
| `access_password` | Only needed if you enable the direct port (below). |
| `cookies_file` | Optional YouTube cookies.txt if YouTube ever blocks downloads. |
| `update_ytdlp_on_start` | Keeps the YouTube downloader current. Leave on. |

### Ollama setup
- Ollama must accept connections from your network. On the Ollama machine set `OLLAMA_HOST=0.0.0.0` (Linux: `systemctl edit ollama` → `Environment="OLLAMA_HOST=0.0.0.0"`; Windows: add it as a system environment variable and restart Ollama).
- Good models: `qwen2.5:7b-instruct` (fast), `qwen2.5:14b-instruct` (better titles, summaries and translations). Any Qwen instruct model you already have will work.
- The add-on works in small chunks so a 7B model handles a 2-hour service fine.

### ESXi VM sizing
- **CPU:** 4+ vCPUs recommended for `large-v3-turbo`. In the VM's CPU settings make sure **AVX2 is exposed to the guest** (check that EVC / CPU compatibility mode isn't hiding it) - Whisper is several times slower without it.
- **RAM:** allow ~3 GB for the add-on while transcribing (large-v3-turbo int8).
- **Disk:** the Whisper model is downloaded once (~1.6 GB). Audio is ~25 MB per hour of service.
- **Speed guide:** on a modern 4-vCPU VM, expect very roughly 30-90 minutes to transcribe a 90-minute service with `large-v3-turbo`; `small` is about 3x faster. Services queue and process one at a time in the background.

## Using it on your devices
- **Easiest:** click **Transcriber** in the Home Assistant sidebar. In the **Home Assistant app** on iPhone/Android it is in the sidebar too, and it works remotely wherever your Home Assistant does (e.g. via Nabu Casa).
- **Home screen app:** open it in Safari/Chrome and choose *Add to Home Screen*.
- **Direct port (optional):** on the add-on's Network section set a port for `8099/tcp`, set `access_password`, then browse to `http://<home-assistant-ip>:<port>`. Only do this on your home network; use the sidebar for remote access.

## Tips
- Add your church name, pastors' names and other unusual words in **Settings → Vocabulary**. Whisper uses them to spell names correctly. In the editor, **Find → Add to vocabulary** teaches it a correction for next time.
- Tick **Don't transcribe sung worship** if you only want the spoken parts.
- **More → Re-organise sections automatically** rebuilds sections (e.g. after changing AI model). It replaces your edits.
- Translations follow your sections. If you split or merge sections after translating, translate again to match.

## Troubleshooting
- **"Not reachable" for Ollama on the Settings page** - check `ollama_url`, that `OLLAMA_HOST=0.0.0.0` is set, and that the firewall allows port 11434.
- **YouTube download errors** - restart the add-on (it updates the downloader on start). If it persists, export YouTube cookies to `/share/church_transcriber/cookies.txt`.
- **Transcription slow** - choose a smaller `whisper_model`, give the VM more vCPUs, and confirm AVX2 is available.
- Logs are on the add-on's **Log** tab.
