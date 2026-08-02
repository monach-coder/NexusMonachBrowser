"""Build-time source for the offline Nexus Vosk TTS worker.

The official voice pack turns this module into nexus-voice-worker.exe. Runtime
communication is UTF-8 JSON Lines; no network API or model download is used.
"""

import argparse
import contextlib
import inspect
import json
import sys


def main() -> int:
    protocol_output = sys.stdout
    # stdout is a strict JSON Lines protocol. Third-party import/model banners
    # and diagnostics are redirected so the browser can never parse them as a
    # synthesis reply.
    with contextlib.redirect_stdout(sys.stderr):
        from vosk_tts import Model, Synth

    parser = argparse.ArgumentParser()
    parser.add_argument("--model", required=True)
    parser.add_argument("--stdio", action="store_true")
    args = parser.parse_args()
    with contextlib.redirect_stdout(sys.stderr):
        # model_path is mandatory here: model_name would make upstream Vosk
        # search user caches and download a missing model from the network.
        model = Model(model_path=args.model)
        synth = Synth(model)

    for line in sys.stdin:
        request = None
        try:
            request = json.loads(line)
            options = {"speaker_id": int(request.get("speaker", 1))}
            rate = max(-4, min(4, int(request.get("rate", 0))))
            parameters = inspect.signature(synth.synth).parameters
            if "speech_rate" in parameters:
                options["speech_rate"] = max(0.72, min(1.38, 1.0 + rate * 0.07))
            elif "length_scale" in parameters:
                options["length_scale"] = max(0.72, min(1.38, 1.0 - rate * 0.07))
            with contextlib.redirect_stdout(sys.stderr):
                synth.synth(request["text"], request["output"], **options)
            reply = {"id": request.get("id"), "ok": True, "error": ""}
        except Exception as error:  # Worker errors are returned, not leaked.
            reply = {"id": request.get("id") if isinstance(request, dict) else None,
                     "ok": False, "error": str(error)[:500]}
        print(json.dumps(reply, ensure_ascii=False), file=protocol_output, flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
