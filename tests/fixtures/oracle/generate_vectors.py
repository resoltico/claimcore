"""Independent Python oracle for ClaimCore durable formats.

It shares no code with the F# implementation. From the source objects in sources.json it
derives RFC 8785 canonical bytes, case-reference identities (UUIDv8), framed ledger payloads,
salted commitments, the 183-byte entry preimage and entry hashes, in the structure of
tests/fixtures/record-format-vectors.json.

  python3 -I generate_vectors.py --request 1 --snapshot 1 --authority 1

Writes JSON to standard output.
"""

import argparse
import copy
import decimal
import datetime
import hashlib
import json
import pathlib
import uuid

SAFE = 9007199254740991
SHORT = {0x08: "\\b", 0x09: "\\t", 0x0A: "\\n", 0x0C: "\\f", 0x0D: "\\r"}
ACT_CODES = {
    "OPEN": 1, "AMEND_REGISTRATION": 2, "CORRECT": 3, "DECIDE": 4, "WITHDRAW_DECISION": 5,
    "RECORD_PAYMENT": 6, "CLEAR_PAYMENT": 7, "CLOSE": 8, "REOPEN": 9, "VOID": 10,
    "REINSTATE": 11, "ERASE": 12, "PROVISION_OWNER": 32, "REGISTER": 33, "GRANT": 34, "REVOKE": 35,
    "ENABLE": 36, "DISABLE": 37,
}
ENTRY_TAG = b"claimcore/ledger-entry/1\n"
REFERENCE_TAG = b"claimcore/case-reference-id/1\n"


def string(value):
    out = ['"']
    for ch in value:
        code = ord(ch)
        if 0xD800 <= code <= 0xDFFF:
            raise ValueError("lone surrogate")
        if ch == '"':
            out.append('\\"')
        elif ch == "\\":
            out.append("\\\\")
        elif code in SHORT:
            out.append(SHORT[code])
        elif code < 0x20:
            out.append("\\u%04x" % code)
        else:
            out.append(ch)
    out.append('"')
    return "".join(out)


def canonical(value):
    """RFC 8785 for ClaimCore's integer-only I-JSON subset."""
    if isinstance(value, dict):
        keys = sorted(value, key=lambda key: key.encode("utf-16-be"))
        return "{" + ",".join(string(k) + ":" + canonical(value[k]) for k in keys) + "}"
    if isinstance(value, list):
        return "[" + ",".join(canonical(v) for v in value) + "]"
    if value is True:
        return "true"
    if value is False:
        return "false"
    if value is None:
        return "null"
    if isinstance(value, int):
        if not -SAFE <= value <= SAFE:
            raise ValueError("unsafe integer")
        return str(value)
    if isinstance(value, str):
        return string(value)
    raise ValueError("unsupported value")


def vector(name, value):
    data = canonical(value).encode("utf-8")
    return {"name": name, "canonicalUtf8": data.decode("utf-8"), "canonicalHex": data.hex(),
            "sha256": hashlib.sha256(data).hexdigest()}


def with_format(value, number):
    copy = dict(value)
    copy["format"] = number
    return copy


def fold_reference(reference):
    return "".join(chr(ord(c) - 32) if "a" <= c <= "z" else c for c in reference)


def canonical_identity(request):
    result = copy.deepcopy(request)
    def amount(values, key):
        text = format(decimal.Decimal(values[key]), "f")
        values[key] = text.rstrip("0").rstrip(".") if "." in text else text
    act = result["act"]
    if result["kind"] == "CASE":
        result["caseReference"] = fold_reference(result["caseReference"])
        if act["kind"] in ("OPEN", "AMEND_REGISTRATION"):
            amount(act["values"], "claimedAmount")
        elif act["kind"] == "DECIDE":
            amount(act["values"], "payableAmount")
        elif act["kind"] == "CORRECT":
            for group, key in (("registration", "claimedAmount"), ("decision", "payableAmount")):
                if act["groups"][group]["kind"] == "REPLACE":
                    amount(act["groups"][group]["values"], key)
    elif act["kind"] in ("GRANT", "REVOKE") and act["scope"]["kind"] == "CASE":
        act["scope"]["caseReference"] = fold_reference(act["scope"]["caseReference"])
    return result


def identity_vectors(sources, case_format, authority_format):
    result = []
    for item in sources["canonicalIdentity"]:
        version = case_format if item["left"]["kind"] == "CASE" else authority_format
        authored_left = with_format(item["left"], version)
        authored_right = with_format(item["right"], version)
        left = canonical_identity(authored_left)
        right = canonical_identity(authored_right)
        same = canonical(left) == canonical(right)
        assert same == item["same"], item["name"]
        result.append({"name": item["name"], "scope": item["left"]["kind"], "same": same,
                       "left": vector("authored left", authored_left), "right": vector("authored right", authored_right),
                       "canonicalLeft": vector("identity left", left), "canonicalRight": vector("identity right", right)})
    return result


def case_id(installation, reference):
    folded = fold_reference(reference)
    digest = hashlib.sha256(REFERENCE_TAG + uuid.UUID(installation).bytes
                            + folded.encode("ascii")).digest()
    raw = bytearray(digest[:16])
    raw[6] = (raw[6] & 0x0F) | 0x80
    raw[8] = (raw[8] & 0x3F) | 0x80
    return str(uuid.UUID(bytes=bytes(raw)))


def valid_reference(reference):
    return 1 <= len(reference) <= 80 and all(0x21 <= ord(c) <= 0x7E for c in reference)


def frame(request, snapshot):
    return (len(request).to_bytes(4, "big") + request + len(snapshot).to_bytes(4, "big")
            + snapshot)


def micros(instant):
    parsed = datetime.datetime.fromisoformat(instant)
    epoch = datetime.datetime(1970, 1, 1, tzinfo=datetime.timezone.utc)
    delta = parsed - epoch
    return (delta.days * 86400 + delta.seconds) * 1_000_000 + delta.microseconds


def day_number(text):
    return datetime.date.fromisoformat(text).toordinal() - 1 if text else 0


def ledger(sources, requests, snapshot, authority):
    installation = sources["caseReferenceIds"]["installationId"]
    previous = bytes(32)
    vectors = []
    for spec in sources["ledgerEntries"]:
        if spec["scope"] == "CASE":
            request_bytes = canonical(requests[0]).encode("utf-8")
            snapshot_bytes = canonical(snapshot).encode("utf-8")
            subject = case_id(installation, requests[0]["caseReference"])
            scope_byte, revision = 1, spec["caseRevision"]
        else:
            request_bytes = canonical(authority).encode("utf-8")
            snapshot_bytes = b""
            subject = spec["subjectId"]
            scope_byte, revision = 2, 0
        payload = frame(request_bytes, snapshot_bytes)
        nonce = bytes.fromhex(spec["payloadNonce"])
        commitment = hashlib.sha256(nonce + payload).digest()
        preimage = b"".join([
            ENTRY_TAG,
            uuid.UUID(spec["installationId"]).bytes,
            spec["sequence"].to_bytes(8, "big"),
            previous,
            bytes([scope_byte, ACT_CODES[spec["act"]]]),
            uuid.UUID(spec["operationId"]).bytes,
            uuid.UUID(spec["actorId"]).bytes,
            micros(spec["recordedAt"]).to_bytes(8, "big", signed=True),
            uuid.UUID(subject).bytes,
            revision.to_bytes(8, "big", signed=True),
            day_number(spec["businessDate"]).to_bytes(4, "big", signed=True),
            commitment,
        ])
        assert len(preimage) == 183, len(preimage)
        entry_hash = hashlib.sha256(preimage).digest()
        vectors.append({
            "name": spec["name"], "installationId": spec["installationId"],
            "sequence": spec["sequence"], "previousHash": previous.hex(),
            "scope": spec["scope"], "act": spec["act"], "actCode": ACT_CODES[spec["act"]],
            "operationId": spec["operationId"], "actorId": spec["actorId"],
            "recordedAt": spec["recordedAt"], "recordedAtUnixMicroseconds": micros(spec["recordedAt"]),
            "subjectId": subject, "caseRevision": spec["caseRevision"],
            "businessDate": spec["businessDate"],
            "businessDateDayNumber": day_number(spec["businessDate"]),
            "payloadNonce": spec["payloadNonce"], "payload": payload.hex(),
            "payloadCommitment": commitment.hex(), "preimage": preimage.hex(),
            "entryHash": entry_hash.hex(),
        })
        previous = entry_hash
    return {"entryFormat": 3, "tag": ENTRY_TAG.decode("ascii"), "preimageBytes": 183,
            "actCodes": ACT_CODES, "vectors": vectors}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--request", type=int, required=True)
    parser.add_argument("--snapshot", type=int, required=True)
    parser.add_argument("--authority", type=int, required=True)
    parser.add_argument("--sources", default=str(pathlib.Path(__file__).with_name("sources.json")))
    arguments = parser.parse_args()
    sources = json.loads(pathlib.Path(arguments.sources).read_text(encoding="utf-8"))
    requests = [with_format(item["value"], arguments.request) for item in sources["caseRequests"]]
    snapshot = with_format(sources["snapshot"]["value"], arguments.snapshot)
    authority = with_format(sources["authorityRecord"]["value"], arguments.authority)
    installation = sources["caseReferenceIds"]["installationId"]
    for refused in sources["caseReferenceIds"]["refused"]:
        assert not valid_reference(refused), refused
    document = {
        "purpose": "Independent Python oracle for ClaimCore durable formats; regenerate with "
                   "python3 -I generate_vectors.py",
        "jcs": [vector(item["name"], item["value"]) for item in sources["jcs"]],
        "jcsRefusals": sources["jcsRefusals"],
        "caseRequests": [vector(item["name"], value)
                         for item, value in zip(sources["caseRequests"], requests)],
        "canonicalIdentity": identity_vectors(sources, arguments.request, arguments.authority),
        "snapshot": vector(sources["snapshot"]["name"], snapshot),
        "authorityRecord": vector(sources["authorityRecord"]["name"], authority),
        "caseReferenceIds": {
            "installationId": installation,
            "accepted": [{"reference": reference, "caseId": case_id(installation, reference)}
                         for reference in sources["caseReferenceIds"]["accepted"]],
            "refused": sources["caseReferenceIds"]["refused"],
        },
        "ledgerPayloads": {
            "caseRequest": canonical(requests[0]),
            "caseSnapshot": canonical(snapshot),
            "authorityRequest": canonical(authority),
        },
        "ledgerEntries": ledger(sources, [requests[0]],
                                snapshot,
                                authority),
    }
    print(json.dumps(document, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
