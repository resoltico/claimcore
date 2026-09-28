#!/usr/bin/env python3
"""Synthetic negatives only: no local machine is admitted as an independent deployment."""

import base64
import hashlib
import json
import subprocess
import sys
import tempfile
import uuid
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest.mock import patch

sys.dont_write_bytecode = True
from deployment_common import (
    DeploymentRefusal,
    canonical,
    private_path,
    sign,
    utc,
    verify,
)
from deployment_fenced_objects import final_objects_digest
from deployment_probe import machine_id, probe
from deployment_product import product_recheck
from deployment_trust import ROLES, evaluate
from deployment_verify import ssh_command, verify_deployment
from location_inspection import inspect as inspect_locations


def keypair(root, name):
    private = root / (name + ".key")
    public = root / (name + ".pub")
    result = subprocess.run(
        ["openssl", "genpkey", "-algorithm", "ED25519", "-out", str(private)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    assert result.returncode == 0
    result = subprocess.run(
        ["openssl", "pkey", "-in", str(private), "-pubout", "-out", str(public)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    assert result.returncode == 0
    private.chmod(0o600)
    public.chmod(0o600)
    return private, public


def refuses(action, category):
    try:
        action()
    except DeploymentRefusal as error:
        assert error.args[0] == category, (error.args[0], category)
        return
    raise AssertionError("Unsafe deployment evidence passed")


def qualification():
    final_objects = [
        {
            "objectId": str(uuid.uuid4()),
            "cluster": cluster,
            "relativePath": f"fenced-tail/{cluster.lower()}/000000010000000000000001.age",
            "ciphertextSha256": f"{index:064x}",
            "ciphertextBytes": 16 * 1024 * 1024 + 32,
            "walSegment": "000000010000000000000001",
            "walSegmentBytes": 16 * 1024 * 1024,
        }
        for index, cluster in enumerate(("PRIMARY", "WITNESS"), 1)
    ]
    result = {
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "primarySystemId": "1111111111111111111",
        "primaryTimeline": 1,
        "witnessSystemId": "2222222222222222222",
        "witnessTimeline": 1,
        "witnessCutoff": 1,
        "witnessCutoffHash": "a" * 64,
        "custodyObjects": {
            role: {
                "objectId": str(uuid.uuid4()),
                "sha256": hashlib.sha256(role.encode()).hexdigest(),
                "bytes": len(role),
            }
            for role in ("archive", "checkpoint")
        },
        "custodyKeyId": str(uuid.uuid4()),
        "custodyPublicKeySha256": "f" * 64,
        "finalWalObjects": final_objects,
    }
    result["finalWalObjectSha256"] = final_objects_digest({"walObjects": final_objects})
    return result


def report(role, nonce, report_sha, pinned, qualified, proof):
    now = datetime.now(timezone.utc)
    body = {
        "format": "claimcore-deployment-probe-1",
        "role": role,
        "nonce": nonce,
        "qualificationSha256": report_sha,
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(seconds=60)),
        "machineHash": pinned["machineHash"],
        "storageHash": pinned["storageHash"],
        "adminActorId": pinned["adminActorId"],
        "hostKeyId": pinned["hostKeyId"],
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
        "containerized": False,
        "availabilityKind": {
            "primary": "database-read",
            "witness": "database-read",
            "archive": "retained-copy",
            "checkpoint": "retained-copy",
            "key": "key-possession",
        }[role],
        "available": True,
        "keyChallengeProofSha256": proof if role == "key" else None,
    }
    if role in ("primary", "witness"):
        body.update(
            postgresSystemId=pinned["postgresSystemId"], timeline=pinned["timeline"]
        )
        if role == "witness":
            body.update(
                witnessTipSequence=qualified["witnessCutoff"],
                witnessTipHash=qualified["witnessCutoffHash"],
            )
    elif role in ("archive", "checkpoint"):
        body.update(
            objectId=pinned["objectId"],
            objectSha256=pinned["objectSha256"],
            objectBytes=pinned["objectBytes"],
        )
        if role == "archive":
            body.update(
                finalWalObjects=qualified["finalWalObjects"],
                finalWalObjectCount=len(qualified["finalWalObjects"]),
                finalWalObjectSha256=qualified["finalWalObjectSha256"],
            )
    else:
        body.update(
            custodyKeyId=pinned["custodyKeyId"],
            custodyPublicKeySha256=pinned["custodyPublicKeySha256"],
            softwareKeyExportable=True,
        )
    return body


def exercise(root):
    nonce, report_sha, proof = "a" * 64, "b" * 64, "c" * 64
    qualified = qualification()
    roles, envelopes, keys = {}, {}, {}
    for role in ROLES:
        private, public = keypair(root, role)
        keys[role] = private
        pinned = {
            "probePublicKey": str(public),
            "machineHash": hashlib.sha256(b"same physical Mac").hexdigest(),
            "storageHash": hashlib.sha256(("storage " + role).encode()).hexdigest(),
            "adminActorId": str(uuid.uuid4()),
            "hostKeyId": str(uuid.uuid4()),
        }
        if role in ("primary", "witness"):
            pinned.update(
                postgresSystemId=qualified[role + "SystemId"],
                timeline=qualified[role + "Timeline"],
            )
        elif role in ("archive", "checkpoint"):
            item = qualified["custodyObjects"][role]
            pinned.update(
                objectId=item["objectId"],
                objectSha256=item["sha256"],
                objectBytes=item["bytes"],
            )
        else:
            pinned.update(
                custodyKeyId=qualified["custodyKeyId"],
                custodyPublicKeySha256=qualified["custodyPublicKeySha256"],
            )
        roles[role] = pinned
        envelopes[role] = sign(
            report(role, nonce, report_sha, pinned, qualified, proof), private
        )
        assert verify(envelopes[role], public)[0]["role"] == role
    config = {
        "format": "claimcore-deployment-1",
        "mode": "production",
        "roles": roles,
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
    }
    check = lambda target, result=envelopes: evaluate(
        config, result, nonce, report_sha, proof, qualified, target
    )
    refuses(lambda: check(True), "custody-not-independent")
    missing = dict(envelopes)
    missing.pop("witness")
    refuses(lambda: check(True, missing), "deployment-roles")
    invalid = dict(envelopes)
    invalid["witness"] = {
        **invalid["witness"],
        "signatureBase64": base64.b64encode(b"\0" * 64).decode("ascii"),
    }
    refuses(lambda: check(True, invalid), "deployment-signature-invalid")
    refuses(
        lambda: evaluate(
            config, envelopes, "d" * 64, report_sha, proof, qualified, True
        ),
        "probe-challenge",
    )
    for role, field, value, category in (
        ("primary", "issuedAt", "2000-01-01T00:00:00Z", "probe-not-fresh"),
        ("primary", "containerized", True, "container-not-independent-host"),
        ("key", "keyChallengeProofSha256", "f" * 64, "key-custody-proof"),
        ("witness", "installationId", str(uuid.uuid4()), "probe-installation-mismatch"),
        ("archive", "objectSha256", "f" * 64, "probe-object-unrelated"),
        (
            "archive",
            "finalWalObjects",
            qualified["finalWalObjects"][:-1],
            "probe-final-wal-incomplete",
        ),
    ):
        changed = dict(envelopes)
        body = dict(changed[role]["report"])
        body[field] = value
        if field == "issuedAt":
            body["expiresAt"] = "2000-01-01T00:01:00Z"
        changed[role] = sign(body, keys[role])
        refuses(
            lambda changed=changed, category=category: check(True, changed), category
        )
    roles["primary"]["machineHash"] = "e" * 64
    refuses(lambda: check(True), "probe-not-owner-pinned")
    roles["primary"]["machineHash"] = hashlib.sha256(b"same physical Mac").hexdigest()
    distinct = json.loads(json.dumps(config))
    distinct["mode"] = "local-test"
    distinct_envelopes = {}
    for number, role in enumerate(ROLES):
        pinned = distinct["roles"][role]
        pinned["machineHash"] = hashlib.sha256(str(number).encode()).hexdigest()
        pinned["storageHash"] = hashlib.sha256(
            ("disk" + str(number)).encode()
        ).hexdigest()
        distinct_envelopes[role] = sign(
            report(role, nonce, report_sha, pinned, qualified, proof), keys[role]
        )
    shared_admin = json.loads(json.dumps(distinct))
    shared_admin["mode"] = "production"
    shared_admin["roles"]["witness"]["adminActorId"] = shared_admin["roles"]["primary"][
        "adminActorId"
    ]
    shared_envelopes = dict(distinct_envelopes)
    shared_envelopes["witness"] = sign(
        report(
            "witness",
            nonce,
            report_sha,
            shared_admin["roles"]["witness"],
            qualified,
            proof,
        ),
        keys["witness"],
    )
    refuses(
        lambda: evaluate(
            shared_admin, shared_envelopes, nonce, report_sha, proof, qualified, True
        ),
        "custody-not-independent",
    )
    same_cluster = json.loads(json.dumps(distinct))
    same_cluster["mode"] = "production"
    same_cluster["roles"]["witness"]["postgresSystemId"] = qualified["primarySystemId"]
    same_qualified = dict(qualified)
    same_qualified["witnessSystemId"] = qualified["primarySystemId"]
    same_envelopes = dict(distinct_envelopes)
    same_envelopes["witness"] = sign(
        report(
            "witness",
            nonce,
            report_sha,
            same_cluster["roles"]["witness"],
            same_qualified,
            proof,
        ),
        keys["witness"],
    )
    refuses(
        lambda: evaluate(
            same_cluster, same_envelopes, nonce, report_sha, proof, same_qualified, True
        ),
        "clusters-not-independent",
    )
    refuses(
        lambda: evaluate(
            distinct, distinct_envelopes, nonce, report_sha, proof, qualified, False
        ),
        "local-only-qualification",
    )
    # A copied software age identity can prove possession, never non-copyability or readiness.
    corroborated = evaluate(
        {**distinct, "mode": "production"},
        distinct_envelopes,
        nonce,
        report_sha,
        proof,
        qualified,
        True,
    )
    assert corroborated["realDataReady"] is False
    return config, qualified


def product_report_negative(root, config, qualified):
    private, public = keypair(root, "product-verifier")
    now = datetime.now(timezone.utc)
    report = {
        "format": "claimcore-restore-qualification-1",
        "source": "ClaimCore.Database",
        "scope": "full",
        "realDataReady": False,
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
        "primarySystemId": qualified["primarySystemId"],
        "primaryTimeline": qualified["primaryTimeline"],
        "witnessSystemId": qualified["witnessSystemId"],
        "witnessTimeline": qualified["witnessTimeline"],
        "custodyObjects": qualified["custodyObjects"],
        "custodyKeyId": qualified["custodyKeyId"],
        "custodyPublicKeySha256": qualified["custodyPublicKeySha256"],
        "cycleId": str(uuid.uuid4()),
        "backupCaptureSequence": 0,
        "backupCaptureHash": "f" * 64,
        "witnessCutoff": 1,
        "witnessCutoffHash": "a" * 64,
        "primaryRegisteredWalHorizon": "0/100",
        "witnessRegisteredWalHorizon": "0/100",
        "reportSignerKeyId": str(uuid.uuid4()),
        "verifierBinarySha256": "0" * 64,
        "evidenceIndexSha256": "9" * 64,
        "checkpointSha256": "b" * 64,
        "signedInventoryFileSha256": "c" * 64,
        "quiescentBarrierSha256": "d" * 64,
        "catalogManifestSha256": "e" * 64,
        "authorityRevision": 2,
        "authorizedApprovers": [
            {
                "actorId": str(uuid.uuid4()),
                "approvalEventId": str(uuid.uuid4()),
                "active": True,
                "role": "owner",
                "grantRevision": revision,
            }
            for revision in (1, 2)
        ],
        "checkedAt": utc(now),
        "validUntil": utc(now + timedelta(minutes=10)),
        "pendingIntents": 0,
    }
    for name in (
        "catalogVerified",
        "dataAuditVerified",
        "registeredWalVerified",
        "recoveryTailUnsealed",
        "authorityReconciled",
        "managedCopiesRegistered",
        "newerFencesApplied",
        "quiescentAuditBarrierVerified",
        "oidcIssuerHttpsVerified",
        "twoOwnerRosterVerified",
        "recoveredDataChecked",
        "pairCompared",
    ):
        report[name] = True
    envelope = sign(report, private)
    statement = root / "forged-full-report.json"
    detached = root / "forged-full-report.sig"
    statement.write_bytes(canonical(report))
    detached.write_bytes(base64.b64decode(envelope["signatureBase64"]))
    statement.chmod(0o600)
    detached.chmod(0o600)
    config.update(
        {
            "installationId": report["installationId"],
            "fullReportPath": str(statement),
            "fullReportSignaturePath": str(detached),
            "fullReportPublicKey": str(public),
            "productVerifierPath": str(root / "missing-product-verifier"),
            "productVerifierSha256": "0" * 64,
        }
    )
    refuses(lambda: verify_deployment(config), "config-selected-product-verifier")
    config.pop("productVerifierPath")
    original = detached.read_bytes()
    altered = bytearray(original)
    altered[0] ^= 1
    detached.write_bytes(altered)
    refuses(lambda: verify_deployment(config), "deployment-signature-invalid")
    detached.write_bytes(original)
    for field, value, category in (
        ("scope", "synthetic-only", "qualification-not-full"),
        ("validUntil", "2000-01-01T00:00:00Z", "qualification-expired"),
    ):
        changed = dict(report)
        changed[field] = value
        signed = sign(changed, private)
        statement.write_bytes(canonical(changed))
        detached.write_bytes(base64.b64decode(signed["signatureBase64"]))
        refuses(lambda: verify_deployment(config), category)
    statement.write_bytes(canonical(report))
    detached.write_bytes(original)
    marker = root / "stub-executed"
    stub = root / "claimcore-stub"
    stub.write_text(
        "#!/usr/bin/env python3\n"
        + "from pathlib import Path\n"
        + f"Path({str(marker)!r}).write_text('executed')\n",
        encoding="ascii",
    )
    stub.chmod(0o700)
    stub_hash = hashlib.sha256(stub.read_bytes()).hexdigest()
    report["verifierBinarySha256"] = stub_hash
    config["productVerifierPath"] = str(stub)
    config["productVerifierSha256"] = stub_hash
    signed = sign(report, private)
    statement.write_bytes(canonical(report))
    detached.write_bytes(base64.b64decode(signed["signatureBase64"]))
    refuses(lambda: verify_deployment(config), "config-selected-product-verifier")
    assert not marker.exists(), "A hash-pinned stub was executed as a product verifier."


def publication_recheck_synthetic(root):
    private, public = keypair(root, "independent-publication")
    binary = root / "synthetic-database.dll"
    binary.write_bytes(b"synthetic product verifier bytes")
    binary.chmod(0o600)
    binary_hash = hashlib.sha256(binary.read_bytes()).hexdigest()
    now = datetime.now(timezone.utc)
    qualified = qualification()
    qualified["evidenceIndexSha256"] = "d" * 64
    manifest = {
        "format": "claimcore-publication-manifest-1",
        "publicationId": str(uuid.uuid4()),
        "verifierBinarySha256": binary_hash,
        "reportSignerKeyId": str(uuid.uuid4()),
        "checkpointSignerKeyId": str(uuid.uuid4()),
        "installationId": qualified["installationId"],
        "lineageId": qualified["lineageId"],
        "epoch": qualified["epoch"],
        "writerGeneration": 1,
        "witnessCutoff": qualified["witnessCutoff"],
        "witnessCutoffHash": qualified["witnessCutoffHash"],
        "issuedAt": utc(now - timedelta(minutes=1)),
        "validUntil": utc(now + timedelta(minutes=10)),
    }
    signed = sign(manifest, private)
    source = root / "claimcore-publication.json"
    detached = root / "claimcore-publication.sig"
    source.write_bytes(canonical(manifest))
    detached.write_bytes(base64.b64decode(signed["signatureBase64"]))
    source.chmod(0o600)
    detached.chmod(0o600)
    report = root / "synthetic-report.json"
    report_signature = root / "synthetic-report.sig"
    index = root / "synthetic-index.json"
    for path in (report, report_signature, index):
        path.write_bytes(b"synthetic private evidence")
        path.chmod(0o600)
    config = {
        "installationId": qualified["installationId"],
        "productVerifierSha256": binary_hash,
        "fullReportPath": str(report),
        "fullReportSignaturePath": str(report_signature),
        "restoreEvidenceIndexPath": str(index),
    }
    nonce = "e" * 64
    report_sha = "f" * 64

    def response(command, received=nonce):
        assert command[1] == str(binary) and command[2] == "verify-restore-report"
        return canonical(
            {
                "format": "claimcore-restore-recheck-1",
                "nonce": received,
                "status": "EVIDENCE_RECHECKED",
                "reportSha256": report_sha,
                "evidenceIndexSha256": qualified["evidenceIndexSha256"],
                "witnessCutoff": qualified["witnessCutoff"],
                "witnessCutoffHash": qualified["witnessCutoffHash"],
                "realDataReady": False,
            }
        )

    arguments = {
        "root_key": public,
        "publication_files": (source, detached),
        "product_runner": response,
        "binary_path": binary,
    }
    proof = product_recheck(config, nonce, report_sha, qualified, **arguments)
    assert proof["syntheticOnly"] is True, (
        "Injected root cannot assert real-data readiness."
    )
    refuses(
        lambda: product_recheck(
            {**config, "productVerifierPath": str(binary)},
            nonce,
            report_sha,
            qualified,
            **arguments,
        ),
        "config-selected-product-verifier",
    )
    refuses(
        lambda: product_recheck(
            config,
            nonce,
            report_sha,
            qualified,
            **{
                **arguments,
                "product_runner": lambda command: response(command, "0" * 64),
            },
        ),
        "product-recheck-mismatch",
    )
    refuses(
        lambda: product_recheck(
            config,
            nonce,
            report_sha,
            qualified,
            **{**arguments, "root_key": keypair(root, "wrong-publication")[1]},
        ),
        "deployment-signature-invalid",
    )
    binary.write_bytes(b"changed synthetic product verifier bytes")
    refuses(
        lambda: product_recheck(config, nonce, report_sha, qualified, **arguments),
        "published-verifier-digest",
    )


def local_probe_negative(root):
    private, public = keypair(root, "local-probe")
    file = root / "synthetic-copy.age"
    file.write_bytes(b"synthetic encrypted-copy marker")
    file.chmod(0o600)
    final_objects = qualification()["finalWalObjects"]
    for index, item in enumerate(final_objects, 1):
        destination = root / item["relativePath"]
        destination.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        destination.parent.chmod(0o700)
        destination.write_bytes(bytes([index]) * item["ciphertextBytes"])
        destination.chmod(0o600)
        item["ciphertextSha256"] = hashlib.sha256(destination.read_bytes()).hexdigest()
    config = {
        "format": "claimcore-deployment-probe-config-1",
        "role": "archive",
        "adminActorId": str(uuid.uuid4()),
        "hostKeyId": str(uuid.uuid4()),
        "storagePath": str(root),
        "availabilityFile": str(file),
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "objectId": str(uuid.uuid4()),
        "objectSha256": hashlib.sha256(file.read_bytes()).hexdigest(),
        "objectBytes": file.stat().st_size,
        "finalWalArchiveRoot": str(root),
        "finalWalObjects": final_objects,
        "probeSigningKey": str(private),
    }
    envelope = probe(config, "archive", "a" * 64, "b" * 64, None)
    verified, _ = verify(envelope, public)
    assert verified["role"] == "archive" and verified["available"] is True


def ssh_pin_negative(root, config):
    identity = root / "ssh-probe-identity"
    generated = subprocess.run(
        ["ssh-keygen", "-q", "-t", "ed25519", "-N", "", "-f", str(identity)],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
        check=False,
    )
    assert generated.returncode == 0
    identity.chmod(0o600)
    public_words = identity.with_suffix(".pub").read_text("ascii").split()
    known = root / "known_hosts"
    known.write_text(
        "synthetic.example " + public_words[0] + " " + public_words[1] + "\n",
        encoding="ascii",
    )
    known.chmod(0o600)
    pinned = config["roles"]["primary"]
    pinned.update(
        {
            "sshHost": "synthetic.example",
            "sshUser": "claimcore",
            "sshPort": 22,
            "remoteProbePath": "/opt/claimcore/deployment_probe.py",
            "knownHostsFile": str(known),
            "sshIdentityFile": str(identity),
            "sshHostKeySha256": hashlib.sha256(
                base64.b64decode(public_words[1])
            ).hexdigest(),
        }
    )
    command = ssh_command(config, "primary", "a" * 64, "b" * 64, "")
    assert "StrictHostKeyChecking=yes" in command
    assert "ProxyCommand=none" in command and "/dev/null" in command
    original = known.read_text("ascii")
    for content, category in (
        ("", "ssh-host-key-not-uniquely-pinned"),
        (original + original, "ssh-host-key-not-uniquely-pinned"),
        (
            "*.example " + public_words[0] + " " + public_words[1] + "\n",
            "ssh-known-hosts-ambiguous",
        ),
        (
            "@cert-authority synthetic.example "
            + public_words[0]
            + " "
            + public_words[1]
            + "\n",
            "ssh-known-hosts-ambiguous",
        ),
    ):
        known.write_text(content, encoding="ascii")
        refuses(
            lambda: ssh_command(config, "primary", "a" * 64, "b" * 64, ""), category
        )
    known.write_text(original, encoding="ascii")
    pinned["sshHostKeySha256"] = "0" * 64
    refuses(
        lambda: ssh_command(config, "primary", "a" * 64, "b" * 64, ""),
        "ssh-host-key-digest",
    )


def private_path_negative(root):
    public_parent = root / "public-parent"
    public_parent.mkdir(mode=0o755)
    content = public_parent / "private.json"
    content.write_text("{}", encoding="ascii")
    content.chmod(0o600)
    refuses(lambda: private_path(content), "private-path-permissions")
    linked = root / "linked.json"
    linked.symlink_to(content)
    refuses(lambda: private_path(linked), "linked-private-path")


def location_inventory_negative(root):
    registry_private, registry_public = keypair(root, "registry-signer")
    inspector_private, _ = keypair(root, "inspection-signer")
    copy_root = root / "private-copy"
    copy_root.mkdir(mode=0o700)
    copy = copy_root / "known-copy.age"
    copy.write_bytes(b"synthetic ciphertext")
    copy.chmod(0o600)
    now = datetime.now(timezone.utc)
    entry = {
        "copyId": str(uuid.uuid4()),
        "producerKind": "OWNER_ATTESTED",
        "custodianId": "synthetic-owner",
        "location": str(copy),
        "kind": "BASE",
        "sourceCaseId": None,
        "ciphertextSha256": hashlib.sha256(copy.read_bytes()).hexdigest(),
        "ciphertextBytes": copy.stat().st_size,
    }
    registry = {
        "format": "claimcore-copy-location-inventory-1",
        "signingKeyId": str(uuid.uuid4()),
        "installationId": str(uuid.uuid4()),
        "lineageId": str(uuid.uuid4()),
        "epoch": 1,
        "witnessCutoffSequence": 1,
        "witnessCutoffHash": "a" * 64,
        "issuedAt": utc(now),
        "expiresAt": utc(now + timedelta(minutes=5)),
        "entries": [entry],
        "knownUnmanaged": [],
    }
    signed = sign(registry, registry_private)
    report = inspect_locations(
        signed, registry_public, inspector_private, str(uuid.uuid4()), 1024
    )
    assert report["report"]["observations"][0]["status"] == "PRESENT"
    assert report["report"]["observations"][0]["sha256"] == entry["ciphertextSha256"]
    copy.unlink()
    missing = inspect_locations(
        signed, registry_public, inspector_private, str(uuid.uuid4()), 1024
    )
    assert missing["report"]["observations"][0]["status"] == "ABSENT"
    assert missing["report"].get("realDataReady") is None
    altered = dict(registry)
    altered["knownUnmanaged"] = [str(uuid.uuid4())]
    liability = inspect_locations(
        sign(altered, registry_private),
        registry_public,
        inspector_private,
        str(uuid.uuid4()),
        1024,
    )
    assert liability["report"]["registrySha256"] != report["report"]["registrySha256"]
    altered = dict(registry)
    altered["entries"] = [entry, entry]
    refuses(
        lambda: inspect_locations(
            sign(altered, registry_private),
            registry_public,
            inspector_private,
            str(uuid.uuid4()),
            1024,
        ),
        "copy-inventory-duplicate",
    )
    altered = dict(registry)
    altered["entries"] = [entry] * 10001
    refuses(
        lambda: inspect_locations(
            sign(altered, registry_private),
            registry_public,
            inspector_private,
            str(uuid.uuid4()),
            1024,
        ),
        "copy-inventory-bound",
    )
    product = dict(entry)
    product.update(
        copyId=str(uuid.uuid4()),
        producerKind="PRODUCT_EXPORT",
        kind="EXPORT",
        custodianId=None,
        location=None,
    )
    altered = dict(registry)
    altered["entries"] = [product]
    unknown = inspect_locations(
        sign(altered, registry_private),
        registry_public,
        inspector_private,
        str(uuid.uuid4()),
        1024,
    )
    assert unknown["report"]["observations"][0]["status"] == "UNKNOWN"
    altered = dict(registry)
    altered["expiresAt"] = "2000-01-01T00:00:00Z"
    refuses(
        lambda: inspect_locations(
            sign(altered, registry_private),
            registry_public,
            inspector_private,
            str(uuid.uuid4()),
            1024,
        ),
        "copy-inventory-expired",
    )


def main():
    with tempfile.TemporaryDirectory(prefix="claimcore-deploy-test-") as temporary:
        root = Path(temporary).resolve()
        root.chmod(0o700)
        config, qualified = exercise(root)
        product_report_negative(root, config, qualified)
        publication_recheck_synthetic(root)
        probe_root = root / "private-probe"
        probe_root.mkdir(mode=0o700)
        local_probe_negative(probe_root)
        if sys.platform.startswith("linux"):
            for failure in (OSError("synthetic"), UnicodeError("synthetic")):
                with patch("deployment_probe.Path.read_text", side_effect=failure):
                    refuses(machine_id, "machine-id-unavailable")
        ssh_pin_negative(root, config)
        private_path_negative(root)
        location_inventory_negative(root)
    print(
        "Deployment gate refused same-host, missing/forged/stale evidence and absent production authority."
    )


if __name__ == "__main__":
    main()
