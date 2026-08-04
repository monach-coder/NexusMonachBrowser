"""Offline Silero TTS worker used by Nexus Monach test builds.

The browser passes an explicit local ``.pt`` package.  This worker never
discovers models, contacts a registry, or downloads files at runtime.
"""

import argparse
import contextlib
import json
import os
import re
import sys
import wave
from pathlib import Path


SAMPLE_RATE = 48_000
SPEAKER = "kseniya"
MAX_SPEECH_CHUNK = 150
PAUSE_SECONDS = 0.11
STRESS_MARKER = "\ue000"

LATIN_LETTER_NAMES = {
    "A": "эй", "B": "би", "C": "си", "D": "ди", "E": "и", "F": "эф",
    "G": "джи", "H": "эйч", "I": "ай", "J": "джей", "K": "кей",
    "L": "эл", "M": "эм", "N": "эн", "O": "оу", "P": "пи", "Q": "кью",
    "R": "ар", "S": "эс", "T": "ти", "U": "ю", "V": "ви",
    "W": "дабл ю", "X": "икс", "Y": "уай", "Z": "зи",
}
LATIN_TRANSLITERATION = {
    "a": "а", "b": "б", "c": "к", "d": "д", "e": "е", "f": "ф",
    "g": "г", "h": "х", "i": "и", "j": "дж", "k": "к", "l": "л",
    "m": "м", "n": "н", "o": "о", "p": "п", "q": "к", "r": "р",
    "s": "с", "t": "т", "u": "у", "v": "в", "w": "в", "x": "кс",
    "y": "й", "z": "з",
}


def _write_wave(path: str, audio) -> None:
    samples = (
        audio.detach()
        .cpu()
        .clamp(-1.0, 1.0)
        .mul(32_767.0)
        .to(dtype=__import__("torch").int16)
        .numpy()
    )
    with wave.open(path, "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(SAMPLE_RATE)
        output.writeframes(samples.tobytes())


def _normalize_plain_text(text: str) -> str:
    # A plus between Cyrillic letters is Silero's documented manual stress
    # marker. Protect it while ordinary arithmetic plus signs become words.
    text = re.sub(r"(?<=[А-Яа-яЁё])\+(?=[А-Яа-яЁё])", STRESS_MARKER, text)
    replacements = {
        "%": " процентов ", "+": " плюс ", "&": " и ", "@": " собака ",
        "/": " ", "\\": " ", "№": " номер ", "€": " евро ", "$": " долларов ",
    }
    for source, target in replacements.items():
        text = text.replace(source, target)
    text = text.replace(STRESS_MARKER, "+")

    def pronounce_latin(match: re.Match[str]) -> str:
        token = match.group(0)
        if token.isupper() and len(token) <= 8:
            return " ".join(LATIN_LETTER_NAMES[letter] for letter in token)
        return "".join(LATIN_TRANSLITERATION[letter.lower()] for letter in token)

    text = re.sub(r"[A-Za-z]+", pronounce_latin, text)
    text = re.sub(r"[^0-9A-Za-zА-Яа-яЁё+.,!?;:()\-—\s]", " ", text)
    return re.sub(r"\s+", " ", text).strip()


def _split_speech_chunks(text: str) -> list[str]:
    chunks: list[str] = []
    sentences = re.split(r"(?<=[.!?…])\s+", text)
    current = ""
    for sentence in sentences:
        remaining = sentence.strip()
        if not remaining:
            continue
        if len(remaining) <= MAX_SPEECH_CHUNK:
            combined = f"{current} {remaining}".strip()
            if len(combined) <= MAX_SPEECH_CHUNK:
                current = combined
                continue
            if current:
                chunks.append(current)
            current = remaining
            continue
        if current:
            chunks.append(current)
            current = ""
        while len(remaining) > MAX_SPEECH_CHUNK:
            floor = MAX_SPEECH_CHUNK // 2
            window = remaining[:MAX_SPEECH_CHUNK + 1]
            candidates = [window.rfind(mark) for mark in (", ", "; ", ": ", " — ", " ")]
            split_at = max(position for position in candidates if position >= floor) if any(
                position >= floor for position in candidates
            ) else MAX_SPEECH_CHUNK
            part = remaining[:split_at].strip()
            if part:
                chunks.append(part)
            remaining = remaining[split_at:].strip()
        if remaining:
            chunks.append(remaining)
    if current:
        chunks.append(current)
    return chunks


def _synthesize(model, text: str, rate: int, style: str):
    """Synthesize through Silero's reliable plain-text path.

    V5's optional SSML parser corrupts its parser state after some perfectly
    valid multi-word Russian phrases. Live dubbing must not lose the current
    and every following phrase merely to change tempo, so tempo is left at the
    native Kseniya rate and Silero's accentor handles the complete plain text.
    """
    normalized = _normalize_plain_text(text)
    if not normalized:
        raise ValueError("Speech text is empty after Silero normalization")
    common = {
        "speaker": SPEAKER,
        "sample_rate": SAMPLE_RATE,
        "put_accent": True,
        "put_yo": True,
    }
    import torch

    chunks = _split_speech_chunks(normalized)
    audio_parts = []
    for index, chunk in enumerate(chunks):
        audio_parts.append(model.apply_tts(text=chunk, **common))
        if index + 1 < len(chunks):
            audio_parts.append(torch.zeros(int(SAMPLE_RATE * PAUSE_SECONDS)))
    return torch.cat(audio_parts), True


def main() -> int:
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    protocol_output = sys.stdout

    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--stdio", action="store_true")
    args = parser.parse_args()

    model_path = Path(args.model).resolve(strict=True)
    if model_path.suffix.lower() != ".pt":
        raise ValueError("Silero model must be an explicit local .pt package")

    os.environ["HF_HUB_OFFLINE"] = "1"
    os.environ["TRANSFORMERS_OFFLINE"] = "1"
    with contextlib.redirect_stdout(sys.stderr):
        import torch

        torch.set_num_threads(min(6, max(2, os.cpu_count() or 2)))
        model = torch.package.PackageImporter(str(model_path)).load_pickle(
            "tts_models", "model"
        )
        model.to(torch.device("cpu"))

    for line in sys.stdin:
        request = None
        try:
            request = json.loads(line)
            output_path = Path(request["output"]).resolve()
            output_path.parent.mkdir(parents=True, exist_ok=True)
            rate = max(-4, min(4, int(request.get("rate", 0))))
            style = str(request.get("style", "natural")).lower()
            text = str(request["text"]).strip()
            if not text:
                raise ValueError("Speech text is empty")
            with contextlib.redirect_stdout(sys.stderr), torch.inference_mode():
                audio, used_plain_text = _synthesize(model, text, rate, style)
            _write_wave(str(output_path), audio)
            reply = {
                "id": request.get("id"),
                "ok": True,
                "error": "",
                "plain_text_fallback": used_plain_text,
            }
        except Exception as error:  # Worker errors are returned, not leaked.
            detail = str(error).strip()
            reply = {
                "id": request.get("id") if isinstance(request, dict) else None,
                "ok": False,
                "error": (f"{type(error).__name__}: {detail}" if detail else
                          type(error).__name__)[:500],
            }
        print(json.dumps(reply, ensure_ascii=False), file=protocol_output, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
