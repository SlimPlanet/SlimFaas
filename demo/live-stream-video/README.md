# Record the Live Stream dashboard

The [finished MP4](../../docs/media/live-stream/dashboard.mp4) shows the real dashboard connected to three SlimFaas nodes in a disposable Kubernetes cluster. The [silent preview](../../docs/media/live-stream/preview.gif) uses four excerpts from the same recording. [English captions](../../docs/media/live-stream/captions.vtt) and a [time-coded transcript](../../docs/media/live-stream/transcript.txt) accompany the film.

## Capture on a fresh cluster

Run from the repository root with Docker, kind, kubectl, Node 26, Python 3.12, FFmpeg with libx264 and about four available CPU cores. The Fibonacci computation is recursive and CPU-bound. The recording uses a private kubeconfig, a loopback port forward and the namespace `slimfaas-demo` in `kind-slimfaas-film`. For Podman, use `podman` for builds and set `KIND_EXPERIMENTAL_PROVIDER=podman`.

```sh
WORK="$PWD/artifacts/live-stream-video"
mkdir -p "$WORK/voice"
python3.12 -m venv "$WORK/venv"
. "$WORK/venv/bin/activate"
pip install -r demo/live-stream-video/requirements.txt
(cd src/SlimFaas/ClientApp && npm ci && npx playwright install chromium)

docker build -t localhost/slimfaas-film:record .
docker build -t localhost/fibonacci-film:record -f samples/Fibonacci/Dockerfile .
kind create cluster --name slimfaas-film --kubeconfig "$WORK/kubeconfig" --wait 120s
kind load docker-image --name slimfaas-film localhost/slimfaas-film:record localhost/fibonacci-film:record
python demo/live-stream-video/cluster.py start
kubectl --kubeconfig "$WORK/kubeconfig" --context kind-slimfaas-film -n slimfaas-demo rollout status statefulset/slimfaas --timeout=5m
kubectl --kubeconfig "$WORK/kubeconfig" --context kind-slimfaas-film -n slimfaas-demo port-forward pod/slimfaas-0 39490:5000
```

Leave the port forward running and execute the takes in a second terminal. Activate the same virtual environment there.

```sh
. artifacts/live-stream-video/venv/bin/activate
node demo/live-stream-video/capture.mjs sync
node demo/live-stream-video/capture.mjs async
python demo/live-stream-video/cluster.py subscribers
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

## Narrate and edit

The film uses the English synthetic voice `af_heart` from [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M). The lossless voice master is included so you can reproduce the edit offline. The generator used the [int8 Kokoro ONNX model](https://github.com/thewh1teagle/kokoro-onnx/releases/tag/model-files-v1.0) at speed 1.04.

```sh
python demo/live-stream-video/narrate.py
python demo/live-stream-video/render.py
```

To synthesize a revised script, pass your local Kokoro server to the first command with `--speech-url http://127.0.0.1:8880/v1/audio/speech`. To record a human narrator, skip the first command and supply one mono 24 kHz 16-bit PCM WAV named for each key in [scenes.json](scenes.json) under `artifacts/live-stream-video/voice`.

Set `FILM_FONT` and `FILM_FONT_BOLD` to local TrueType paths if Arial or DejaVu Sans is unavailable. Set `FFMPEG` to the encoder executable when using a custom installation. The editor renders H.264/AAC with fast start, and labels compressed waits with their elapsed-time multiplier. Durations follow the narration and the observed cluster timing. Review the framing when changing the capture viewport, dashboard layout or workload names.

See the [Hacker News submission notes](hacker-news.md) for the project link and measured facts.
