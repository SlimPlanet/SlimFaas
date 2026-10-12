#!/usr/bin/env python3
"""Install the exact release packages in clean consumers before signing them."""
import argparse
import os
from pathlib import Path
import tempfile

from release_lib import NPM_PACKAGE, python_version, run, version_parts, write_json


def test_packages(directory, version):
    version_parts(version)
    directory = directory.resolve()
    with tempfile.TemporaryDirectory(prefix="slimfaas-consumers-") as temporary:
        root = Path(temporary)
        js = root / "npm"
        js.mkdir()
        write_json(js / "package.json", {"private": True, "type": "module"})
        run("npm", "install", "--ignore-scripts", "--no-audit", "--no-fund",
            directory / f"slimfaas-planet-saver-{version}.tgz", cwd=js)
        run("node", "--input-type=module", "-e",
            f"import Client from '{NPM_PACKAGE}'; if(typeof Client !== 'function') throw new Error('Missing export');", cwd=js)
        py = python_version(version)
        for suffix in ("-py3-none-any.whl", ".tar.gz"):
            environment = root / ("wheel" if suffix.endswith("whl") else "sdist")
            run("uv", "venv", environment, cwd=root)
            interpreter = environment / ("Scripts/python.exe" if os.name == "nt" else "bin/python")
            run("uv", "pip", "install", "--python", interpreter, directory / f"slimfaas_client-{py}{suffix}", cwd=root)
            run(interpreter, "-c", "import slimfaas_client; from importlib.metadata import version; "
                f"assert version('slimfaas-client') == '{py}'", cwd=root)
        dotnet = root / "dotnet"
        dotnet.mkdir()
        (dotnet / "Consumer.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="SlimFaasClient" Version="{version}" /></ItemGroup>
</Project>
''')
        (dotnet / "Program.cs").write_text('''using System;
using System.Reflection;
var assembly = Assembly.Load("SlimFaasClient");
if (assembly.GetExportedTypes().Length == 0) throw new Exception("Client has no exported API");
Console.WriteLine(assembly.FullName);
''')
        # Isolated cache prevents an already-published package hiding a broken local package.
        environment = dict(os.environ, NUGET_PACKAGES=str(root / "nuget-cache"))
        run("dotnet", "restore", "--source", directory, "--source", "https://api.nuget.org/v3/index.json", cwd=dotnet, env=environment)
        run("dotnet", "run", "--no-restore", cwd=dotnet, env=environment)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    test_packages(args.directory, args.version)
