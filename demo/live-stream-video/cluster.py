"""Install the recording workloads into the dedicated kind cluster."""
import argparse
from pathlib import Path
import subprocess

HERE = Path(__file__).resolve().parent


def install(stage):
    subprocess.run([
        "kubectl", "--kubeconfig", str(HERE / "artifacts" / "kubeconfig"),
        "--context", "kind-slimfaas-film", "--namespace", "slimfaas-demo",
        "apply", "-f", str(HERE / "cluster" / f"{stage}.yaml"),
    ], check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", choices=["start", "subscribers"])
    install(parser.parse_args().stage)
