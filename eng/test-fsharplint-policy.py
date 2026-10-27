#!/usr/bin/env python3
"""Compile isolated lint controls, then prove each configured limit rejects its violation."""
import json
import pathlib
import subprocess
import tempfile


CONTROLS = {
    "maxLinesInFile": (
        "maxLinesInFile", 500,
        "let value = 1\n" + "\n" * 498,
        "let value = 1\n" + "\n" * 501,
    ),
    "maxNumberOfFunctionParameters": (
        "maxItems", 5,
        "let total a b c d e = a + b + c + d + e\nlet _ = total 1 2 3 4 5\n",
        "let total a b c d e f = a + b + c + d + e + f\nlet _ = total 1 2 3 4 5 6\n",
    ),
    "maxNumberOfItemsInTuple": (
        "maxItems", 4,
        "let value = (1, 2, 3, 4)\n",
        "let value = (1, 2, 3, 4, 5)\n",
    ),
}


def command(arguments):
    return subprocess.run(arguments, capture_output=True, text=True, timeout=30, check=False)


def require_success(result, reason):
    if result.returncode != 0:
        raise RuntimeError(f"{reason}\n{result.stdout}\n{result.stderr}")


def main():
    policy = json.loads(pathlib.Path("eng/fsharplint.json").read_text(encoding="utf-8"))
    with tempfile.TemporaryDirectory(prefix="claimcore-lint-controls-") as directory:
        root = pathlib.Path(directory)
        for name, (setting, maximum, positive, negative) in CONTROLS.items():
            rule = policy[name]
            if not rule["enabled"] or rule["config"][setting] != maximum:
                raise RuntimeError(f"{name} must enforce the adopted limit {maximum}")
            # Exactly one rule is enabled, so another lint failure cannot qualify this control.
            config = root / f"{name}.json"
            config.write_text(json.dumps({name: rule}), encoding="utf-8")
            for label, source in [("positive", positive), ("negative", negative)]:
                file = root / f"{name}-{label}.fsx"
                file.write_text(source, encoding="utf-8")
                require_success(command(["dotnet", "fsi", "--exec", str(file)]), f"Compile {name} {label}")
                result = command([
                    "dotnet", "fsharplint", "lint", "--file-type", "file", str(file),
                    "--lint-config", str(config),
                ])
                if label == "positive":
                    require_success(result, f"Positive lint control {name}")
                elif result.returncode == 0:
                    raise RuntimeError(f"{name} did not reject its compiled violation")
                elif not result.stdout.strip() and not result.stderr.strip():
                    raise RuntimeError(f"{name} failed without diagnostic evidence")
    print("FSharpLint limit controls passed: file length, parameters, tuple items")


if __name__ == "__main__":
    main()
