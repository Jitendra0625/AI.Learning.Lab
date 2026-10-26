import subprocess
from pathlib import Path

def run_gate(name, command, folder):
    print(f"[gate:{name}] running: {' '.join(command)}")
    result = subprocess.run(
        command,
        cwd=folder,
        capture_output=True,
        text=True,
        timeout=300,
    )
    passed = result.returncode == 0
    output = result.stdout + result.stderr

    Path("work/logs").mkdir(parents=True, exist_ok=True)
    Path(f"work/logs/{name}.txt").write_text(output, encoding="utf-8")

    print(f"[gate:{name}] {'PASS' if passed else 'FAIL'}")
    return passed, output

def build_gate(folder):
    return run_gate("build", ["dotnet", "build"], folder)

def test_gate(folder):
    return run_gate("test", ["dotnet", "test"], folder)

if __name__ == "__main__":
    run_gate("dotnet-version", ["dotnet", "--version"], ".")    