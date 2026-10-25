"""Local Docker image renderer. No manuscript planning or hosted image API."""
import base64
import gc
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from io import BytesIO
import json
import os
from pathlib import Path
from threading import Lock, Thread

MODEL = "black-forest-labs/FLUX.2-klein-4B"
REVISION = "e7b7dc27f91deacad38e78976d1f2b499d76a294"
LOCK = Lock()
READY = False
DOWNLOAD_ERROR = None


def prepare_model():
    global READY, DOWNLOAD_ERROR
    from huggingface_hub import snapshot_download
    while not READY:
        try:
            print("Preparing local image model (about 16 GB on first use)...", flush=True)
            snapshot_download(MODEL, revision=REVISION, allow_patterns=["model_index.json", "scheduler/*", "text_encoder/*", "tokenizer/*", "transformer/*", "vae/*"], max_workers=2)
            READY, DOWNLOAD_ERROR = True, None
            print("Local chapter image model ready.", flush=True)
        except Exception as error:
            import time
            DOWNLOAD_ERROR = str(error)
            print(f"Image model download paused; retrying in 60 seconds: {error}", flush=True)
            time.sleep(60)


def generate(request):
    if not READY:
        raise RuntimeError("Local image model is still downloading; retry artwork when /health reports ready.")
    import torch
    from diffusers import Flux2KleinPipeline
    from PIL import Image
    if request.get("model") != MODEL or request.get("revision") != REVISION:
        raise ValueError("Renderer requires the installed, pinned FLUX.2 klein 4B model/revision.")
    size = request.get("size", 768)
    if type(size) is not int or not 256 <= size <= 1024 or size % 64:
        raise ValueError("Image size must be a multiple of 64 between 256 and 1024.")
    prompt = request.get("prompt")
    if not isinstance(prompt, str) or not 8 <= len(prompt) <= 12000:
        raise ValueError("Invalid image prompt.")
    encoded = request.get("images", [])
    if not isinstance(encoded, list) or len(encoded) > 4:
        raise ValueError("At most four reference images are supported.")
    images = []
    for value in encoded:
        contents = base64.b64decode(value, validate=True)
        with Image.open(BytesIO(contents)) as image:
            if image.width * image.height > 4_194_304:
                raise ValueError("Reference image is too large.")
            images.append(image.convert("RGB"))
    pipe = None
    try:
        # Sequential offload keeps this usable on the 8 GB narration GPU.
        pipe = Flux2KleinPipeline.from_pretrained(MODEL, revision=REVISION, torch_dtype=torch.bfloat16, local_files_only=True)
        pipe.enable_sequential_cpu_offload()
        pipe.vae.enable_tiling()
        parameters = dict(prompt=prompt, height=size, width=size, guidance_scale=1.0,
                          num_inference_steps=4, generator=torch.Generator("cpu").manual_seed(int(request["seed"])))
        if images:
            parameters["image"] = images
        image = pipe(**parameters).images[0].convert("RGB")
        output = BytesIO()
        image.save(output, format="JPEG", quality=94)
        return dict(image=base64.b64encode(output.getvalue()).decode(),
                    renderer=dict(model=MODEL, revision=REVISION, steps=4, offload="sequential"))
    finally:
        # Do not retain GPU weights between requests or contend with narration.
        del pipe
        gc.collect()
        torch.cuda.empty_cache()


class Handler(BaseHTTPRequestHandler):
    def respond(self, status, value):
        contents = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(contents)))
        self.end_headers()
        self.wfile.write(contents)

    def do_GET(self):
        if self.path == "/health":
            cached = 0
            for path in Path(os.environ.get("HF_HOME", "/models/huggingface")).rglob("*"):
                try:
                    if path.is_file() and not path.is_symlink():
                        cached += path.stat().st_size
                except OSError:
                    pass
            self.respond(200, dict(service="StoryCast illustrations", model=MODEL, revision=REVISION, ready=READY,
                                   downloadError=DOWNLOAD_ERROR, cachedBytes=cached))
        else:
            self.respond(404, dict(error="Unknown endpoint"))

    def do_POST(self):
        if self.path != "/render":
            self.respond(404, dict(error="Unknown endpoint"))
            return
        try:
            size = int(self.headers.get("Content-Length", "0"))
            if not 0 < size <= 60_000_000:
                raise ValueError("Invalid request size.")
            request = json.loads(self.rfile.read(size))
            with LOCK:
                result = generate(request)
            self.respond(200, result)
        except (ValueError, KeyError, TypeError) as error:
            self.respond(400, dict(error=str(error)))
        except Exception as error:
            self.respond(503, dict(error=str(error)))


if __name__ == "__main__":
    Thread(target=prepare_model, daemon=True).start()
    ThreadingHTTPServer(("0.0.0.0", int(os.environ.get("PORT", "7862"))), Handler).serve_forever()
