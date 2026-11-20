# Make the SlimFaas Live Stream films

Run the commands below from this directory. You can copy it outside the SlimFaas repository. It contains the educational master, the narration for all three editions, the recording manifests and the pnpm project.

| Edition | Published film | Rebuild |
| --- | --- | --- |
| Animated house · English | [From eight windows to Versailles (1 min 58 s)](../../docs/media/live-stream/house.mp4) · [Audio only](../../docs/media/live-stream/house.m4a) · [English captions](../../docs/media/live-stream/house.vtt) | `pnpm house` |
| Animated house · French | [De huit fenêtres à Versailles (2 min 13 s)](../../docs/media/live-stream/house-fr.mp4) · [Audio only](../../docs/media/live-stream/house-fr.m4a) · [French captions](../../docs/media/live-stream/house-fr.vtt) | `pnpm house:fr` |
| Marketing | [Zero idle replicas (31 seconds)](../../docs/media/live-stream/marketing.mp4), portrait 4:5, English voice and burned-in captions | `pnpm marketing` |
| Educational | [Dashboard walkthrough](media/dashboard.mp4), 3 min 11 s, widescreen, English voice and selectable captions | `pnpm educational` after capturing three takes |

The [marketing brief](marketing/brief.md) documents the positioning, source for the cost figure and SlimFaas visual identity.

## Install the film tools

Use Node 26, pnpm 12.21.0, Python 3.12+ and FFmpeg with libx264. The educational edit also uses the drawtext and palette filters. Set `FFMPEG` to select a custom encoder executable.

```sh
pnpm install --frozen-lockfile
pnpm browser:install
pnpm test
```

## Rebuild the animated house

Run `pnpm house` for English or `pnpm house:fr` for French. Each command produces an MP4, the identical AAC track in an M4A and matching WebVTT captions in `media/`, using the stems `house` and `house-fr`. Both renders run offline from the same artwork. Each language has its own script, lossless narration, caption timings, on-screen labels and number formats. The chick’s beak follows its voice. In the closing shot, the final spoken cue times the reveal of an igloo Versailles, its 38 lights and the chick’s wink. [English scenes](marketing/house/scenes.json) · [French scenes](marketing/house/fr/scenes.json) · [House](marketing/house/scene.html) · [Architecture](marketing/house/chapters.html) · [Penguin](marketing/house/penguin.html)

The two meters use separate illustrative tariffs. Eight 60 W lamps left on for eight hours use 3.84 kWh and cost €0.96 at €0.25/kWh. At €10 per application pod-hour, eight idle pods cost €80 in the first hour. After scale-to-zero, the second hour adds €0. SlimFaas stays at the door to receive requests. The billing clock pauses while the house grows to 32 pods in the tower and 96 at Versailles, displaying the hourly rate at each scale. Each rocket represents a simulated message. The count rises from one request to 24 at the house, 640 at the tower and 4,800 at the palace.

The closing example applies a 60% reduction to €10,000 per month, leaving €4,000 and saving €6,000. Holding that monthly saving for twelve months frees €72,000. The opening identifies the €10,000 bill as an illustrative scenario.

## Rebuild the short marketing edit

```sh
pnpm marketing
```

This edit can run offline after setup. It uses the local [educational master](media/dashboard.mp4), [recorded observations](observed.json), [script](marketing/scenes.json) and voice master. Four dashboard excerpts follow an idle API through its first request, a burst and its return to zero. A separate card attributes the reported cost reduction to Guillaume Chervet’s team at Devoxx Belgium 2025. When replacing the recording, review the in/out points and crops in the [editor](marketing/render.py). Titles, artwork and framing live in the [storyboard](marketing/board.html) and [frame renderer](marketing/frames.mjs).

Outputs are `media/marketing.mp4` and `media/marketing.vtt`. The editor checks the entire timeline against a 60-second limit. Audio takes and scratch frames go into `artifacts/marketing/`.

## Rebuild the educational edit

Capture the three takes below, or copy the existing `sync/`, `async/` and `subscriptions/` take directories into the project's `artifacts/`. Earlier checkouts stored them in the repository's `artifacts/live-stream-video/`. Then rebuild the master.

```sh
pnpm educational
```

This regenerates the educational master in `media/`, its poster, silent GIF, English captions, time-coded transcript and the [observation summary](observed.json). To publish a reviewed edition in the SlimFaas repository, copy its media files to `docs/media/live-stream/`. The site and repository README use those published copies.

## Capture on a fresh cluster

Use kind, kubectl and a running Docker engine with about four available CPU cores. For Podman, set `KIND_EXPERIMENTAL_PROVIDER=podman` and use `podman pull` below. The [film fixtures](cluster/) use versioned SlimFaas and Fibonacci container images. Preload the worker image so registry download time is outside the cold-start shot. The recording uses a private kubeconfig, a loopback port forward and the namespace `slimfaas-demo` in `kind-slimfaas-film`.

```sh
mkdir -p artifacts
kind create cluster --name slimfaas-film --kubeconfig "$PWD/artifacts/kubeconfig" --wait 120s
docker pull docker.io/axaguildev/fibonacci-api:0.89.1
kind load docker-image --name slimfaas-film docker.io/axaguildev/fibonacci-api:0.89.1
python3 cluster.py start
kubectl --kubeconfig "$PWD/artifacts/kubeconfig" --context kind-slimfaas-film -n slimfaas-demo rollout status statefulset/slimfaas --timeout=5m
kubectl --kubeconfig "$PWD/artifacts/kubeconfig" --context kind-slimfaas-film -n slimfaas-demo port-forward pod/slimfaas-0 39490:5000
```

Leave the port forward running. In a second terminal in this directory, record the takes.

```sh
pnpm capture sync
pnpm capture async
python3 cluster.py subscribers
pnpm capture subscriptions
```

The recorder checks the following before accepting a take.

- The synchronous HTTP caller receives Fibonacci(10) = 55. The actual deployment goes from zero ready pods to one and back to zero.
- All 30 Fibonacci(48) submissions return 202. The queue grows, the ready count exceeds one, and all 30 results are observed in worker logs before the deployment returns to zero. The film preset targets four ready workers.
- Four distinct pods discard their process-local tool definitions after one broadcast. For the Stream, one pod in each deployment observes the result and both subscription cursors advance. The evaluator and audit fixtures record receipt of an example agent result.

The [observation summary](observed.json) identifies the four broadcast recipients and the two Stream recipients in the published film. Each stage saves timestamped browser screenshots and a `take.json` containing timing and observations under the project’s `artifacts/` directory. An assertion failure prevents publication. Start with a fresh cluster for a new recording, as Stream cursors and the consumers' cached state are part of the demonstration. Stop the port forward before deleting the recording cluster.

```sh
kind delete cluster --name slimfaas-film --kubeconfig "$PWD/artifacts/kubeconfig"
```

## Replace the narration

The French and English house films use Qwen3-TTS 1.7B CustomVoice, generated locally on Apple Silicon with MLX Audio. Ono Anna plays the girl and Vivian plays the chick. Sohee narrates the house in French and Serena narrates it in English. Each sentence has its own acting direction. The narrator picks up speed as the rocket traffic builds, while the chick moves from surprise at the annual saving to a soft appeal and a wry final line. The mix gives the prices extra room and leaves a 0.76-second pause before the final joke. Each scene targets −17 LUFS and −2 dBTP before AAC encoding. Rendering uses the bundled recordings. [Voice directions](marketing/house/voice-direction.json) · [Qwen3-TTS](https://huggingface.co/Qwen/Qwen3-TTS-12Hz-1.7B-CustomVoice) · [MLX weights](https://huggingface.co/mlx-community/Qwen3-TTS-12Hz-1.7B-CustomVoice-8bit/tree/41d3337e8b7f2843a75841595fc14e4b9a7a4b96)

Hear the same opening and closing lines with Kokoro first and Qwen second. [French comparison, Qwen at 0:13](../../docs/media/live-stream/house-voice-comparison-fr.m4a) · [English comparison, Qwen at 0:12](../../docs/media/live-stream/house-voice-comparison-en.m4a)

To audition a different reading on Apple Silicon, edit the corresponding cue in the voice directions. With the 8-bit MLX snapshot downloaded locally, this example records the appeal and punchline as two separate WAVs. Omit the cue filters to record the entire language.

```sh
hf download mlx-community/Qwen3-TTS-12Hz-1.7B-CustomVoice-8bit --revision 41d3337e8b7f2843a75841595fc14e4b9a7a4b96 --local-dir artifacts/qwen-model
uv run --no-project --python 3.12 --with mlx-audio==0.6.7 --with soundfile==0.14.0 python marketing/house/record_qwen.py fr --model-dir artifacts/qwen-model --output artifacts/qwen-auditions/fr --cue year-2 --cue year-3
```

When all the WAVs for a language are ready, the mixer writes the lossless master and the sample count of each caption. Run the film command to rebuild the animation, captions and final audio together.

```sh
uv run --no-project --python 3.12 --with numpy --with soundfile==0.14.0 python marketing/house/mix_qwen.py fr --takes artifacts/qwen-auditions/fr
pnpm house:fr
```

The educational and dashboard marketing editions use Kokoro `af_heart` at speed 1.055. Each edition includes a lossless mono 24 kHz master. Running the film commands restores the published voices and timings offline. [Kokoro ONNX model](https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0)

```sh
python3 narrate.py
python3 render.py
```

For an alternate Kokoro read, pass a local speech server with `--speech-url http://127.0.0.1:8880/v1/audio/speech`. For this alternative the scene’s `speaker` field selects a Kokoro base voice. Pass the same edition (`marketing`, `house` or `house-fr`) to `narrate.py --mode` and `marketing/render.py`. To record a human narrator, supply one mono 24 kHz 16-bit PCM WAV per scene key in the edition's `voice/` directory and run its editor directly. The `film.py` commands restore the bundled takes.

For the educational edition, set `FILM_FONT` and `FILM_FONT_BOLD` to local TrueType paths if Arial or DejaVu Sans is unavailable. The editor renders H.264/AAC with fast start and labels compressed waits with their elapsed-time multiplier. Durations follow the narration and the observed cluster timing. Review the framing when changing the capture viewport, dashboard layout or workload names.

The synchronous take shows the HTTP proxy waking the deployment to its configured starting count. In the burst, SlimFaas collects Prometheus-format metrics and evaluates the queue-depth trigger locally. The [autoscaling guide](../../docs/autoscaling.md) describes both decisions. The closing examples cover queued and scheduled jobs, scheduled function wake-ups, expiring Sets and streamed Files. The observability chapter revisits the recorded dashboard and describes the optional [OpenTelemetry export](../../docs/opentelemetry.md). The MP4 includes chapter markers and an English subtitle track. Chromium renders the title cards offline.

The opening describes scale-to-zero as a way to reduce idle resource use. The [CNCF directory](https://www.cncf.io/projects/slimfaas/) lists SlimFaas as a Sandbox project.
