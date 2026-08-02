"""Exercise the optional Piper worker with three local voice styles."""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import wave
from pathlib import Path


PHRASE = (
    "Нексус Монах готов. Сегодня второе августа две тысячи двадцать "
    "шестого года. Чем я могу помочь?"
)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--model", type=Path, required=True)
    parser.add_argument("--output-dir", type=Path, required=True)
    parser.add_argument(
        "--worker",
        type=Path,
        default=Path(__file__).parents[1]
        / "src"
        / "NexusMonach"
        / "AI"
        / "adapters"
        / "piper_voice_worker.py",
    )
    args = parser.parse_args()
    model = args.model.resolve(strict=True)
    worker = args.worker.resolve(strict=True)
    args.output_dir.mkdir(parents=True, exist_ok=True)

    worker_command = [sys.executable, str(worker)] if worker.suffix.lower() == ".py" else [str(worker)]
    process = subprocess.Popen(
        [*worker_command, "--model", str(model), "--stdio"],
        stdin=subprocess.PIPE,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    requests = []
    assert process.stdin is not None
    for style in ("natural", "calm", "expressive"):
        output = (args.output_dir / f"nexus-voice-{style}.wav").resolve()
        requests.append((style, output))
        process.stdin.write(
            json.dumps(
                {
                    "id": style,
                    "text": PHRASE,
                    "output": str(output),
                    "style": style,
                    "rate": 0,
                },
                ensure_ascii=False,
            )
            + "\n"
        )
    process.stdin.close()
    stdout = process.stdout.read() if process.stdout is not None else ""
    stderr = process.stderr.read() if process.stderr is not None else ""
    exit_code = process.wait(timeout=120)
    if exit_code != 0:
        raise RuntimeError(f"Piper worker failed ({exit_code}): {stderr[-2000:]}")

    replies = {}
    for line in stdout.splitlines():
        try:
            reply = json.loads(line)
        except json.JSONDecodeError:
            continue
        replies[reply.get("id")] = reply
    for style, output in requests:
        reply = replies.get(style)
        if not reply or not reply.get("ok"):
            raise RuntimeError(f"Piper {style} synthesis failed: {reply}")
        with wave.open(str(output), "rb") as audio:
            if audio.getnframes() < 1000 or audio.getframerate() < 16000:
                raise RuntimeError(f"Piper created an invalid WAV: {output}")
            duration = audio.getnframes() / audio.getframerate()
        print(f"{style}: {output} ({duration:.2f}s)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
