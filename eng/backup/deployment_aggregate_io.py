"""Create-only private delivery of a signed deployment aggregate."""

import os
from pathlib import Path

from deployment_common import DeploymentRefusal, private_path, require


def write_aggregate(config, raw, signature):
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
            raise DeploymentRefusal("aggregate-output-exists") from None
        with os.fdopen(descriptor, "wb") as destination:
            destination.write(data)
            destination.flush()
            os.fsync(destination.fileno())
