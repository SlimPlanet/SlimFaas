"""Build one edition of the Live Stream film using its bundled English voice."""
import argparse
from pathlib import Path
import subprocess
import sys

HERE = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("edition", choices=("educational", "marketing", "house"))
    args = parser.parse_args()
    editor = [HERE / "render.py"] if args.edition == "educational" else [HERE / "marketing/render.py", args.edition]
    subprocess.run([sys.executable, HERE / "narrate.py", "--mode", args.edition], check=True)
    subprocess.run([sys.executable, *editor], check=True)


if __name__ == "__main__":
    main()
