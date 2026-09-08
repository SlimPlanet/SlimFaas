# Get Started with Docker Compose

Use the Docker orchestrator to explore four functions, events, jobs and data in the SlimFaas dashboard. This demonstration uses one SlimFaas node and persistent Docker volumes.

## Before you start

Install Git and Docker with **Compose v2**. Start the engine and allow it to pull .NET base images and build the sample applications. Local .NET and Node installations are not required for container builds.

```bash
docker version
docker compose version
git clone https://github.com/SlimPlanet/SlimFaas.git
cd SlimFaas
```

Keep port `30021` free. Stop a native-local demo first because its node ports overlap. SlimFaas manages containers through the mounted Docker socket; this demo is intended for a local development engine.

## Build and start

The tutorial overlay extends the repository's Compose example with the remaining Fibonacci functions and correct callback addresses. Build the job image first, then start only the core services. The first command builds **FibonacciBatch**, the program executed by jobs created through the SlimFaas API. These job containers are created on demand, so `docker compose up` does not build their image. The tag `slimfaas-tour-batch:local` must match `Configurations.fibonacci.Image` in the overlay. The final `.` supplies the repository root as the build context because FibonacciBatch also references the .NET client under `client/dotnet/SlimFaasClient`.

```bash
docker build -f src/FibonacciBatch/Dockerfile -t slimfaas-tour-batch:local .
docker compose -f docker-compose.yml -f demo/docker-compose.get-started.yml up -d --build slimfaas fibonacci1 fibonacci2 fibonacci3 fibonacci4
docker compose -f docker-compose.yml -f demo/docker-compose.get-started.yml logs -f slimfaas
```

The explicit service list leaves Kafka, its connector and Jaeger for their own guides. The overlay enables the data APIs for the tour and configures the `fibonacci` job to use the image built above. Its `fibonacci1` dependency also provides a network for the job.

Leave the services running; **Ctrl+C** exits the log viewer. SlimFaas can replace the original function containers with managed replicas as it scales, so use the dashboard for the complete function inventory.

## Open the dashboard

Open **http://127.0.0.1:30021/**. In a terminal at the repository root:

```bash
export BASE_URL=http://127.0.0.1:30021
curl -i "$BASE_URL/ready"
curl -fsS "$BASE_URL/status-functions"
curl -fsS "$BASE_URL/function/fibonacci1/hello/compose"
```

Expect `200 READY`, the function list and a greeting. Retry readiness while the first startup is in progress. The table shows functions waking and returning to zero after inactivity; the network map shows the route taken by each request.

Continue to the [Guided Tour](guided-tour.md). Select **Compose** in Bruno.

## Docker socket and Podman

By default the example mounts `/var/run/docker.sock`. For a different socket, set `DOCKER_SOCKET_PATH` to the host socket path before starting Compose; SlimFaas connects to its mounted path through `DOCKER_HOST` inside the container.

The repository includes Podman setup helpers. On macOS:

```bash
./run-podman-compose.sh -f docker-compose.yml -f demo/docker-compose.get-started.yml up -d --build slimfaas fibonacci1 fibonacci2 fibonacci3 fibonacci4
```

Build the batch image with `podman build -f src/FibonacciBatch/Dockerfile -t slimfaas-tour-batch:local .` first. See the helper's output for socket configuration. On Windows, use the PowerShell helper `run-podman-compose.ps1` with the same Compose arguments. The tour's multiline cURL commands use Bash; Bruno provides the same requests on Windows.

## Troubleshooting

| Symptom | What to check |
|---|---|
| Cannot connect to Docker | Start Docker/Podman and inspect `docker context show` and the socket path. |
| Function calls time out | Read SlimFaas logs, inspect container health and confirm all tutorial services were built. |
| Callback never completes | Confirm `SlimFaas__BaseUrl` is `http://slimfaas:30021` inside the function container. |
| Job cannot start | Build `slimfaas-tour-batch:local` on the same engine SlimFaas uses. |
| Data API returns 404 | Start with both Compose files; the overlay enables public data access for this demo. |
| A function disappears from Compose's list | SlimFaas creates its own replicas. Inspect the dashboard and `docker ps -a`. |

For Kafka, use the [Kafka guide](kafka.md); for exported traces, see [OpenTelemetry](opentelemetry.md).

## Stop and reset

Stop and remove the Compose services:

```bash
docker compose -f docker-compose.yml -f demo/docker-compose.get-started.yml down
```

SlimFaas-created replicas and templates can survive Compose cleanup. Before removing any remaining containers, inspect only the demonstrated function labels and names with `docker ps -a`; remove the specific demo containers you identify. Do not use a global container prune.

Named data and backup volumes are retained. To reset this dedicated demo, use the same `down` command with `--volumes` after removing its remaining managed containers. This deletes the demo's stored data.
