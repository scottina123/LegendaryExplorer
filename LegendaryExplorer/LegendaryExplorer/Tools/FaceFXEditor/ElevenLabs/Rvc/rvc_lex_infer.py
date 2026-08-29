"""Offline bridge from Legendary Explorer to the self-contained RVC WebUI runtime."""

import inspect
import json
import os
import sys
import traceback
from types import SimpleNamespace


PROTOCOL_PREFIX = "LEX_RVC_RESULT:"
PROGRESS_PREFIX = "LEX_RVC_PROGRESS:"
PROJECT_ROOT = os.getcwd()
if PROJECT_ROOT not in sys.path:
    sys.path.insert(0, PROJECT_ROOT)
os.environ.setdefault("weight_root", os.path.join(PROJECT_ROOT, "assets", "weights"))
os.environ.setdefault("index_root", os.path.join(PROJECT_ROOT, "logs"))
os.environ.setdefault("outside_index_root", os.path.join(PROJECT_ROOT, "assets", "indices"))
os.environ.setdefault("rmvpe_root", os.path.join(PROJECT_ROOT, "assets", "rmvpe"))

_config = None
_converter = None
_loaded_model = None


def send_result(request_id, ok, error=None):
    payload = {"id": request_id, "ok": ok}
    if error:
        payload["error"] = error
    print(PROTOCOL_PREFIX + json.dumps(payload, ensure_ascii=False), flush=True)


def send_progress(request_id, stage):
    payload = {"id": request_id, "stage": stage}
    print(PROGRESS_PREFIX + json.dumps(payload, ensure_ascii=False), flush=True)


def create_config():
    from configs.config import Config

    original_argv = sys.argv[:]
    sys.argv = [sys.argv[0]]
    try:
        return Config()
    finally:
        sys.argv = original_argv


def get_converter_class():
    try:
        from infer.modules.vc.modules import VC
    except ImportError:
        from infer.vc.modules import VC
    return VC


def load_model(model_path):
    global _config, _converter, _loaded_model
    model_path = os.path.abspath(model_path)
    model_name = os.path.basename(model_path)
    if _converter is None:
        _config = create_config()
        _converter = get_converter_class()(_config)
    if _loaded_model != model_path:
        _converter.get_vc(model_name)
        _loaded_model = model_path


def invoke_conversion(request, request_id):
    send_progress(request_id, "Loading the RVC voice model")
    load_model(request["modelPath"])
    send_progress(request_id, "Extracting pitch and converting audio")
    method = _converter.vc_single
    parameters = inspect.signature(method).parameters
    f0_curve_path = request.get("f0CurvePath")
    f0_curve = SimpleNamespace(name=f0_curve_path) if f0_curve_path else None
    index_path = request.get("indexPath") or ""
    values = {
        "sid": int(request["speakerId"]),
        "speaker_id": int(request["speakerId"]),
        "input_audio_path": request["inputPath"],
        "input_audio": request["inputPath"],
        "f0_up_key": int(request["pitch"]),
        "pitch": int(request["pitch"]),
        "f0_file": f0_curve,
        "f0_method": request["f0Method"],
        "file_index": index_path,
        "file_index2": index_path,
        "index_path": index_path,
        "index_rate": float(request["indexRate"]),
        "filter_radius": int(request["filterRadius"]),
        "resample_sr": int(request["resampleSampleRate"]),
        "rms_mix_rate": float(request["rmsMixRate"]),
        "protect": float(request["protect"]),
    }
    kwargs = {name: values[name] for name in parameters if name in values}
    missing = [
        name
        for name, parameter in parameters.items()
        if parameter.default is inspect.Parameter.empty and name not in kwargs
    ]
    if missing:
        raise RuntimeError("Unsupported RVC vc_single parameters: " + ", ".join(missing))

    status, result = method(**kwargs)
    if result is None or result[0] is None or result[1] is None:
        raise RuntimeError(str(status or "RVC returned no audio"))

    sample_rate, audio = result
    send_progress(request_id, "Writing the converted WAV")
    output_path = os.path.abspath(request["outputPath"])
    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    from scipy.io import wavfile

    wavfile.write(output_path, int(sample_rate), audio)


def process_request(request):
    request_id = int(request.get("id", 0))
    try:
        if request.get("command") != "infer":
            raise ValueError("Unsupported bridge command")
        invoke_conversion(request, request_id)
        send_result(request_id, True)
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        send_result(request_id, False, str(error))


def main():
    if len(sys.argv) == 3 and sys.argv[1] == "--request-file":
        with open(sys.argv[2], "r", encoding="utf-8-sig") as request_file:
            process_request(json.load(request_file))
        return

    for line in sys.stdin:
        if line.strip():
            process_request(json.loads(line))


if __name__ == "__main__":
    main()
