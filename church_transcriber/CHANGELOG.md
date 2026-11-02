# Changelog

## 1.1.4
- If the first-run Whisper model download fails (network/DNS), the service now shows "Couldn't download the Whisper model" and retries automatically, instead of a generic "Transcription failed".
- Verified end to end: the add-on's transcription script runs the full faster-whisper pipeline with PyAV 19 (the version that caused the `metadata_errors` crash).

## 1.1.3
- **Fixed "TypeError: open() got an unexpected keyword argument 'metadata_errors'"** during transcription. A new release of the PyAV library broke faster-whisper's audio reader; the add-on now decodes audio itself with ffmpeg, so PyAV updates can't break it again. faster-whisper is pinned to the tested version (1.2.1).
- Whisper uses the already-downloaded model without contacting huggingface.co on every run (one less network dependency).
- Progress shows "Downloading the Whisper model" and "Preparing audio" stages.
- Error explanations now look at the actual error line, so harmless warnings in the log can't cause a misleading message.

## 1.1.2
- **Network problems no longer fail a service.** DNS failures ("Temporary failure in name resolution"), timeouts and YouTube server errors now retry automatically over about two hours, shown as **Retrying** with the next attempt time and a **Try now** button.
- Downloads use IPv4-only lookups (yt-dlp and the Whisper model download), avoiding a common Home Assistant DNS problem.
- yt-dlp waits between retries instead of retrying instantly.
- New **Internet** check on the Settings page and in the add-on log at start-up.
- Streams YouTube marks as "still processing" are tried straight away instead of only waiting; gives up after 2 days. Services whose scheduled start passed long ago are checked hourly.
- DNS troubleshooting steps in the documentation.

## 1.1.1
- **Try again now resumes** from where a failed service stopped (keeps the downloaded audio and finished transcription) - no need to delete and re-add.
- Clear, plain-English error messages for YouTube bot checks, HTTP 403s, private/members-only videos, network problems, Whisper running out of memory, missing CPU features and model download failures, plus a "Show details" box with the full error to copy.
- Mobile/share links (m.youtube.com, youtu.be, ?pp=, ?si=) are cleaned up automatically.
- More resilient downloads (retries and timeouts) so a slow YouTube response can't leave a service stuck.

## 1.1.0
- Automation API (`/api/v1`) secured by an API key: latest or specific service, whole transcript or chosen parts (overview & sermon notes, scriptures, outline, sections by kind or number), any finished translation, JSON / text / Markdown / HTML, length limit, file exports, and queueing new services.
- Home Assistant events when a transcript or translation is ready (or fails), carrying the parts you choose. Optional webhook for Node-RED/n8n.
- New API page with a URL/YAML builder, live preview and ready-to-paste automation examples.
- "Use in an automation" shortcut in the editor.

## 1.0.0
- First release: YouTube download, local Whisper transcription, church-service sections (chapters / Ollama / built-in), sermon notes, scripture detection, rich-text editor with audio sync, Ollama translation, scheduled & live streams, channel watching, exports (Word, PDF, Markdown, text, HTML, SRT).
