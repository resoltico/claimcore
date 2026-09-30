"""Synthetic candidate documents over the isolated restore fixture."""

import json
import os
import re
import uuid
from collections.abc import Sequence
from dataclasses import dataclass
from pathlib import Path

from backup_types import JsonObject
from managed_copy_drill_common import digest, run

ARGUMENT_COUNT = 8
WAL_SEGMENT_NAME = re.compile(r"[0-9A-F]{24}")
PLAINTEXT_LIMIT_BYTES = 1073741824
TAR_ENTRY_LIMIT = 100000


@dataclass(frozen=True)
class DrillInputs:
    """The values the shell owner passes to the drill, in argument order."""

    scratch: Path
    installation: str
    lineage: str
    epoch: int
    sequence: int
    cutoff_hash: str
    owner_role: str
    database_name: str

    @classmethod
    def from_arguments(cls, arguments: Sequence[str]) -> "DrillInputs":
        """Build the drill inputs from the shell owner's argument order."""
        scratch, installation, lineage, epoch, sequence, cutoff_hash, owner_role, database = (
            arguments
        )
        return cls(
            Path(scratch),
            installation,
            lineage,
            int(epoch),
            int(sequence),
            cutoff_hash,
            owner_role,
            database,
        )


@dataclass(frozen=True)
class CopyFixture:
    """The fixture's objects, verifier key pair and proof directory."""

    inputs: DrillInputs
    manifest_file: Path
    wal_range: JsonObject
    timeline: int
    system: str
    segment: Path
    proof_root: Path
    signing_key: Path
    public_key: Path

    @classmethod
    def prepare(cls, inputs: DrillInputs) -> "CopyFixture":
        """Create the verifier key pair and proof directory for the drill."""
        manifest_file = inputs.scratch / "primary" / "backup_manifest"
        manifest = json.loads(manifest_file.read_text(encoding="ascii"))
        (wal_range,) = manifest["WAL-Ranges"]
        segments = sorted(
            path
            for path in (inputs.scratch / "primary" / "pg_wal").iterdir()
            if WAL_SEGMENT_NAME.fullmatch(path.name)
        )
        if not segments:
            msg = "managed-copy-verifier-test-no-wal"
            raise RuntimeError(msg)
        proof_root = inputs.scratch / "copy-proofs"
        proof_root.mkdir(mode=0o700)
        signing_key, public_key = proof_root / "verifier.key", proof_root / "verifier.pub"
        run(["openssl", "genpkey", "-algorithm", "Ed25519", "-out", str(signing_key)])
        run(["openssl", "pkey", "-in", str(signing_key), "-pubout", "-out", str(public_key)])
        signing_key.chmod(0o600)
        public_key.chmod(0o600)
        return cls(
            inputs,
            manifest_file,
            wal_range,
            int(wal_range["Timeline"]),
            str(manifest["System-Identifier"]),
            segments[0],
            proof_root,
            signing_key,
            public_key,
        )

    @property
    def base_object(self) -> Path:
        """Return the encrypted base-backup object."""
        return self.inputs.scratch / "archive" / "primary.tar.age"

    @property
    def wal_object(self) -> Path:
        """Return the encrypted WAL-segment object."""
        return self.inputs.scratch / "archive" / "primary-wal" / (self.segment.name + ".age")

    def candidate(self, kind: str, object_file: Path, label: str) -> JsonObject:
        """Build a verification input for `object_file` labelled `label`."""
        base = kind == "BASE"
        inputs = self.inputs
        return {
            "format": "claimcore-managed-copy-verification-input-1",
            "verificationEventId": str(uuid.uuid4()),
            "copyId": str(uuid.uuid4()),
            "copyEventId": str(uuid.uuid4()),
            "copyRevision": 2,
            "archiveObjectId": str(uuid.uuid4()),
            "installationId": inputs.installation,
            "lineageId": inputs.lineage,
            "witnessEpoch": inputs.epoch,
            "witnessCutoffSequence": inputs.sequence,
            "witnessCutoffHash": inputs.cutoff_hash,
            "cluster": "PRIMARY",
            "kind": kind,
            "postgresSystemId": self.system,
            "timeline": self.timeline,
            "walSegmentBytes": self.segment.stat().st_size,
            "backupManifestSha256": digest(self.manifest_file) if base else None,
            "walStartLsn": self.wal_range["Start-LSN"] if base else None,
            "walEndLsn": self.wal_range["End-LSN"] if base else None,
            "walSegment": None if base else self.segment.name,
            "locationCommitment": os.urandom(32).hex(),
            "ciphertextSha256": digest(object_file),
            "ciphertextBytes": object_file.stat().st_size,
            "ciphertextFile": str(object_file),
            "archiveRoot": str(inputs.scratch / "archive"),
            "ageIdentityFile": str(inputs.scratch / "identity.age"),
            "verificationSigningKeyFile": str(self.signing_key),
            "verifierSigningKeyId": str(uuid.uuid4()),
            "verifierHolderActorId": str(uuid.uuid4()),
            "outputProofFile": str(self.proof_root / (label + ".json")),
            "outputSignatureFile": str(self.proof_root / (label + ".sig")),
            "privateScratchRoot": str(self.proof_root),
            "maximumPlaintextBytes": PLAINTEXT_LIMIT_BYTES,
            "maximumTarEntries": TAR_ENTRY_LIMIT,
            "databaseOwnerRole": inputs.owner_role if base else None,
            "databaseName": inputs.database_name if base else None,
        }
