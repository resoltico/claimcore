"""Create-only private delivery of a signed deployment aggregate."""

import os
from pathlib import Path

from backup_types import JsonObject
from deployment_common import DeploymentRefusalError, private_path, require
from managed_common import sync_directory


def write_aggregate(config: JsonObject, raw: bytes, signature: bytes) -> None:
    """Write the aggregate and its signature, refusing to replace either file."""
    outputs = (config["aggregateOutputFile"], config["aggregateSignatureFile"])
    require(outputs[0] != outputs[1], "aggregate-output-path")
    for path, data in zip(outputs, (raw, signature), strict=True):
        target = Path(path)
        require(target.is_absolute() and not target.exists(), "aggregate-output-exists")
        private_path(target.parent, directory=True)
        try:
            descriptor = os.open(
                target, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600
            )
        except OSError:
            msg = "aggregate-output-exists"
            raise DeploymentRefusalError(msg) from None
        with os.fdopen(descriptor, "wb") as destination:
            destination.write(data)
            destination.flush()
            os.fsync(destination.fileno())
        sync_directory(target.parent)
