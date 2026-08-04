"""Build-time source for the optional offline Nexus Piper HD voice worker.

The worker accepts only an explicit local ONNX path. It never discovers or
downloads voices, so installing a compatible voice pack is an intentional,
auditable operation performed outside the running browser.
"""

import argparse
import contextlib
import json
import os
import sys
import wave


STYLE_CONFIG = {
    "calm": (0.55, 0.65, 1.06),
    "expressive": (0.80, 0.92, 0.94),
    "natural": (0.667, 0.80, 1.00),
}


def main() -> int:
    # PyInstaller inherits the Windows ANSI code page for redirected pipes.
    # The browser protocol is always UTF-8, including Russian request text.
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="replace")
    protocol_output = sys.stdout
    with contextlib.redirect_stdout(sys.stderr):
        from piper import PiperVoice, SynthesisConfig

    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--stdio", action="store_true")
    args = parser.parse_args()

    model_path = os.path.abspath(args.model)
    config_path = model_path + ".json"
    if not os.path.isfile(model_path) or not os.path.isfile(config_path):
        raise FileNotFoundError("Local Piper model or adjacent JSON config is missing.")
    with contextlib.redirect_stdout(sys.stderr):
        voice = PiperVoice.load(model_path, config_path=config_path, use_cuda=False)

    for line in sys.stdin:
        request = None
        try:
            request = json.loads(line)
            text = str(request["text"]).strip()
            output = os.path.abspath(str(request["output"]))
            if not 1 <= len(text) <= 500:
                raise ValueError("Speech text must contain from 1 to 500 characters.")
            if os.path.splitext(output)[1].lower() != ".wav":
                raise ValueError("Piper output must be a WAV file.")

            style = str(request.get("style", "natural")).lower()
            noise_scale, noise_width, base_length = STYLE_CONFIG.get(
                style, STYLE_CONFIG["natural"])
            rate = max(-4, min(4, int(request.get("rate", 0))))
            speed = max(0.72, min(1.38, 1.0 + rate * 0.07))
            synthesis = SynthesisConfig(
                noise_scale=noise_scale,
                noise_w_scale=noise_width,
                length_scale=base_length / speed,
                normalize_audio=True,
            )
            with contextlib.redirect_stdout(sys.stderr):
                with wave.open(output, "wb") as wav_file:
                    voice.synthesize_wav(text, wav_file, syn_config=synthesis)
            reply = {"id": request.get("id"), "ok": True, "error": ""}
        except Exception as error:  # Worker errors are returned, not leaked.
            reply = {"id": request.get("id") if isinstance(request, dict) else None,
                     "ok": False, "error": str(error)[:500]}
        print(json.dumps(reply, ensure_ascii=False), file=protocol_output, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
