#!/usr/bin/env python3
"""KnA neural upscaler sidecar.

Decodes a video with FFmpeg (KnA's pre-filters applied), runs a super-resolution model with PyTorch and
writes raw RGB48LE frames to stdout; KnA's encoding FFmpeg reads them as its video input:

    ffmpeg -f rawvideo -pixel_format rgb48le -video_size WxH -framerate F -i pipe:0 ...

Models are loaded with spandrel (Real-ESRGAN, AnimeVideo v3, Real-CUGAN, SwinIR, SPAN, DAT, ... checkpoints,
.pth or .safetensors) or, for .onnx files, with onnxruntime. Devices: CUDA (NVIDIA), ROCm (AMD, through the
CUDA API of torch-rocm), MPS (Apple Silicon), XPU (Intel), CPU.

    python kna_upscale.py --check            prints the available devices and libraries as JSON
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import threading


def log(msg: str) -> None:
    print(f"[kna-ai] {msg}", file=sys.stderr, flush=True)


def check() -> int:
    info: dict = {"python": sys.version.split()[0]}
    try:
        import numpy  # noqa: F401
        info["numpy"] = numpy.__version__
    except ImportError:
        info["numpy"] = None
    try:
        import torch
        info["torch"] = torch.__version__
        info["cuda"] = torch.cuda.is_available()
        info["gpu"] = torch.cuda.get_device_name(0) if torch.cuda.is_available() else None
        info["rocm"] = bool(getattr(torch.version, "hip", None))
        info["mps"] = bool(getattr(torch.backends, "mps", None) and torch.backends.mps.is_available())
        info["xpu"] = bool(getattr(torch, "xpu", None) and torch.xpu.is_available())
    except ImportError:
        info["torch"] = None
    try:
        import spandrel
        info["spandrel"] = spandrel.__version__
    except ImportError:
        info["spandrel"] = None
    try:
        import onnxruntime
        info["onnxruntime"] = onnxruntime.__version__
        info["onnx_providers"] = onnxruntime.get_available_providers()
    except ImportError:
        info["onnxruntime"] = None
    print(json.dumps(info))
    return 0


def pick_device(name: str):
    import torch
    if name in ("cuda", "rocm") or (name == "auto" and torch.cuda.is_available()):
        return torch.device("cuda")
    if name == "mps" or (name == "auto" and getattr(torch.backends, "mps", None) and torch.backends.mps.is_available()):
        return torch.device("mps")
    if name == "xpu" or (name == "auto" and getattr(torch, "xpu", None) and torch.xpu.is_available()):
        return torch.device("xpu")
    return torch.device("cpu")


class TorchModel:
    def __init__(self, path: str, device_name: str, fp16: bool, tile: int):
        import torch
        from spandrel import ModelLoader
        self.torch = torch
        self.device = pick_device(device_name)
        desc = ModelLoader().load_from_file(path)
        self.scale = desc.scale
        self.model = desc.model.eval().to(self.device)
        self.half = fp16 and self.device.type != "cpu" and getattr(desc, "supports_half", True)
        if self.half:
            self.model = self.model.half()
        self.tile = tile
        log(f"model {desc.architecture.name if hasattr(desc, 'architecture') else '?'} x{self.scale} on {self.device}"
            f"{' fp16' if self.half else ''}")

    def __call__(self, rgb):  # rgb: numpy uint16 HxWx3
        torch = self.torch
        with torch.inference_mode():
            t = torch.from_numpy(rgb.astype("float32") / 65535.0).permute(2, 0, 1).unsqueeze(0).to(self.device)
            if self.half:
                t = t.half()
            out = self._tiled(t) if self.tile > 0 else self.model(t)
            out = out.clamp_(0, 1).squeeze(0).permute(1, 2, 0).float().mul_(65535.0).round_()
            return out.to("cpu").numpy().astype("<u2")

    def _tiled(self, t):
        torch = self.torch
        _, c, h, w = t.shape
        s, size, pad = self.scale, self.tile, 16
        out = torch.zeros((1, c, h * s, w * s), dtype=t.dtype, device=t.device)
        for y in range(0, h, size):
            for x in range(0, w, size):
                y0, x0 = max(y - pad, 0), max(x - pad, 0)
                y1, x1 = min(y + size + pad, h), min(x + size + pad, w)
                part = self.model(t[:, :, y0:y1, x0:x1])
                oy, ox = (y - y0) * s, (x - x0) * s
                hh, ww = min(size, h - y) * s, min(size, w - x) * s
                out[:, :, y * s:y * s + hh, x * s:x * s + ww] = part[:, :, oy:oy + hh, ox:ox + ww]
        return out


class OnnxModel:
    def __init__(self, path: str, device_name: str, scale: int):
        import onnxruntime as ort
        wanted = {"cuda": ["CUDAExecutionProvider"], "rocm": ["ROCMExecutionProvider", "MIGraphXExecutionProvider"],
                  "mps": ["CoreMLExecutionProvider"], "cpu": []}.get(device_name)
        available = ort.get_available_providers()
        providers = [p for p in (wanted if wanted is not None else
                                 ["TensorrtExecutionProvider", "CUDAExecutionProvider", "ROCMExecutionProvider",
                                  "DmlExecutionProvider", "CoreMLExecutionProvider"]) if p in available] + ["CPUExecutionProvider"]
        self.session = ort.InferenceSession(path, providers=providers)
        self.input = self.session.get_inputs()[0].name
        self.scale = scale
        log(f"onnx model on {self.session.get_providers()[0]}")

    def __call__(self, rgb):
        import numpy as np
        x = (rgb.astype("float32") / 65535.0).transpose(2, 0, 1)[None]
        y = self.session.run(None, {self.input: x})[0][0]
        return (np.clip(y, 0, 1).transpose(1, 2, 0) * 65535.0).round().astype("<u2")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--check", action="store_true")
    ap.add_argument("--ffmpeg", default="ffmpeg")
    ap.add_argument("--input")
    ap.add_argument("--input-args", default="[]", help="JSON list of FFmpeg options that open the input (discs, rotation)")
    ap.add_argument("--vf", default="", help="FFmpeg filters applied before the model")
    ap.add_argument("--width", type=int)
    ap.add_argument("--height", type=int)
    ap.add_argument("--fps", default="24000/1001")
    ap.add_argument("--matrix", default="bt709", choices=["bt601", "bt709", "bt2020"])
    ap.add_argument("--model")
    ap.add_argument("--scale", type=int, default=4)
    ap.add_argument("--device", default="auto")
    ap.add_argument("--fp16", action="store_true")
    ap.add_argument("--tile", type=int, default=0)
    ap.add_argument("--start", type=float, default=0.0)
    ap.add_argument("--end", type=float)
    ap.add_argument("--cfr", action="store_true", help="force a constant frame rate (variable frame rate sources)")
    a = ap.parse_args()
    if a.check:
        return check()
    if not (a.input and a.width and a.height and a.model):
        ap.error("--input, --width, --height and --model are required")

    import numpy as np

    model = OnnxModel(a.model, a.device, a.scale) if a.model.lower().endswith(".onnx") else TorchModel(a.model, a.device, a.fp16, a.tile)
    if model.scale != a.scale:
        log(f"the model enlarges x{model.scale}, KnA expected x{a.scale}: rename the file with its scale (e.g. 'MyModel_x2.pth')")
        return 2
    matrix = {"bt601": "bt601", "bt709": "bt709", "bt2020": "bt2020"}[a.matrix]
    chain = [f for f in a.vf.split(",") if f] if a.vf else []
    chain += [f"scale=in_color_matrix={matrix}:in_range=tv:flags=accurate_rnd+full_chroma_int", "format=rgb48le"]

    # Input args come from KnA (InputArgs: disc protocols, display rotation…) and end with "-i <url>".
    input_args = json.loads(a.input_args) or ["-i", a.input]
    cmd = [a.ffmpeg, "-hide_banner", "-nostdin", "-v", "error"]
    if a.start > 0:
        cmd += ["-ss", f"{a.start:.3f}"]
    cmd += input_args + ["-map", "0:v:0", "-an", "-sn", "-dn", "-vf", ",".join(chain)]
    # Frame exact by default; --cfr duplicates/drops frames to match --fps (VFR sources).
    cmd += ["-fps_mode", "cfr", "-r", a.fps] if a.cfr else ["-fps_mode", "passthrough"]
    if a.end is not None:
        cmd += ["-t", f"{max(0.0, a.end - a.start):.3f}"]
    cmd += ["-f", "rawvideo", "-"]
    log(" ".join(cmd))
    dec = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0)
    threading.Thread(target=lambda: [log(l.decode(errors='replace').rstrip()) for l in dec.stderr], daemon=True).start()

    frame_bytes = a.width * a.height * 6
    out = sys.stdout.buffer
    n = 0
    try:
        while True:
            buf = dec.stdout.read(frame_bytes)
            while buf and len(buf) < frame_bytes:
                more = dec.stdout.read(frame_bytes - len(buf))
                if not more:
                    break
                buf += more
            if len(buf) < frame_bytes:
                break
            rgb = np.frombuffer(buf, dtype="<u2").reshape(a.height, a.width, 3)
            up = model(rgb)
            out.write(up.tobytes())
            n += 1
            if n % 100 == 0:
                log(f"{n} frames")
    except BrokenPipeError:
        pass  # the encoder stopped reading (segment end, preview, stop)
    finally:
        dec.kill()
    log(f"done: {n} frames")
    return 0 if dec.wait() in (0, -9, 1) or n > 0 else 1


if __name__ == "__main__":
    sys.exit(main())
