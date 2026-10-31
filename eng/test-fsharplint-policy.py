#!/usr/bin/env python3
"""Compile isolated controls for the retained complexity and whitespace policies."""
import json
import pathlib
import subprocess
import tempfile


CONTROLS = {
    "cyclomaticComplexity": (
        "maxComplexity", 12,
        "let choose value = if value = 0 then 1 else 0\n",
        "let choose value =\n" + "\n".join(
            ("    if" if index == 0 else "    elif") + f" value = {index} then {index}"
            for index in range(20)
        ) + "\n    else 20\n",
    ),
    "trailingWhitespaceOnLine": (
        "numberOfSpacesAllowed", 0,
        "let value = 1\n",
        "let value = 1   \n",
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
    print("FSharpLint policy controls passed: complexity and trailing whitespace")


if __name__ == "__main__":
    main()
