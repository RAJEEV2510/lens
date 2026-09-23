# Lens

Ask your video in plain English.

Drop in camera footage, Lens indexes every object it sees, and you search it by typing questions like
*"when was the first bus seen?"* or *"how many trucks passed in the first 30 seconds?"*. Answers come back
with the matching frames, boxes drawn on.

Built in .NET 8. Object detection runs locally on CPU with YOLOv10 through ONNX Runtime. The question box answers
from the database first, with no model at all, for the everyday questions (when, how many, show me, any, what footage).
Anything else goes to a local open model through Ollama, and to Claude only if you set a key. Nothing needs the cloud.

```
video ──▶ ffmpeg (decode, 2 fps, letterbox) ──▶ YOLOv10 (ONNX Runtime, CPU) ──▶ detections
                                                                                   │
                                                             PostgreSQL / TimescaleDB  or  a JSON file
                                                                                   │
        "show me buses after 6pm" ──▶ planner (no model) ──▶ search / count ──▶ answer + frames
                                    └─ else ──▶ local model via Ollama, or Claude ──▶ same 3 tools
```

## What it does today

- **Index** any file ffmpeg can decode: mp4, mkv, mov, ts, or an RTSP URL. Two frames per second by default.
- **Detect** 80 COCO classes: person, car, truck, bus, motorcycle, bicycle and more. About 13 to 19 frames/s on a laptop CPU.
- **Find faces** with a second model (`models/yolov11n-face.onnx`, or `LENS_FACE_MODEL_PATH`) run on the same frames; they are stored
  as class `face`. Detection only, no recognition. Leave the model out and faces are skipped.
- **Store** detections with a time in seconds and a wall-clock time, so both "at 0:42" and "after 6pm" work.
- **Search** by class, camera, time window, confidence and minimum box size. Consecutive sightings collapse into events with a start and end.
- **Ask** in plain English. Common questions are answered straight from the store in a few milliseconds with no model. The rest go to a
  local open model (Ollama) or Claude, which call `list_videos`, `search_detections` and `count_detections`. The UI shows every call made and who answered.
- **Learn** from use: every question and the query that answered it is logged to `data/ask-log.jsonl`, ready to fine-tune your own small model.
- **Upload** from the browser. Files go into a background indexing queue with live progress.
- **Show** the exact frame for every hit, with the box drawn on it.

## Quick start (Windows, no Docker)

```powershell
git clone https://github.com/RAJEEV2510/lens && cd lens
.\scripts\get-ffmpeg.ps1        # portable ffmpeg into tools/  (~190 MB)
.\scripts\get-models.ps1        # YOLOv10n + YOLOv11n-face ONNX into models/ (~19 MB)

# index a clip into a JSON store
dotnet run --project src/Lens.Indexer -c Release -- "D:\videos\junction.mp4" --camera "junction" --json data\lens.json --start "2026-09-22T18:00:00+05:30"

# optional: a local open model for the harder questions (about 2 GB, runs on CPU)
winget install Ollama.Ollama
ollama pull qwen2.5:3b

# run the API and the page. No API key needed.
dotnet run --project src/Lens.Api -c Release --urls http://localhost:5080
```

Open http://localhost:5080. Indexing prints progress like:

```
probe   : 1600x1200 @ 12 fps, 60.2s
  t=   24.5s  frames=    50  detections=    304  12.3 fps
  t=   49.5s  frames=   100  detections=    760  13.2 fps

done    : video #1, 120 frames, 934 detections in 9.0s (13.4 frames/s)
          car            398
          person         267
          motorcycle     235
          truck          29
          bus            5
```

## Quick start (Docker)

```bash
cp .env.example .env            # optional: ANTHROPIC_API_KEY, or LENS_OLLAMA_URL for a local model
docker compose up --build
```

That starts TimescaleDB (with pgvector) on port 5433 and the API on http://localhost:8080. Upload videos from the page.

## The app

Angular 20 front end in `src/Lens.Web`, built into the API's `wwwroot` so there is one thing to deploy.

| Page | What |
|---|---|
| **Live** | Camera wall: 1 to 16 tiles, WebRTC video with detection boxes drawn live, per-tile fps and inference time, rolling class counts, click to expand, merged detection feed |
| **Cameras** | Add, edit, pause, remove RTSP cameras; optional low-resolution sub-stream for detection; overlay offset per camera |
| **Search** | Filter search over everything indexed, events or raw sightings, frames with boxes |
| **Ask** | The question box: database first, then a local model, then Claude. Shows who answered and every store call |
| **Videos** | Upload recordings, watch indexing progress, browse the library |

```
cd src/Lens.Web && npm install
npx ng build --configuration production     # writes to ../Lens.Api/wwwroot
npx ng serve --proxy-config proxy.conf.json # dev server on :4200 proxying /api and /hubs to :5080
```

### How live video reaches the browser

Browsers cannot play RTSP. Lens uses the same layering as Frigate: a media gateway for playback, its own decoder for detection.

- **WebRTC through MediaMTX.** When a camera is added, Lens registers a path in MediaMTX over its API (`rtspTransport: tcp`, on demand).
  MediaMTX pulls the camera only while someone is watching and serves the browser over WHEP with sub-second latency.
  Enable the API in `mediamtx.yml` (`api: yes`); WebRTC is on by default. Configure with `Lens:MediaMtx:ApiUrl` and `WebRtcUrl`.
- **Analytics view.** A checkbox on the Live page swaps every tile for the exact frames the detector processed, boxes and labels
  drawn on the server (`GET /api/sources/{id}/annotated`, MJPEG at the detector's own rate; `annotated.jpg` for one frame). What you
  see is precisely what was detected, with no playback latency or overlay offset in between. `GET /api/frame?...&annotate=true`
  does the same for any archived or indexed moment, drawing the detections stored for that frame.
- **MJPEG fallback.** If MediaMTX is not reachable, the tile plays `/api/sources/{id}/mjpeg`, an ffmpeg-per-viewer stream at 5 fps from Lens itself.
- **Boxes over live video.** Detections arrive over SignalR with wall-clock times and are drawn on a canvas over the video, delayed by the
  camera's overlay offset to match playback latency. They line up within a few hundred milliseconds, not frame-exact; the archived
  frames in Search are frame-exact. Tune the offset per camera on the Cameras page.

Two things learned the hard way: set `video.muted` as a *property* before `play()` (the template attribute does not satisfy Chrome's
autoplay policy and the tile stays silently black), and pull cameras into MediaMTX over TCP (UDP loses RTP packets on a busy host).

## Live cameras

Add an RTSP, RTMP or HTTP stream on the page (or `POST /api/sources`) and Lens watches it continuously:

- one ffmpeg reader per camera, TCP transport, low-latency flags, killed cleanly when the source stops
- wall-clock timestamps, so "what happened at 6:12 pm" works across cameras
- detections flushed to the store every 3 seconds, a JPEG archived for every second that had detections
- automatic reconnect with exponential backoff (5 s to 60 s) when the stream drops, and a stall watchdog that drops a silent connection after 20 s
- every detection pushed to the browser over SignalR, so the live view and the feed update as it happens

Tested against Happytime RTSP Server serving recorded files as cameras (`rtsp://host/<file>`), and against MediaMTX with ffmpeg publishing:

```
ffmpeg -re -stream_loop -1 -i clip.mp4 -c:v libx264 -preset veryfast -tune zerolatency -g 24 -keyint_min 24 -sc_threshold 0 -an -f rtsp rtsp://localhost:8554/cam1
```

CPU budget: two 1600x1200 cameras at 2 fps cost about 450 ms of inference per frame each on a laptop with MediaMTX and the browser
decoding alongside. For more cameras give Lens the camera's sub-stream for detection, or run ONNX Runtime with the CUDA provider.

The short keyframe interval (`-g 24`) matters: a decoder can only start at a keyframe, and a file with one keyframe every 20 seconds
looks like a dead stream for 20 seconds. Real cameras send one every 1 to 4 seconds. The connect grace period is 60 s for that reason.

A local file path can also be added as a source; it is read at real-time pace and looped, which is handy for testing without hardware.

## Indexer options

| Flag | Default | Meaning |
|---|---|---|
| `--camera NAME` | `default` | Label used for "which camera" questions |
| `--start ISO8601` | file modified time | Wall-clock time the footage begins |
| `--fps N` | `2` | Frames analysed per second of video |
| `--conf 0.35` | `0.35` | Minimum detection confidence |
| `--classes a,b` | all | Keep only these COCO classes |
| `--json PATH` | | Use a JSON file store (no database) |
| `--db CONN` | `LENS_CONNECTION_STRING` | Use PostgreSQL |

## API

| Route | What |
|---|---|
| `GET /api/videos` | Indexed videos with detection counts |
| `GET /api/detections/search?classes=bus&camera=gate-2&fromSeconds=0&toSeconds=30&group=2` | Events, merged within `group` seconds |
| `GET /api/detections/counts?classes=car&bucketMinutes=15` | Totals per class, or per time bucket |
| `GET /api/frame?videoId=1&t=12.5` | JPEG of that moment |
| `POST /api/videos/upload` (multipart: `file`, `camera`, `startedAt`, `fps`) | Queue a video for indexing, returns a job |
| `GET /api/jobs/{id}` | Indexing progress |
| `POST /api/ask` `{ "question": "..." }` | Answer, hits, the store calls made, and `provider` (`local`, `ollama:<model>` or `claude:<model>`) |
| `GET /api/ask/providers` | Which answerers are available: the local model's reachability, whether a Claude key is set, RAG index state |
| `POST /api/rag/index?rebuild=false`, `GET /api/rag/status` | Build the optional RAG vector index in the background; poll progress |

## How the question box works

Three answerers, tried in order. `Lens:Provider` in `appsettings.json` (or `LENS_PROVIDER`) is `auto` by default and can be
forced to `local`, `ollama` or `claude`.

1. **Database first, no model.** `QuestionPlanner` in `Lens.Core/Query` reads the question with a small grammar: object classes and
   their everyday synonyms (people, vehicles, bikes, lorries, autos), an intent (how many, first, last, show, any, what footage,
   summary), a time window in seconds (`in the first 30 seconds`, `between 1:00 and 1:30`, `around 42 seconds`) or wall-clock
   (`after 6pm`, `between 6:15pm and 6:45pm`, `in the evening`, read on the footage's own day and time zone), and a camera or video.
   It runs the same store queries the model tools would, and writes the answer from a template. It answers in a few milliseconds and
   costs nothing. If it sees a concept the detector cannot know (colour, plates, faces, speed, direction) it says so instead of
   guessing. If it cannot parse the question at all, it hands over.
2. **Local open model.** `OllamaAgent` runs the same tool loop against [Ollama](https://ollama.com) on `http://localhost:11434`
   (`Lens:OllamaUrl`) with `qwen2.5:3b` by default (`Lens:OllamaModel`; any model that supports tools works, `llama3.2:3b` and
   `qwen2.5:7b` are good choices if you have the RAM). The footage inventory is put into the prompt up front so a 3B model usually
   answers with a single search call. Nothing leaves the machine. On a laptop CPU expect 10 to 40 seconds per question.
3. **Claude.** Only if `ANTHROPIC_API_KEY` is set. Same tools, same loop, `LensAgent`.

The UI shows which one answered and every store call it made.

### Optional: RAG mode

The Ask page has a switch: **Database first** (the default above) or **RAG (embeddings)**. RAG is classic retrieval-augmented
generation over the detections:

1. **Index.** Every merged event becomes one sentence, for example *"A large bus on camera atcc-junction (samples_atcc_60s.mp4) at
   0:05 into the video, 18:00:05 on Tuesday 22 September 2026, in view for 3 seconds (7 sightings, 83% confidence), in the middle
   centre of the frame."* Each sentence is embedded with a local open embedding model through Ollama (`nomic-embed-text`,
   `Lens:EmbedModel`, 274 MB) and stored in a small file-backed vector index (`data/lens-vectors.jsonl` + `.f32`,
   `Lens:VectorIndexPath`). Click **Build index** on the Ask page or `POST /api/rag/index`; it is incremental, `?rebuild=true`
   starts over. `GET /api/rag/status` reports progress.
2. **Retrieve.** The question is embedded the same way and the closest `Lens:RagTopK` events (default 12) are pulled by cosine
   similarity, optionally restricted to one video first.
3. **Generate.** The local chat model writes an answer from those events only, and says so when they do not contain one. Without a
   chat model the retrieved events are returned as the answer.

When to use which: RAG suits fuzzy questions ("anything unusual near the gate in the evening?"). For exact counts and time
windows the database-first mode is more accurate, because similarity search returns the closest events, not all of them. The
choice is per question (`"mode": "rag"` on `POST /api/ask`) or a server-wide default (`Lens:Provider = rag`). The same
`FileVectorIndex` interface is where a pgvector implementation or CLIP image embeddings per crop would go, which is what makes
"white van" style questions possible; that is not built yet.

### Training your own model

Every question, whoever answered it, is appended to `data/ask-log.jsonl` (`Lens:AskLogPath`, set it empty to disable) with the
store calls and the answer. That log is a fine-tuning set in the making:

```
python scripts/export-training-data.py --provider local     # clean, deterministic rows from the planner
python scripts/export-training-data.py --min-hits 1         # or everything that found something
```

It writes `data/train.jsonl` in the chat-with-tool-calls format that Unsloth, LLaMA-Factory and axolotl accept. A LoRA over
`Qwen2.5-1.5B-Instruct` on a few hundred rows takes under an hour on a free Colab GPU; export the result as GGUF, `ollama create`
it, set `Lens:OllamaModel` to its name, and the question box now runs on your own model.

## Design notes

- **Decode and inference on separate threads.** ffmpeg writes raw RGB frames to a bounded `System.Threading.Channels` channel, ONNX Runtime reads from it. Neither waits on the other; the channel provides backpressure.
- **ffmpeg does the resize and letterbox.** The .NET side only ever sees fixed-size 640x640 buffers, so preprocessing is a single loop with no image library.
- **Sampling uses `fps=2:round=up`, and that is not cosmetic.** The fps filter emits the *last* source frame whose rounded timestamp falls in a slot. With the default rounding, slot 11.0 s gets the frame at 11.24 s, and a later `-ss 11` grab returns 11.0 s, so boxes drift off moving objects by a quarter second. Ceiling rounding makes the sampled frame and the seeked frame the same frame. Found by checksum-matching filter output against decoded input.
- **YOLOv10 has an end-to-end head**, so there is no NMS step. The YOLOv8 format (NMS required) is also supported and detected from the output shape.
- **Binary COPY into Postgres.** Detections stream straight from the detector into `COPY ... FROM STDIN (FORMAT BINARY)`, never buffered in memory.
- **Events, not frames.** Search uses a gaps-and-islands window query so 25 sightings of the same person become one event from 0:00 to 0:11.
- **Schema works on plain Postgres.** TimescaleDB and pgvector are used when present and skipped when not, so the same `init.sql` runs on RDS.

## Benchmarks (laptop CPU, no GPU)

| Clip | Resolution | Sample rate | Frames | Detections | Wall time | Throughput |
|---|---|---|---|---|---|---|
| junction, 60 s | 1600x1200 | 2 fps | 120 | 934 | 9.0 s | 13.4 frames/s |
| gate, 30 s | 1920x1080 | 2 fps | 60 | 121 | 3.1 s | 19.4 frames/s |

Search over the JSON store returns in single-digit milliseconds at this size. Postgres numbers at 1M+ rows are the next thing to measure.

## Roadmap

- [x] **Analytics view per camera.** The exact frames Lens processed with its own boxes drawn server-side, as a toggle on the Live page.
- [ ] **One stream for detection and playback.** Run detection on MediaMTX's path for the camera so both consumers read the same moment
      (with file-backed test servers each RTSP session starts from the beginning, so today the two can be minutes apart).
- [ ] **Latest-frame-wins in the live pipeline.** Drop stale frames when inference falls behind instead of queueing them, so box lag stays bounded.
- [ ] CLIP embeddings per detection crop, pgvector search, so "white van" and "looks like this" work
- [x] Ollama provider so the question box runs offline with a local model
- [x] Database-first planner so common questions never touch a model
- [x] Optional RAG mode: event sentences embedded locally, similarity retrieval, local model writes the answer
- [ ] Fine-tune a 1.5B model on the question log and ship it as the default local model
- [ ] Amazon Bedrock provider (same agent, different client)
- [ ] Eval suite: 50 questions with known answers, published accuracy
- [ ] Live RTSP mode with SignalR push
- [ ] Angular front end replacing the single-page demo

## Tests

```
dotnet test
```

36 unit tests cover the letterbox maths, NMS, both YOLO output formats, search grouping and filters on the JSON store, the frame archive,
the no-model question planner (intents, synonyms, seconds and clock windows, camera scoping, unsupported concepts), the RAG path
(event sentences, the file vector index, incremental builds), and server-side frame annotation.
CI also applies the schema to a real TimescaleDB container and builds the Docker image.

## Licence

MIT
