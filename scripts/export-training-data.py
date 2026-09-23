"""Turn Lens's question log into a fine-tuning dataset for your own small model.

Every question asked in the Ask box is appended to data/ask-log.jsonl together with the store calls that answered it,
whoever answered (the no-model planner, a local Ollama model, or Claude). This script rewrites those rows into the
chat format most open fine-tuning tools accept (Unsloth, LLaMA-Factory, axolotl, Ollama's own `ollama create`
with a Modelfile after conversion): a system prompt, the user question, and an assistant turn holding the tool calls.

    python scripts/export-training-data.py                  # data/ask-log.jsonl -> data/train.jsonl
    python scripts/export-training-data.py --min-hits 1     # keep only questions that found something
    python scripts/export-training-data.py --provider local # only rows the planner answered (clean, deterministic)

Review the output before training: delete rows whose answer was wrong. A few hundred good rows are enough to teach a
1.5B to 3B model this tool vocabulary with a LoRA on a free Colab GPU.
"""
import argparse
import json
import sys
from pathlib import Path

SYSTEM = (
    "You are Lens, an assistant that answers questions about indexed CCTV footage by calling tools. "
    "Tools: list_videos(), search_detections(video_id?, camera?, classes?, from_seconds?, to_seconds?, from_time?, to_time?, "
    "min_confidence?, min_area?, group_window_seconds?, limit?), count_detections(same filters, bucket_minutes?). "
    "Classes are COCO names (person, car, truck, bus, motorcycle, bicycle, ...). Use from_time/to_time for clock times and "
    "from_seconds/to_seconds for positions in the video. Call a tool, then answer in two or three short sentences."
)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log", default="data/ask-log.jsonl")
    ap.add_argument("--out", default="data/train.jsonl")
    ap.add_argument("--min-hits", type=int, default=0, help="drop rows with fewer matching frames than this")
    ap.add_argument("--provider", default=None, help="keep only rows answered by this provider prefix, e.g. local or ollama")
    args = ap.parse_args()

    src = Path(args.log)
    if not src.exists():
        print(f"no log at {src}; ask a few questions in the app first", file=sys.stderr)
        return 1

    kept = skipped = 0
    with src.open(encoding="utf-8") as f, Path(args.out).open("w", encoding="utf-8") as out:
        for line in f:
            line = line.strip()
            if not line:
                continue
            row = json.loads(line)
            calls = [c for c in row.get("toolCalls", []) if not (isinstance(c.get("input"), dict) and c["input"].get("prefetched"))]
            if not calls or row.get("stopReason") in ("unhandled", "max_rounds", "max_tokens"):
                skipped += 1
                continue
            if row.get("hits", 0) < args.min_hits or (args.provider and not row.get("provider", "").startswith(args.provider)):
                skipped += 1
                continue
            example = {
                "messages": [
                    {"role": "system", "content": SYSTEM},
                    {"role": "user", "content": row["question"]},
                    {
                        "role": "assistant",
                        "content": "",
                        "tool_calls": [{"type": "function", "function": {"name": c["tool"], "arguments": c.get("input") or {}}} for c in calls],
                    },
                    {"role": "assistant", "content": row.get("answer", "")},
                ],
                "meta": {"provider": row.get("provider"), "at": row.get("at")},
            }
            out.write(json.dumps(example, ensure_ascii=False) + "\n")
            kept += 1

    print(f"wrote {kept} examples to {args.out} ({skipped} skipped)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
