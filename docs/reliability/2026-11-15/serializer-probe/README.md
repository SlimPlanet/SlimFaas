# Limited-heap serializer regression

Run from the repository root at `c2fa989a` or a later source containing the exact
serializer. This standalone test executable uses the existing test assembly's
internal access; it adds no dependency and changes no product configuration.
The `legacy` mode calls the original serializer API in this same probe. It is
not an unmodified full-runtime measurement.

```bash
probe_dir=artifacts/pr506/reproductions/batch-response-memory-02/probe
mkdir -p "$probe_dir"
cp docs/reliability/2026-11-15/serializer-probe/Program.cs "$probe_dir/Program.cs"
cp docs/reliability/2026-11-15/serializer-probe/Probe.csproj.template "$probe_dir/Probe.csproj"
dotnet build "$probe_dir/Probe.csproj" -c Release -nr:false
probe_dll="$probe_dir/bin/Release/net11.0/SlimData.Tests.dll"
# Successful legacy control: retain its byte length and SHA-256.
dotnet "$probe_dll" legacy
# Expected exit 3 with OutOfMemoryException; keep the failed observation.
DOTNET_GCHeapHardLimit=0x14000000 dotnet "$probe_dll" legacy
# Expected exit 0 with the same encoded length/SHA-256 as the legacy control.
DOTNET_GCHeapHardLimit=0x14000000 dotnet "$probe_dll" exact
```

Confirm the printed bounded `heapLimit` is 335,544,320 bytes. The probe allocates
64 distinct 2 MiB payloads and keeps them alive through serialization. Printed
heap values are observations after encoding, not peak memory or RSS. Do not
compare its single execution times as an application performance benchmark.
