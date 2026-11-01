# Changelog

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
