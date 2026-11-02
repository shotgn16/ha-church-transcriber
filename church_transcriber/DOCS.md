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
- **Automations**: fire Home Assistant events when a service is ready, or pull the whole transcript or chosen parts (e.g. just the sermon) through a secured API.
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

## Automations & API

Open the **API** page in the app. It has a point-and-click builder that generates the exact URL and Home Assistant YAML for you, with a **Preview** button. Everything below is what it produces.

### Push: trigger automations when a service is ready
The add-on fires Home Assistant events:

| Event | When |
|---|---|
| `church_transcriber_transcript_ready` | A service has finished transcribing |
| `church_transcriber_translation_ready` | A translation has finished (`trigger.event.data.translation` is the language) |
| `church_transcriber_failed` | Processing failed (`trigger.event.data.error`) |

The event carries the parts you choose on the API page (default: overview & sermon notes, scripture references and outline). Example - send the sermon notes to your phone:

```yaml
alias: Sermon notes when a service is ready
triggers:
  - trigger: event
    event_type: church_transcriber_transcript_ready
actions:
  - action: notify.mobile_app_your_phone
    data:
      title: "{{ trigger.event.data.title }}"
      message: "{{ trigger.event.data.summary.text[:1000] }}"
```

You can also set a **webhook** on the API page to receive the same JSON in Node-RED, n8n, etc.

### Pull: fetch a transcript from any automation
Base address from Home Assistant: `http://<add-on hostname>:8099/api/v1` - the API page shows yours (e.g. `http://a1b2c3d4-church-transcriber:8099`). Every request needs the API key from the API page as the header `Authorization: Bearer <key>` (or `X-API-Key: <key>`).

| Endpoint | Purpose |
|---|---|
| `GET /api/v1/transcripts/latest` | The newest finished service (filter with `?channel=` or `?watch=`) |
| `GET /api/v1/transcripts/{id}` | A specific service (returns HTTP 202 with progress while still processing) |
| `GET /api/v1/transcripts` | List services (`?status=ready`, `?q=search`, `?limit=`) with their sections |
| `GET /api/v1/transcripts/{id}/export?format=docx` | Download a file (`docx`, `md`, `txt`, `html`, `srt`; `&lang=`) |
| `POST /api/v1/transcripts` | Queue a service: `{"url": "https://youtu.be/…", "translate_to": ["es"]}` |
| `GET /api/v1/status` | What's processing, waiting and ready |

**Choosing the whole transcript or only parts** (query parameters):

| Parameter | Example | Meaning |
|---|---|---|
| *(none)* | | The whole transcript: overview, scriptures, outline and all sections |
| `parts` | `parts=summary,scriptures` | Any of `summary` (overview & sermon notes), `scriptures`, `outline`, `sections` |
| `kinds` | `kinds=sermon` or `kinds=sermon,scripture` | Only sections of these kinds - works for any service |
| `sections` | `sections=1,3-5` | Only these section numbers (or section ids) |
| `lang` | `lang=es` | A finished translation (falls back to the original with a `warning`) |
| `format` | `format=text` | `json` (default), `text`, `markdown` or `html` |
| `max_chars` | `max_chars=500` | Shorten the text - handy for notifications and TTS |
| `timestamps` | `timestamps=1` | Add `[12:34]` timestamps to paragraphs |

Examples: `/api/v1/transcripts/latest?kinds=sermon&format=text` (just the sermon as text) - `/api/v1/transcripts/latest?parts=summary&lang=es` (Spanish sermon notes).

JSON responses always include a ready-made `text` field combining the selected parts, plus `title`, `date`, `url` and structured `summary`, `scriptures`, `outline` and `sections` (each section has `index`, `kind`, `title`, `start`, `timestamp`, `text`, `html`).

Pull on demand with a `rest_command`:

```yaml
# secrets.yaml
church_transcriber_auth: "Bearer ct_your_key_here"

# configuration.yaml
rest_command:
  church_sermon:
    url: "http://a1b2c3d4-church-transcriber:8099/api/v1/transcripts/latest?kinds=sermon&max_chars=2000"
    headers:
      Authorization: !secret church_transcriber_auth

# in a script/automation
actions:
  - action: rest_command.church_sermon
    response_variable: transcript
  - action: notify.notify
    data:
      message: "{{ transcript.content.text }}"
```

Calls made through the Home Assistant sidebar don't need the key; everything else does. Use **New key** on the API page if a key is ever exposed.

## Tips
- Add your church name, pastors' names and other unusual words in **Settings → Vocabulary**. Whisper uses them to spell names correctly. In the editor, **Find → Add to vocabulary** teaches it a correction for next time.
- Tick **Don't transcribe sung worship** if you only want the spoken parts.
- **More → Re-organise sections automatically** rebuilds sections (e.g. after changing AI model). It replaces your edits.
- Translations follow your sections. If you split or merge sections after translating, translate again to match.

## Troubleshooting

If a service fails, open it: the red message explains the cause (temporary network problems retry automatically first), **Show details** has the full error (with a **Copy details** button), and **Try again** resumes from where it stopped - there's no need to delete the service.

**DNS: "Home Assistant couldn't look up YouTube's address" / "Temporary failure in name resolution"**
This means Home Assistant itself couldn't turn `www.youtube.com` into an address - it's a network setting on the Home Assistant machine, not a problem with the video. The add-on now retries automatically for about two hours (you'll see **Retrying**), and the **Internet** row on the app's **Settings** page shows whether lookups currently work.

If it keeps happening, point Home Assistant at reliable DNS servers. Install the **Terminal & SSH** add-on, open it and run:

```
ha dns options --servers dns://1.1.1.1 --servers dns://8.8.8.8
ha dns restart
```

Then restart this add-on. Other things worth checking: the VM's network adapter has a working gateway/DNS in ESXi, and your router or Pi-hole/AdGuard isn't blocking YouTube domains. `ha dns info` and `ha resolution info` in the terminal show Home Assistant's DNS status.

**"YouTube asked to confirm this isn't a bot"** - YouTube sometimes blocks automated downloads. Give the add-on your YouTube cookies:
1. On a computer, install a browser extension that exports cookies in *Netscape / cookies.txt* format (for example "Get cookies.txt LOCALLY" for Chrome/Edge, or "cookies.txt" for Firefox).
2. Open youtube.com (signing in is optional but more reliable), and export cookies for youtube.com to a file named `cookies.txt`.
3. Copy it into the `share` folder on Home Assistant as `church_transcriber/cookies.txt` (using the Samba share or File editor add-on), so the path is `/share/church_transcriber/cookies.txt`.
4. Press **Try again** - no restart needed.

**"YouTube refused the download (HTTP 403)" or "couldn't solve YouTube's player challenge"** - YouTube changed something. Restart the add-on (it updates the downloader on start) and press **Try again**.

**"Whisper ran out of memory"** - give the Home Assistant VM more RAM (6 GB or more for `large-v3-turbo`), or set `whisper_model` to `medium` or `small`, restart the add-on and press **Try again** (the download is reused).

**"This CPU doesn't expose the instructions Whisper needs"** - in ESXi, edit the VM's CPU settings so AVX/AVX2 are passed through (check the EVC / CPU compatibility mode), then restart the VM.

**"Couldn't download the Whisper model"** - the first transcription downloads the model from huggingface.co; check the machine's internet access and press **Try again**.

**Stuck on "Stream ended - waiting for YouTube to finish processing"** - after a live stream YouTube can take a while (sometimes hours for long streams) before the recording can be downloaded. The add-on checks every 15 minutes and continues automatically.

**Ollama shows "Not reachable"** on the Settings page - check `ollama_url`, that `OLLAMA_HOST=0.0.0.0` is set on the Ollama machine, and that the firewall allows port 11434.

**Transcription slow** - choose a smaller `whisper_model`, give the VM more vCPUs, and confirm AVX2 is available.

Logs are on the add-on's **Log** tab.
