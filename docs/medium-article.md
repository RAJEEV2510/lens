# Ask Your Cameras in Plain English: Building Lens, a Local Video Search Engine in .NET

*How I went from a Claude tool-use agent to a database-first planner, a local open model, an optional RAG mode, and server-drawn detection boxes. All on a laptop CPU, nothing sent to the cloud.*

![Ask page: "show me persons" answered from the database in 0.1 s, with the matching frames](https://raw.githubusercontent.com/RAJEEV2510/lens/main/docs/images/search-results.png)

Every CCTV deployment I have seen has the same problem. Hours of footage, one question, and a person scrubbing a timeline at 8x speed hoping to catch the moment. Something happened at the gate last evening. Was there a bus at the junction before 6pm? How many trucks came through in the first half hour?

The footage already contains the answers. Nobody can get at them.

Lens is my attempt to fix that for a small setup: a few cameras, a laptop, no GPU, no cloud bill. You drop in recordings or point it at RTSP cameras, it indexes every object it sees, and you type a question. It answers with a sentence and the exact frames, boxes drawn on.

This article is the story of building it, and in particular the part I got wrong first: how the question box should work. I started with a large language model doing everything. I ended with a system where most questions never touch a model at all, a small open model handles the rest locally, and RAG is an optional switch rather than the foundation. The reasons are worth spelling out, because I think the same reasoning applies to most "chat with your data" projects.

The code is on GitHub at github.com/RAJEEV2510/lens. It is .NET 8, Angular, ONNX Runtime and ffmpeg.

## What it does

- **Index** any file ffmpeg can decode, or an RTSP camera. Two frames per second by default.
- **Detect** 80 COCO classes with YOLOv10, plus faces with a second small model. About 13 to 19 frames per second on a laptop CPU.
- **Store** every detection with its position in the video and its wall-clock time, so both "at 0:42" and "after 6pm" work.
- **Search** by class, camera, time window and confidence. Consecutive sightings of the same class merge into events with a start and end, so 25 frames of one bus become one bus.
- **Ask** in plain English. The answer names the video, the time into it, the clock time, and shows the frames.
- **Watch** live cameras with detection boxes, and switch to a view where the boxes are drawn on the server so they cannot drift.

Here is the architecture in one picture:

```
video ──▶ ffmpeg (decode, 2 fps, letterbox) ──▶ YOLOv10 (ONNX Runtime, CPU) ──▶ detections
                                                                                   │
                                                             PostgreSQL / TimescaleDB  or  a JSON file
                                                                                   │
        "show me buses after 6pm" ──▶ planner (no model) ──▶ search / count ──▶ answer + frames
                                    └─ else ──▶ local model via Ollama, or Claude ──▶ same 3 tools
```

## Indexing: the boring part that has to be right

The pipeline is deliberately simple. ffmpeg decodes the video, drops it to two frames per second, resizes with the aspect ratio preserved and pads to the model's square input. It writes raw RGB to a pipe. The .NET side reads fixed-size buffers, runs them through ONNX Runtime, and writes detections to the store in batches. Decode and inference run on separate threads with a bounded channel between them, so neither waits on the other and memory stays flat.

Two details cost me real time and are worth passing on.

**The frame you detect on must be the frame you show.** ffmpeg's fps filter, with default rounding, emits the last source frame whose rounded timestamp falls in a slot. For 25 fps input, slot 11.0 s gets the frame at 11.24 s. When you later seek to 11.0 s to show the user that frame, you get a different picture, and every box on a moving object is a quarter second off. Using `fps=2:round=up` makes the sampled frame and the seeked frame the same frame. I found this by checksum-matching the filter's output against the decoded input, and it explained a class of "the boxes are slightly wrong" complaints in one go.

**Events, not frames.** A person walking across the frame for eleven seconds is 22 detections at 2 fps. If search returns 22 rows, every count is wrong by a factor of twenty and the results page is a wall of near-identical thumbnails. Lens merges consecutive sightings of the same class within a two-second window into one event with a start, an end, a sighting count, and the best frame. In Postgres this is a gaps-and-islands window query; in the JSON store it is a loop. Every "how many" question in the system answers with events and says so.

![An indexed frame with the stored detections drawn on the server: nine objects, the clock time and the position in the video in the caption](https://raw.githubusercontent.com/RAJEEV2510/lens/main/docs/images/frame-annotated.jpg)

The store is pluggable. For one camera and a demo, a JSON file is enough. For anything real it is TimescaleDB, because a camera at 2 fps with eight objects per frame writes about 1.4 million rows a day and you want time partitioning, compression and a retention policy that drops raw rows after 90 days while keeping hourly rollups.

## The question box, version one: an LLM with tools

The first version of the question box was the obvious one. The question goes to Claude with three tools: list the videos, search detections with a filter, count detections. Claude decides which tools to call, reads the results, writes a short answer. A manual loop echoes every tool call and result back into the conversation, at most eight rounds, and the UI shows the whole trace so a reviewer can see how the answer was reached.

It worked well. Claude is good at turning "when was the first bus seen at the junction after 6pm?" into a search with the right class, camera and clock window, and it writes a clean answer. The trace made it debuggable. The system prompt told it what the detector cannot know, colours, plates, faces, direction, so it said so instead of guessing.

Then I looked at the questions people actually typed, and at the bill and the latency for each of them.

- what footage do you have?
- how many trucks in the first 30 seconds?
- when was the first bus?
- show me people after 6pm
- any bicycles at the junction?

Every one of those is a filter. Class, optional camera, optional time window, one of five intents. Sending them through a frontier model, paying for a few thousand input tokens, waiting several seconds, and getting back a tool call I could have written from a regular expression felt wrong. It is wrong. The model was doing string parsing.

## Version two: the database answers first

So I wrote the parser. `QuestionPlanner` lives in the core library and has no model dependency. It reads the question with a small grammar:

- **Classes and synonyms.** "people" and "pedestrians" are person. "Vehicles" and "traffic" are car, truck, bus and motorcycle. "Bike" is motorcycle and bicycle. "Auto" and "rickshaw" are truck and motorcycle, with a note in the answer explaining why, because YOLO has never met a three-wheeler.
- **Intent.** How many, first, last, show, is there any, what footage, summary.
- **Time in the video.** "In the first 30 seconds", "between 1:00 and 1:30", "around 42 seconds", "after 2 minutes".
- **Wall clock.** "After 6pm", "between 6:15pm and 6:45pm", "in the evening". Resolved on the day the footage starts, in the footage's own time zone, which matters when one camera is on UTC and another on IST.
- **Scope.** "Video 2", or a camera name matched by its tokens, so "the gate camera" finds `anpr-gate-live`.

It runs the same store queries the model tools would and writes the answer from a template. The important design choices:

**It refuses honestly.** If the question mentions colour, plates, faces, speed or direction, it says the detector does not know that and suggests what it can answer. No model, no guessing.

**It hands over when it should.** If it cannot find a class or an intent, it returns nothing and the next answerer takes the question. "Why is this junction so busy?" is not a filter.

**It logs like a model.** Every planned call is recorded in the same tool-call vocabulary the model uses, so the UI's "how the answer was reached" panel works identically and, as it turned out later, the log doubles as training data.

The result is that the everyday questions answer in about 20 to 100 milliseconds, cost nothing, and are exactly right, because a count from a database is a count.

## Version three: the rest runs locally

For the questions the planner cannot parse I wanted a model, but not a cloud one. The user of a camera system does not want per-question billing, and they usually do not want footage descriptions leaving the building.

Ollama made this easy. It serves open models with an OpenAI-style chat API that supports tool calling. I pulled Qwen 2.5 3B, about 1.9 GB, and pointed the same three tools at it. The loop is the same as the Claude one. Small models sometimes write the tool call as text instead of using the tool channel, so there is a rescue path that parses a JSON blob out of the content. Numbers arrive as strings sometimes, classes arrive as "bus,car" sometimes; the argument parser tolerates all of it.

The first run was sobering: 287 seconds for one question. The answer was fine. The time was almost all prompt processing. On this CPU a 3B model reads about 25 to 50 tokens per second, and I was sending it nearly 10,000 tokens: the footage inventory as verbose JSON, and an enum of all 80 class names repeated in two tool schemas.

Three changes brought it to under 7 seconds:

| Change | Effect |
|---|---|
| Inventory as one compact line per video instead of JSON | about 5x fewer tokens |
| Drop the 80-name enum from the tool schemas, describe common classes in a sentence | about 600 tokens saved per question |
| `keep_alive` so the model stays loaded between questions | no 10-second reload |

The lesson generalises. With a local model on a CPU, every prompt token is a fraction of a second. Prompt engineering stops being about phrasing and becomes about byte count.

The answer order is now: database first, then the local model, then Claude only if a key is configured. The Ask page tells you which one answered and what it cost.

## RAG, as an option, and why it is not the default

Everyone asks whether this is RAG. For a long time the honest answer was no, and I want to explain why, because the reasoning is the most useful part of this project.

Retrieval-augmented generation embeds your data, embeds the question, retrieves the closest chunks by similarity, and has a model write from them. It is the right tool when the data is unstructured text and the question is fuzzy.

Detections are not unstructured. They are rows: class, camera, timestamp, confidence, box. The questions people ask are filters over those rows. A vector index cannot answer "how many trucks after 6pm" correctly. It returns the twelve rows whose descriptions are nearest to that sentence, not all the trucks after 6pm. Counts come out wrong and time windows are unreliable. The database-first path is not a compromise for lack of a model; it is the better retrieval for this data.

I built RAG anyway, as a switch, because there is a kind of question it does suit: the fuzzy one. "Anything unusual near the gate in the evening?" has no filter. So:

1. **Index.** Every merged event becomes one sentence: *"A large bus on camera atcc-junction (samples_atcc_60s.mp4) at 0:05 into the video, 18:00:05 on Tuesday 22 September 2026, in view for 3 seconds (7 sightings, 83% confidence), in the middle centre of the frame."* Each sentence is embedded with a local embedding model through Ollama, nomic-embed-text, 274 MB, and stored in a small file-backed vector index. Building it for a thousand events takes a couple of minutes on the CPU and is incremental.
2. **Retrieve.** The question is embedded the same way and the closest twelve events are pulled by cosine similarity, optionally restricted to one video first, which is how real systems combine both: filter, then rank.
3. **Generate.** The local chat model writes from those events only. The system prompt makes it say "among the retrieved events" rather than presenting counts as totals, and to say so when the events do not contain an answer.

On the Ask page there is a dropdown: Database first, or RAG. The choice is remembered. In RAG mode the question "anything unusual near the gate in the evening?" came back with a bicycle spotted repeatedly at four times, which was true and which no filter would have surfaced. The question "hi" came back with a description of an arbitrary person, because RAG always answers from whatever is nearest. That is the trade, and the switch lets the user pick the side.

One honest limit: the sentences describe what the detector knows, so RAG here cannot see colours or appearance either. "White van" needs an image embedding per detection crop, CLIP or similar, in the same index. The interface is there; that part is not built.

## Live cameras, and the boxes that would not line up

Live view was the feature I expected to be easy and was not.

The camera streams to MediaMTX, which serves the browser over WebRTC with sub-second latency. Separately, Lens pulls the same camera, samples it at 2 fps, runs detection, and pushes each detection set to the browser over SignalR. The browser draws the boxes on a canvas over the video, delayed by a per-camera offset to compensate for playback latency.

It looks like this when it works, and like this when it does not:

![WebRTC video with client-drawn boxes: the boxes belong to a moment the picture has already left](https://raw.githubusercontent.com/RAJEEV2510/lens/main/docs/images/live-webrtc-overlay.jpg)

The gate camera on the left is showing a truck; the boxes are for two people and a suitcase from a second earlier. The problem is structural. Two independent decoders read the same camera. With a real camera they are close; with a file-backed test server each RTSP session starts from the beginning, so they can be minutes apart. Even when close, the offset drifts, and a fixed delay cannot follow it. Tuning the offset per camera is guesswork dressed up as a setting.

The fix is to stop reconciling two streams and draw on the one the detector actually saw. Every frame the pipeline processes is now handed to an observer along with its detections. The server draws the boxes and labels onto that frame with ImageSharp, using the same colours as the UI, adds a caption with the camera, the clock time and the frame index, and serves it: one frame on request, or an MJPEG stream at the detector's own rate. The Live page has an "Analytics view" checkbox that switches every tile to it.

![The same two cameras in Analytics view: every box is on the object it belongs to, because the picture is the detector's own frame](https://raw.githubusercontent.com/RAJEEV2510/lens/main/docs/images/live-analytics-view.jpg)

![Gate camera, server-annotated: car 91%, person 73%, motorcycle 52%, and the caption says exactly which frame this is](https://raw.githubusercontent.com/RAJEEV2510/lens/main/docs/images/live-gate-annotated.jpg)

What you see is precisely what was detected. There is no overlay timing, no offset, and nothing to tune. The same annotation runs on archived and indexed frames, so a search result can show the stored detections drawn on the stored frame.

The next step, which I am building now, is to make this the playback stream itself: one decode per camera on the server, boxes drawn on every frame at playback rate, streamed back to the client. That removes the second decoder entirely and makes the low-latency view and the correct view the same view.

## Training your own model from the log

Every question, whoever answered it, is appended to a JSONL log with the store calls that answered it and the answer. The planner's rows are clean and deterministic: question in, exact tool call out. That is a fine-tuning set in the making.

A script rewrites the log into the chat-with-tool-calls format that Unsloth, LLaMA-Factory and axolotl accept. A few hundred good rows are enough to teach a 1.5B model this tool vocabulary with a LoRA on a free Colab GPU. Export the result as GGUF, create it in Ollama, set it as the local model, and the question box runs on a model trained on your own users' questions. The laptop's 2 GB GPU cannot train it, but it does not need to.

I like this loop because it inverts the usual dependency. The deterministic path does not need the model; the model is trained from the deterministic path's output; and the model handles only what the deterministic path could not, which keeps shrinking.

## Numbers

Measured on a Core i7-1165G7 laptop, 16 GB RAM, no usable GPU.

| Task | Result |
|---|---|
| Indexing, 1600x1200 at 2 fps, YOLOv10n | 13 to 19 frames per second |
| Two live cameras, 1280x720, objects and faces | about 300 to 600 ms inference per frame each |
| "When was the first bus seen?" via the planner | 47 ms, no tokens |
| "Why is the junction so busy?" via Qwen 2.5 3B | 6.7 s after the prompt shrink, 287 s before |
| RAG question, 12 events retrieved, Qwen writes | 30 to 70 s, almost all model reading time |
| RAG index build, 1002 events, nomic-embed-text | about 2 minutes, incremental afterwards |
| Unit tests | 36, covering letterbox maths, NMS, both YOLO formats, search grouping, the planner, RAG, annotation |

The RAG and local-model times are what a CPU costs you. A modest GPU takes both to a second or two. The planner does not care.

## What I would tell someone starting this

**Look at the questions before choosing the retrieval.** If they are filters, use a database. If they are fuzzy, use embeddings. If you do not know, log them for a week. The worst outcome is a model doing string parsing at a dollar a thousand.

**Make the model the fallback, not the foundation.** The deterministic path is faster, cheaper, correct, and testable. Let the model handle the tail, and use the log to shrink the tail.

**Local models are viable if you respect the token budget.** A 3B model on a CPU is fine for a tool call if the prompt is a thousand tokens and hopeless if it is ten thousand. Count bytes.

**Draw on the frame you detected on.** Any pipeline that detects on one decode and displays another will drift. Annotate on the server, or make the two the same decode.

**Say what you do not know.** The detector cannot see colours. The best answer to "what colour was the car" is a sentence saying so and suggesting a question it can answer.

Lens is at github.com/RAJEEV2510/lens. It runs on Windows without Docker in three commands, or with Docker Compose alongside TimescaleDB. If you try it on your own footage, I would like to hear what questions people typed, because that list is the whole roadmap.
