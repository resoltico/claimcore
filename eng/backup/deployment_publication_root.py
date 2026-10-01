"""Source-reviewed, package-fixed publication root; never an owner config input."""

import hashlib
import re
import stat
from pathlib import Path

from backup_types import Json
from deployment_common import DeploymentRefusalError, require

# A separately custodied public key is pinned only by an exact reviewed source revision.
# The private key must never be installed with ClaimCore. This checkout is unprovisioned.
REVIEWED_PUBLICATION_ROOT_SHA256: str | None = None
ROOT_LIMIT = 8192


def reviewed_publication_root(
    *, pinned_sha: Json = None, source: str | Path | None = None
) -> bytes | None:
    """Return package public bytes only when the reviewed source pin is present."""
    pinned = REVIEWED_PUBLICATION_ROOT_SHA256 if pinned_sha is None else pinned_sha
    if pinned is None:
        return None
    require(
        isinstance(pinned, str) and re.fullmatch(r"[0-9a-f]{64}", pinned),
        "publication-root-pin-invalid",
    )
    path = (
        Path(__file__).with_name("reviewed-publication-root.pem")
        if source is None
        else Path(source)
    )
    try:
        info = path.lstat()
        require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, "publication-root-file")
        require(0 < info.st_size <= ROOT_LIMIT, "publication-root-size")
        raw = path.read_bytes()
    except OSError:
        msg = "publication-root-unavailable"
        raise DeploymentRefusalError(msg) from None
    require(hashlib.sha256(raw).hexdigest() == pinned, "publication-root-digest")
    require(
        raw.startswith(b"-----BEGIN PUBLIC KEY-----\n")
        and raw.endswith(b"-----END PUBLIC KEY-----\n"),
        "publication-root-format",
    )
    return raw
