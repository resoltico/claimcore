"""Negative controls for bounded, nonpayload backup failure categories."""

import subprocess

import managed


def main():
    assert (
        managed.failure_category(managed.BackupFailure("manifest-invalid"))
        == "manifest-invalid"
    )
    assert (
        managed.failure_category(managed.BackupFailure("private /claimant"))
        == "backup-refusal"
    )
    assert (
        managed.failure_category(PermissionError("private /claimant"))
        == "backup-permission-denied"
    )
    assert (
        managed.failure_category(FileNotFoundError("private /claimant"))
        == "backup-file-unavailable"
    )
    assert (
        managed.failure_category(subprocess.TimeoutExpired("private", 1))
        == "backup-subprocess-timeout"
    )
    assert (
        managed.failure_category(KeyError("private /claimant")) == "backup-data-invalid"
    )
    assert (
        managed.failure_category(RuntimeError("private /claimant"))
        == "unexpected-backup-failure"
    )


if __name__ == "__main__":
    main()
