# Church Service Transcriber

Turns YouTube church services into organised, editable, translatable notes - all on your own network.

- **Paste a YouTube link** (or several) and the service is downloaded and transcribed. Mobile and share links work too.
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
- **Self-healing**: network hiccups retry automatically, and **Try again** resumes a failed service from where it stopped.
- Works on **Windows, macOS, Android and iPhone/iPad** - in any browser or the Home Assistant app.

Nothing leaves your network except the downloads from YouTube (and the one-time Whisper model download): speech-to-text (Whisper) runs inside the add-on and all AI work runs on your Ollama server.

## Requirements

| | Needed |
|---|---|
| Home Assistant | Home Assistant OS or Supervised (add-ons are required) |
| CPU | 64-bit x86 (`amd64`) or ARM (`aarch64`). On x86 the CPU must support **AVX2** - in a VM, make sure it's passed through (see [ESXi / VM notes](#esxi--virtual-machine-notes)) |
| vCPUs | 2 minimum, **4 or more recommended** - transcription speed scales with cores |
| RAM | Depends on the Whisper model - see the [model table](#choosing-a-whisper-model). **6 GB for the VM** suits the default model |
| Disk | The model (75 MB - 3.1 GB, downloaded once) + about 25 MB of audio per hour of service + transcripts (a few hundred KB each) |
| Internet | To reach YouTube and, once, huggingface.co (model download) |
| Ollama (optional) | Any machine on your network running [Ollama](https://ollama.com) with an instruct model (Qwen recommended) - for sections, sermon notes and translation. Without it, the built-in section detector is used and translation is unavailable |

## Installation

1. In Home Assistant go to **Settings → Add-ons → Add-on Store**, open the **⋮** menu → **Repositories**, and add
   `https://github.com/shotgn16/ha-church-transcriber`
   (or use the **Add repository** button on the [GitHub page](https://github.com/shotgn16/ha-church-transcriber)).
2. Find **Church Service Transcriber** in the store and click **Install**. The add-on is built on your machine - the first install takes about 5-15 minutes.
3. On the **Configuration** tab set `ollama_url` (and change `whisper_model` if your VM has less than 6 GB RAM - see below).
4. **Start** the add-on and open **Transcriber** from the sidebar.
5. Open **Settings** in the app and check the **Internet** and **Ollama** rows are green.

The first transcription downloads the Whisper model - expect a pause at *"Downloading the Whisper model"* (about 1.6 GB for the default).

### Updating
When a new version is published, **Settings → Add-ons → Church Service Transcriber** shows **Update** (use **⋮ → Check for updates** in the Add-on Store to look straight away). Your transcripts, settings, API key and downloaded model are kept. See the **Changelog** tab for what changed.

## Choosing a Whisper model

Whisper runs on the CPU inside the add-on. Bigger models are more accurate (especially with names, accents and quiet speakers) but slower and need more memory. Set `whisper_model` on the **Configuration** tab and restart the add-on; the new model downloads on the next transcription.

| `whisper_model` | Download (disk) | RAM while transcribing* | Recommended VM RAM** | Speed on CPU*** | Accuracy |
|---|---|---|---|---|---|
| `tiny` | 76 MB | ~0.6 GB | 3 GB | fastest (~12×) | Basic - misses names, OK for a rough draft |
| `base` | 145 MB | ~0.7 GB | 3 GB | ~8× | Fair |
| `small` | 484 MB | ~1.2 GB | 4 GB | ~4× | Good for clear speech |
| `medium` | 1.53 GB | ~2.3 GB | 5 GB | ~2× | Very good |
| `distil-large-v3` | 1.51 GB | ~2.3 GB | 5 GB | ~3× | Very good - **English only** |
| **`large-v3-turbo`** (default) | 1.62 GB | ~2.5 GB | **6 GB** | ~3× | Excellent - close to `large-v3` |
| `large-v3` | 3.09 GB | ~4.3 GB | 8 GB | 1× (slowest) | Best |

\* Peak memory of the transcription process with `int8` (the default) for a 1½-hour service. Measured reference point: faster-whisper's own benchmark shows `small`/`int8` peaking at about 1.0 GB on 13 minutes of audio; the other figures are estimates scaled from model size. Longer services add about 0.35 GB per hour (the decoded audio is held in memory).
\** Total for the Home Assistant VM: Home Assistant itself plus a typical set of integrations and add-ons uses 1.5-2.5 GB, so this includes headroom. If you run other heavy add-ons, add their usage on top.
\*** Relative speed, `large-v3` = 1×. Real speed depends on your CPU and number of vCPUs - on a modern 4-vCPU VM a 1½-hour service with `large-v3-turbo` takes very roughly 30-90 minutes. Services queue and process one at a time in the background.

**Which to choose?** Keep `large-v3-turbo` if the VM has 6 GB or more. Use `medium` with 5 GB, `small` with 4 GB. If your services are only in English, `distil-large-v3` is a good fast alternative.

`whisper_compute`: leave on `int8` (fastest, least memory). `int8_float32` and `float32` use roughly 2× / 3× the memory for little accuracy gain on CPU.

If the VM runs out of memory the service shows **"Whisper ran out of memory"** - choose a smaller model or add RAM, then press **Try again** (the downloaded audio is reused).

## Configuration

Set these on the add-on's **Configuration** tab, then restart the add-on.

| Option | Default | What it does |
|---|---|---|
| `whisper_model` | `large-v3-turbo` | Speech-to-text model - see [Choosing a Whisper model](#choosing-a-whisper-model). |
| `whisper_compute` | `int8` | Leave on `int8` for CPUs. |
| `whisper_threads` | `0` | CPU threads for Whisper; 0 = all vCPUs. Lower it to leave CPU free for Home Assistant during long transcriptions. |
| `ollama_url` | | Your Ollama server, e.g. `http://192.168.1.50:11434`. **Required for AI features.** |
| `ollama_model` | *(blank)* | Leave blank to auto-select (prefers a Qwen *instruct* model), or set e.g. `qwen2.5:7b-instruct`. You can also pick from a list on the app's **Settings** page. |
| `ollama_context` | `16384` | Context window sent to Ollama. Lower to 8192 if Ollama runs out of memory. |
| `use_ai_for_sections` | `true` | Use Ollama to split services into sections (otherwise the built-in detector). |
| `access_password` | *(blank)* | Only needed if you enable the direct port (below). |
| `download_video` | `false` | Also save the full MP4 of each service (up to 1080p) to the media folder - roughly 1-2 GB per hour. |
| `media_folder` | `/share/church_transcriber` | Where videos are saved. |
| `cookies_file` | `/share/church_transcriber/cookies.txt` | Optional YouTube cookies, only needed if YouTube blocks downloads (see Troubleshooting). |
| `update_ytdlp_on_start` | `true` | Keeps the YouTube downloader current. Leave on - YouTube changes often. |

The app's own **Settings** page holds the rest: church name and vocabulary (helps Whisper spell names), default language, automatic translations, skipping sung worship, export timestamps and the Ollama model.

### Ollama setup
- Ollama must accept connections from your network. On the Ollama machine set `OLLAMA_HOST=0.0.0.0` (Linux: `systemctl edit ollama` → `Environment="OLLAMA_HOST=0.0.0.0"`; Windows: add it as a system environment variable and restart Ollama) and allow port 11434 through its firewall.
- Good models: `qwen2.5:7b-instruct` (fast, ~5 GB RAM/VRAM on the Ollama machine), `qwen2.5:14b-instruct` (better titles, summaries and translations, ~10 GB). Any Qwen instruct model you already have will work.
- The add-on sends the service in small chunks, so a 7B model handles a 2-hour service fine.
- Ollama runs on its own machine, so its memory is separate from the Home Assistant VM's.

### ESXi / virtual machine notes
- **AVX2:** Whisper needs AVX/AVX2. In ESXi, check the VM's CPU compatibility / **EVC mode** isn't hiding them (a "baseline" EVC level can). Without them the service fails with *"This CPU doesn't expose the instructions Whisper needs"*, or runs several times slower.
- **vCPUs:** give the VM 4 or more; set `whisper_threads` lower than the vCPU count if Home Assistant feels sluggish while transcribing.
- **RAM:** size the VM from the [model table](#choosing-a-whisper-model). Avoid memory overcommit/ballooning for this VM if you can - Whisper's peak use is short but large.
- **Disk:** keep at least 5 GB free beyond the model size.

## Using it on your devices
- **Easiest:** click **Transcriber** in the Home Assistant sidebar. In the **Home Assistant app** on iPhone/Android it is in the sidebar too, and it works remotely wherever your Home Assistant does (e.g. via Nabu Casa).
- **Home screen app:** open it in Safari/Chrome and choose *Add to Home Screen*.
- **Direct port (optional):** in the add-on's **Network** section set a port for `8099/tcp`, set `access_password`, then browse to `http://<home-assistant-ip>:<port>`. Only do this on your home network; use the sidebar for remote access.

## How a service is processed
1. **Checking** - reads the video details. Scheduled or live services wait (shown as **Scheduled** / **Live now**) and continue by themselves when the stream has ended.
2. **Downloading** - the audio only (about 25 MB per hour), kept so you can play it in the editor.
3. **Transcribing** - Whisper turns speech into timed text (the longest step).
4. **Organising** - sections, overview, sermon notes and scripture references.
5. **Ready** - edit, translate, export or use in automations. Translations you asked for run afterwards.

If a step fails because of a temporary network problem, the service shows **Retrying** and tries again by itself (after 2, 5, 10, 20, 30 and 60 minutes). Other failures show a red explanation with **Show details**; **Try again** resumes from the last completed step.

## Automations & API

Open the **API** page in the app. It has a point-and-click builder that generates the exact URL and Home Assistant YAML for you, with a **Preview** button. Everything below is what it produces.

### Push: trigger automations when a service is ready
The add-on fires Home Assistant events:

| Event | When |
|---|---|
| `church_transcriber_transcript_ready` | A service has finished transcribing |
| `church_transcriber_translation_ready` | A translation has finished (`trigger.event.data.translation` is the language) |
| `church_transcriber_failed` | Processing failed (`trigger.event.data.error`) |

The event carries the parts you choose on the API page (default: overview & sermon notes, scripture references and outline). **Send a test now** on the API page fires one using your latest service. Example - send the sermon notes to your phone:

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

Section kinds: `prelude`, `welcome`, `worship`, `prayer`, `announcements`, `scripture`, `sermon`, `offering`, `communion`, `baptism`, `testimony`, `response`, `benediction`, `other`.

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

## Your data & backups
- Transcripts, translations, settings, channel watches, the API key and the audio for each service are stored in the add-on's own data folder and **included in Home Assistant backups**.
- The downloaded Whisper model is **left out of backups** (it's large and simply downloads again after a restore).
- Videos saved with `download_video` live in the `share` folder (`/share/church_transcriber/videos`) and follow your `share` backup settings.
- Deleting a service in the app removes its transcript, translations and audio.

## Tips
- Add your church name, pastors' names and other unusual words in **Settings → Vocabulary**. Whisper uses them to spell names correctly. In the editor, **Find → Add to vocabulary** teaches it a correction for next time.
- Tick **Don't transcribe sung worship** if you only want the spoken parts.
- **More → Re-organise sections automatically** rebuilds sections (e.g. after changing AI model). It replaces your edits.
- Translations follow your sections. If you split or merge sections after translating, translate again to match.

## Troubleshooting

If a service fails, open it: the red message explains the cause (temporary network problems retry automatically first), **Show details** has the full error (with a **Copy details** button), and **Try again** resumes from where it stopped - there's no need to delete the service. The add-on's **Log** tab has more detail.

**DNS: "Home Assistant couldn't look up YouTube's address" / "Temporary failure in name resolution"**
Home Assistant itself couldn't turn `www.youtube.com` into an address - it's a network setting on the Home Assistant machine, not a problem with the video. The add-on retries automatically for about two hours (you'll see **Retrying**), and the **Internet** row on the app's **Settings** page shows whether lookups currently work.

If it keeps happening, point Home Assistant at reliable DNS servers. Install the **Terminal & SSH** add-on, open it and run:

```
ha dns options --servers dns://1.1.1.1 --servers dns://8.8.8.8
ha dns restart
```

Then restart this add-on. Also check that the VM's network adapter has a working gateway/DNS in ESXi, and that your router or Pi-hole/AdGuard isn't blocking YouTube domains. `ha dns info` and `ha resolution info` show Home Assistant's DNS status.

**"YouTube asked to confirm this isn't a bot"** - YouTube sometimes blocks automated downloads. Give the add-on your YouTube cookies:
1. On a computer, install a browser extension that exports cookies in *Netscape / cookies.txt* format (for example "Get cookies.txt LOCALLY" for Chrome/Edge, or "cookies.txt" for Firefox).
2. Open youtube.com (signing in is optional but more reliable), and export cookies for youtube.com to a file named `cookies.txt`.
3. Copy it into the `share` folder on Home Assistant as `church_transcriber/cookies.txt` (using the Samba share or File editor add-on), so the path is `/share/church_transcriber/cookies.txt`.
4. Press **Try again** - no restart needed.

**"YouTube refused the download (HTTP 403)" or "couldn't solve YouTube's player challenge"** - YouTube changed something. Restart the add-on (it updates the downloader on start) and press **Try again**.

**"Whisper ran out of memory"** - choose a smaller `whisper_model` or give the VM more RAM (see the [model table](#choosing-a-whisper-model)), restart the add-on and press **Try again**.

**"This CPU doesn't expose the instructions Whisper needs"** - in ESXi, edit the VM's CPU settings so AVX/AVX2 are passed through (check the EVC / CPU compatibility mode), then restart the VM.

**"Couldn't download the Whisper model"** - the first transcription downloads the model from huggingface.co (76 MB - 3.1 GB depending on the model). It retries automatically; if it keeps failing, check the Internet row on the Settings page and the DNS steps above.

**Stuck on "Stream ended - waiting for YouTube to finish processing"** - after a live stream YouTube can take a while before the recording can be downloaded. The add-on checks every 15 minutes and continues automatically (it gives up after 2 days).

**Ollama shows "Not reachable"** on the Settings page - check `ollama_url`, that `OLLAMA_HOST=0.0.0.0` is set on the Ollama machine, and that the firewall allows port 11434.

**Transcription slow** - choose a smaller `whisper_model`, give the VM more vCPUs, and confirm AVX2 is available.
