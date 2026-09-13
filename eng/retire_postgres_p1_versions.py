"""One-off, exact-version retirement for superseded ClaimCore PostgreSQL p1 images."""

import hashlib
import json
import os
import sys
from pathlib import Path
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

API = "https://api.github.com/"
VERSIONS = "users/resoltico/packages/container/claimcore-postgres/versions"
MAIN_BASELINE = (
    "repos/resoltico/claimcore/contents/db/postgresql-baseline.json?ref=main"
)
BASELINE_FILE = Path(__file__).resolve().parents[1] / "db/postgresql-baseline.json"
BASELINE_IMAGE = (
    "ghcr.io/resoltico/claimcore-postgres:18.6-trixie-p2-r34736975281-1"
    "@sha256:ae84d4fd380ade930b6246340a39e2448786f5bcaad258ee1f2e62807a5f8c45"
)
BASELINE_VERSION = (
    1242208996,
    "sha256:ae84d4fd380ade930b6246340a39e2448786f5bcaad258ee1f2e62807a5f8c45",
    "18.6-trixie-p2-r34736975281-1",
)
ALLOWLIST_FILE = Path(__file__).with_name("postgres_p1_retirement_allowlist.tsv")
ALLOWLIST_SHA256 = "3ab69d89c05ee99346a9e2300622bf55561fc84d06d3bc42f9cc725fb40b8469"


class RetirementError(Exception):
    """Safe, non-payload-bearing retirement failure."""


class VersionAbsent(RetirementError):
    """An exact obsolete version has already been removed."""


def load_targets():
    raw = ALLOWLIST_FILE.read_bytes()
    if hashlib.sha256(raw).hexdigest() != ALLOWLIST_SHA256:
        raise RetirementError(
            "The exact p1 allowlist changed; no versions were deleted."
        )
    rows = [line.split("|") for line in raw.decode("ascii").splitlines()]
    if len(rows) != 9 or any(len(row) != 3 for row in rows):
        raise RetirementError("The exact p1 allowlist is malformed.")
    return tuple((int(row[0]), row[1], row[2]) for row in rows)


TARGETS = load_targets()


def require_baseline(document):
    if (
        not isinstance(document, dict)
        or document.get("containerImage") != BASELINE_IMAGE
    ):
        raise RetirementError(
            "The reviewed p2 baseline changed; no versions were deleted."
        )


def require_version(actual, expected):
    version_id, digest, tag = expected
    metadata = actual.get("metadata") if isinstance(actual, dict) else None
    container = metadata.get("container") if isinstance(metadata, dict) else None
    if not isinstance(actual, dict) or not all(
        (
            actual.get("id") == version_id,
            actual.get("name") == digest,
            actual.get("url") == f"{API}{VERSIONS}/{version_id}",
            isinstance(metadata, dict) and metadata.get("package_type") == "container",
            isinstance(container, dict) and container.get("tags") == [tag],
        )
    ):
        raise RetirementError(
            f"Package version {version_id} metadata changed; no versions were deleted."
        )


def preflight(fetch):
    if len(TARGETS) != 9 or len({row[0] for row in TARGETS}) != 9:
        raise RetirementError(
            "The exact p1 allowlist changed; no versions were deleted."
        )
    if any(
        row[0] == BASELINE_VERSION[0]
        or row[1] == BASELINE_VERSION[1]
        or row[2] == BASELINE_VERSION[2]
        for row in TARGETS
    ):
        raise RetirementError("The active p2 version entered the deletion list.")
    try:
        local_baseline = json.loads(BASELINE_FILE.read_text(encoding="utf-8"))
    except ValueError:
        raise RetirementError(
            "The local baseline is invalid; no versions were deleted."
        ) from None
    require_baseline(local_baseline)
    require_baseline(fetch(MAIN_BASELINE, "application/vnd.github.raw+json"))
    require_version(fetch(f"{VERSIONS}/{BASELINE_VERSION[0]}"), BASELINE_VERSION)
    present = []
    for row in TARGETS:
        try:
            actual = fetch(f"{VERSIONS}/{row[0]}")
        except VersionAbsent:
            continue
        require_version(actual, row)
        present.append(row)
    return present


def run(fetch, erase, environment):
    if (
        environment.get("GITHUB_ACTIONS"),
        environment.get("GITHUB_EVENT_NAME"),
        environment.get("GITHUB_REPOSITORY"),
        environment.get("GITHUB_REF"),
    ) != ("true", "workflow_dispatch", "resoltico/claimcore", "refs/heads/main"):
        raise RetirementError(
            "Retirement is limited to manual runs on resoltico/claimcore main."
        )
    execute = environment.get("CLAIMCORE_RETIRE_EXECUTE", "false")
    if execute not in ("true", "false"):
        raise RetirementError("Retirement mode must be true or false.")
    if (
        execute == "true"
        and environment.get("CLAIMCORE_RETIRE_CONFIRM") != "DELETE 9 P1 VERSIONS"
    ):
        raise RetirementError("Deletion requires the exact nine-version confirmation.")
    present = preflight(fetch)
    absent = len(TARGETS) - len(present)
    if execute == "false":
        print(
            f"Dry run: p2 protected; p1 versions present={len(present)}, already absent={absent}; no deletion."
        )
        return
    for version_id, _, _ in present:
        erase(f"{VERSIONS}/{version_id}")
        print(f"Deleted superseded p1 package version {version_id}.")
    print(f"Retirement complete: deleted={len(present)}, already absent={absent}.")


def request(path, method="GET", accept="application/vnd.github+json"):
    token = os.environ.get("GH_TOKEN")
    if not token:
        raise RetirementError("The GitHub workflow token is unavailable.")
    headers = {
        "Accept": accept,
        "Authorization": f"Bearer {token}",
        "X-GitHub-Api-Version": "2026-03-10",
    }
    try:
        with urlopen(
            Request(API + path, headers=headers, method=method), timeout=20
        ) as response:
            if response.status != (204 if method == "DELETE" else 200):
                raise RetirementError(
                    f"GitHub package API returned HTTP {response.status}."
                )
            return None if method == "DELETE" else json.load(response)
    except HTTPError as error:
        if (
            error.code == 404
            and method == "GET"
            and path in {f"{VERSIONS}/{row[0]}" for row in TARGETS}
        ):
            raise VersionAbsent("Exact obsolete package version is absent.") from None
        raise RetirementError(
            f"GitHub package API returned HTTP {error.code}."
        ) from None
    except (URLError, ValueError):
        raise RetirementError(
            "GitHub package API response was unavailable or invalid."
        ) from None


if __name__ == "__main__":
    try:
        run(
            lambda path, accept="application/vnd.github+json": request(
                path, accept=accept
            ),
            lambda path: request(path, method="DELETE"),
            os.environ,
        )
    except (RetirementError, OSError) as error:
        print(f"Retirement stopped: {error}", file=sys.stderr)
        raise SystemExit(1) from None
