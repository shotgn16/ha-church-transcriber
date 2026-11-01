#!/bin/sh
set -e
mkdir -p /data/transcripts /data/media /data/models
echo "[church_transcriber] Starting web app on port 8099"
exec dotnet /app/ChurchTranscriber.dll
