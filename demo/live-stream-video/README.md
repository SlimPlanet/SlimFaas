# Make the SlimFaas Live Stream films

Both editions use dashboard footage recorded with three SlimFaas nodes in a disposable Kubernetes cluster.

| Edition | Watch | Rebuild from the repository root |
| --- | --- | --- |
| Marketing | [Give your agent tools the night off](../../docs/media/live-stream/marketing.mp4) · 39 seconds, portrait 4:5, English voice and burned-in captions | `python3 demo/live-stream-video/film.py marketing` |
| Educational | [Live Stream dashboard walkthrough](../../docs/media/live-stream/dashboard.mp4) · 3 min 11 s, widescreen, English voice and selectable captions | `python3 demo/live-stream-video/film.py educational` |

### Build the marketing edition

Install Python 3, Node 26 and FFmpeg with libx264. From the repository root, install the dashboard's Chromium once and render the film.

```sh
(cd src/SlimFaas/ClientApp && npm ci && npx playwright install chromium)
python3 demo/live-stream-video/film.py marketing
```

The short cut uses the checked-in educational MP4, the [observed results](observed.json) and its own [script](marketing/scenes.json) and voice master. It can be rebuilt offline after installing the tools. The three dashboard shots show a real HTTP wake-up, four workers handling the burst and the idle deployment returning to zero. When replacing the underlying recording, review the in/out points and crops in [the short-cut editor](marketing/render.py). Titles, artwork and crop placement are in [the storyboard](marketing/board.html) and [frame renderer](marketing/frames.mjs).

The encoder writes the MP4 and [English WebVTT](../../docs/media/live-stream/marketing.vtt) under `docs/media/live-stream/`. It checks the complete timeline, including the final invitation, against a 60-second limit before rendering. Audio takes and temporary frames live under `artifacts/live-stream-video/marketing/`. Set `FFMPEG` to use a custom encoder executable.

### Build the educational edition

Capture the three takes below, then run `python3 demo/live-stream-video/film.py educational`. The [silent GIF](../../docs/media/live-stream/preview.gif) uses four excerpts from the recording. [English captions](../../docs/media/live-stream/captions.vtt) and a [time-coded transcript](../../docs/media/live-stream/transcript.txt) accompany the film.

## Capture on a fresh cluster

Run from the repository root with Docker, kind, kubectl, Node 26, Python 3.12, FFmpeg with libx264, drawtext and palette filters and about four available CPU cores. The Fibonacci computation is recursive and CPU-bound. The recording uses a private kubeconfig, a loopback port forward and the namespace `slimfaas-demo` in `kind-slimfaas-film`. For Podman, use `podman` for builds and set `KIND_EXPERIMENTAL_PROVIDER=podman`.

```sh
WORK="$PWD/artifacts/live-stream-video"
mkdir -p "$WORK/voice"
(cd src/SlimFaas/ClientApp && npm ci && npx playwright install chromium)

docker build -t localhost/slimfaas-film:record .
docker build -t localhost/fibonacci-film:record -f samples/Fibonacci/Dockerfile .
kind create cluster --name slimfaas-film --kubeconfig "$WORK/kubeconfig" --wait 120s
kind load docker-image --name slimfaas-film localhost/slimfaas-film:record localhost/fibonacci-film:record
python3 demo/live-stream-video/cluster.py start
kubectl --kubeconfig "$WORK/kubeconfig" --context kind-slimfaas-film -n slimfaas-demo rollout status statefulset/slimfaas --timeout=5m
kubectl --kubeconfig "$WORK/kubeconfig" --context kind-slimfaas-film -n slimfaas-demo port-forward pod/slimfaas-0 39490:5000
```

Leave the port forward running and execute the takes in a second terminal.

```sh
node demo/live-stream-video/capture.mjs sync
node demo/live-stream-video/capture.mjs async
python3 demo/live-stream-video/cluster.py subscribers
node demo/live-stream-video/capture.mjs subscriptions
```

The recorder checks the following before accepting a take.

- The synchronous HTTP caller receives Fibonacci(10) = 55. The actual deployment goes from zero ready pods to one and back to zero.
- All 30 Fibonacci(48) submissions return 202. The queue grows, the ready count exceeds one, and all 30 results are observed in worker logs before the deployment returns to zero. The film preset targets four ready workers.
- Four distinct pods discard their process-local tool definitions after one broadcast. For the Stream, one pod in each deployment observes the result and both subscription cursors advance. The evaluator and audit fixtures record receipt of an example agent result.

The [observation summary](observed.json) identifies the four broadcast recipients and the two Stream recipients in the published film. Each stage saves timestamped browser screenshots and a `take.json` containing timing and observations under `artifacts/live-stream-video`. An assertion failure prevents publication. Start with a fresh cluster for a new recording, as Stream cursors and the consumers' cached state are part of the demonstration. To retire the recording cluster, stop the port forward and run:

```sh
kind delete cluster --name slimfaas-film --kubeconfig "$PWD/artifacts/live-stream-video/kubeconfig"
```

## Replace the narration

Both films use the English synthetic voice `af_heart` from [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M). Each lossless voice master is included so you can reproduce the edit offline. The generator used the [int8 Kokoro ONNX model](https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0) at speed 1.055.

```sh
python3 demo/live-stream-video/narrate.py
python3 demo/live-stream-video/render.py
```

To synthesize a revised script, pass your local Kokoro server to the first command with `--speech-url http://127.0.0.1:8880/v1/audio/speech`. For the marketing edition, add `--mode marketing` to that command, then run `python3 demo/live-stream-video/marketing/render.py`. To record a human narrator, supply one mono 24 kHz 16-bit PCM WAV per scene key in the edition's `voice/` directory and run its editor directly. The `film.py` commands restore the bundled takes.

For the educational edition, set `FILM_FONT` and `FILM_FONT_BOLD` to local TrueType paths if Arial or DejaVu Sans is unavailable. The editor renders H.264/AAC with fast start and labels compressed waits with their elapsed-time multiplier. Durations follow the narration and the observed cluster timing. Review the framing when changing the capture viewport, dashboard layout or workload names.

The synchronous take shows the HTTP proxy waking the deployment to its configured starting count. In the burst, SlimFaas collects Prometheus-format metrics and evaluates the queue-depth trigger locally. The [autoscaling guide](../../docs/autoscaling.md) describes both decisions. The closing examples cover queued and scheduled jobs, scheduled function wake-ups, expiring Sets and streamed Files. The observability chapter revisits the recorded dashboard and describes the optional [OpenTelemetry export](../../docs/opentelemetry.md). The MP4 includes chapter markers and an English subtitle track. Chromium renders the title cards offline using the dashboard’s existing Playwright installation.

The opening describes scale-to-zero as a way to reduce idle resource use. The [CNCF directory](https://www.cncf.io/projects/slimfaas/) lists SlimFaas as a Sandbox project.
