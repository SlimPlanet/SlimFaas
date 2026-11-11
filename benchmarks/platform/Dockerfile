# Test instrumentation inherits the exact runtime image under comparison.
ARG RUNTIME_IMAGE
FROM mcr.microsoft.com/dotnet/sdk:10.0.403-alpine3.24 AS tools
WORKDIR /source
COPY . .
RUN dotnet publish benchmarks/SlimFaasBenchmark -c Release -r linux-musl-arm64 \
      --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -o /tools/benchmark && \
    dotnet publish tools/SlimFaas.MemoryLab -c Release -r linux-musl-arm64 \
      --self-contained true -p:PublishAot=false -p:PublishTrimmed=false -o /tools/memorylab

FROM ${RUNTIME_IMAGE}
USER root
RUN apk add --no-cache bash python3 py3-pip util-linux iproute2 iptables e2fsprogs
COPY --from=tools /tools /tools
COPY .bin /src/.bin
COPY tests/SlimFaas.Tests/Local/chaos_function.py /src/tests/SlimFaas.Tests/Local/chaos_function.py
COPY client/python/slimfaas-client /src/client/python/slimfaas-client
RUN python3 -m venv /opt/validation && \
    python3 -c 'import tomllib; from pathlib import Path; p=tomllib.loads(Path("/src/client/python/slimfaas-client/uv.lock").read_text()); print("websockets=="+next(x["version"] for x in p["package"] if x["name"]=="websockets"))' > /tmp/validation-requirements.txt && \
    /opt/validation/bin/pip install --no-deps --only-binary=:all: -r /tmp/validation-requirements.txt && \
    rm /tmp/validation-requirements.txt
ENV PATH="/opt/validation/bin:${PATH}" \
    PYTHONPATH="/src/client/python/slimfaas-client/src"
WORKDIR /src
ENTRYPOINT ["/bin/sh", "/src/.bin/platform-entrypoint.sh"]
