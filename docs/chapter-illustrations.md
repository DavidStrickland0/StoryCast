# Chapter illustrations

Ollama selects a source-grounded scene and proposes character designs. A separate local Docker service renders images using FLUX.2 klein 4B; no hosted image API or API key is used. The image model and its revision are pinned in `tools/illustration_renderer.py`.

Build/start the service and enable a book:

```powershell
.\start-illustrations.ps1 -Build -Books 'C:\path\to\book'
```

Model files download automatically when the service starts into Docker volume `storycast-illustration-models`. The health endpoint reports `ready: false` during the initial download. Audio exports continue with pending artwork until the model is ready. Use `--wait-for-model` on backfills to wait for this initial setup. The service uses sequential CPU offload for smaller GPUs and releases weights after each image. Its port is published on localhost only. Rendering may be slow on an 8 GB GPU, particularly while narration is running.

Settings live in the book's `production/illustrations.json`: `enabled`, `plannerModel`, `rendererUrl`, `size` (256–1024, multiples of 64), and optional `style`. Defaults use Ollama `qwen3.5:4b`, renderer `http://host.docker.internal:7862`, and 768 pixel square art.

`illustrations/bible.json` freezes each character's visual description and the shared style. Descriptions for unspecified appearance are visual design choices, not manuscript facts. The renderer creates one reusable style reference and reusable character reference sheets and passes those images into every scene depicting that character. Edit the bible to correct a design; content fingerprints create revised references/art without overwriting earlier images. Visual identity is guided by references, not guaranteed by a seed or text prompt. Every generated image is marked as requiring visual review.

Plans include exact source quotations. Scene selection currently uses the first 18,000 characters of each chapter; it does not consume later chapters. At most three characters are illustrated in a scene. Chat participants are excluded by the planning instructions. Saved plans, prompts, references, images, and receipts stay under `illustrations/`.

New chapter exports generate artwork and attach it as an ID3 front cover. Image failures produce `illustrations/pending/chapter-NNN.json` and allow audio export to complete. Retry art independently, including already completed chapters, without regenerating or re-encoding speech:

```powershell
.\illustrate-chapters.ps1 -Book 'C:\path\to\book' -Chapters '14,15'
```

The wrapper runs the tool inside Docker and waits for the initial model download. Add `-Detach` to let the job continue after closing the terminal; Docker prints its container ID, and `docker logs --follow <container-id>` shows progress. Omitting `-Chapters` processes existing final chapter MP3s. The underlying Python tool also supports `--ollama-url` and `--wait-for-model`. Completed illustrations are reused only when their inputs, reference hashes and image checksum match. Existing book covers remain the fallback when chapter art is unavailable. No existing MP3 is changed until its illustration succeeds.
