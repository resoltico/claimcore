"""Exact private libpq profiles; remote backup endpoints require verified TLS."""

import configparser
import hashlib
import os
import re
import stat
from configparser import ConfigParser
from pathlib import Path

from backup_types import JsonObject
from deployment_common import is_sha256, private_path, refuse, require

PROFILE_KEYS = {
    "host",
    "port",
    "user",
    "password",
    "passfile",
    "dbname",
    "sslmode",
    "sslrootcert",
    "sslcert",
    "sslkey",
}
SERVICE_FILE_LIMIT = 32768
MAX_PORT = 65535
EXPECTED_SERVICES = 4
LOOPBACK = ("localhost", "127.0.0.1")


def _profile(parser: ConfigParser, name: str) -> dict[str, str]:
    require(parser.has_section(name), "pgservice-profile-missing")
    values = dict(parser.items(name))
    require(
        set(values) <= PROFILE_KEYS
        and all(value and "\n" not in value for value in values.values()),
        "pgservice-profile-shape",
    )
    require("password" in values or "passfile" in values, "pgservice-credential")
    require(not ("password" in values and "passfile" in values), "pgservice-credential")
    if "passfile" in values:
        private_path(values["passfile"])
    for certificate_field in ("sslcert", "sslkey"):
        if certificate_field in values:
            private_path(values[certificate_field])
    require(re.fullmatch(r"[0-9]{1,5}", values.get("port", "")), "pgservice-port")
    require(1 <= int(values["port"]) <= MAX_PORT, "pgservice-port")
    require(
        re.fullmatch(r"[A-Za-z_][A-Za-z0-9_-]{0,63}", values.get("user", "")),
        "pgservice-user",
    )
    return values


def _check_remote_tls(
    config: JsonObject, cluster: str, profiles: tuple[dict[str, str], ...]
) -> None:
    for values in profiles:
        host = values.get("host", "")
        require(
            re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", host)
            and host not in ("localhost", "127.0.0.1", "::1")
            and values.get("sslmode") == "verify-full",
            "pgservice-tls-required",
        )
        ca = private_path(values.get("sslrootcert", ""))
        require(
            is_sha256(config["pgTlsRootSha256"][cluster])
            and hashlib.sha256(ca.read_bytes()).hexdigest() == config["pgTlsRootSha256"][cluster],
            "pgservice-ca-pin",
        )
    require(
        profiles[0]["sslrootcert"] == profiles[1]["sslrootcert"],
        "pgservice-cluster-ca",
    )


def _cluster(config: JsonObject, parser: ConfigParser, cluster: str, *, remote: bool) -> None:
    names = config[cluster]
    metadata = _profile(parser, names["metadataService"])
    replication = _profile(parser, names["replicationService"])
    require(names["metadataService"] != names["replicationService"], "pgservice-role-separation")
    database = metadata.get("dbname", "")
    # The isolated primitive drill uses postgres; the fenced fixture uses _test.
    require(
        (database.endswith("_test") or database == "postgres") if not remote else bool(database),
        "pgservice-database",
    )
    require("dbname" not in replication, "pgservice-replication-database")
    for name in ("host", "port"):
        require(metadata.get(name) == replication.get(name), "pgservice-cluster-endpoint")
    if remote:
        _check_remote_tls(config, cluster, (metadata, replication))
    else:
        for values in (metadata, replication):
            require(values.get("host") in LOOPBACK, "pgservice-synthetic-loopback")


def _read_service_file(source: Path) -> bytes:
    try:
        descriptor = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    except OSError:
        refuse("pgservice-private-file")
    try:
        status = os.fstat(descriptor)
        require(
            stat.S_ISREG(status.st_mode)
            and status.st_nlink == 1
            and status.st_uid == os.geteuid()
            and status.st_mode & 0o077 == 0,
            "pgservice-private-file",
        )
        require(status.st_size <= SERVICE_FILE_LIMIT, "pgservice-size")
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            document = stream.read(SERVICE_FILE_LIMIT + 1)
        require(len(document) <= SERVICE_FILE_LIMIT, "pgservice-size")
    finally:
        os.close(descriptor)
    return document


class _CaseSensitiveParser(ConfigParser):
    """A parser that keeps option names exactly as written."""

    def optionxform(self, optionstr: str) -> str:
        return optionstr


def _parse(document: bytes) -> ConfigParser:
    parser = _CaseSensitiveParser(interpolation=None, strict=True)
    try:
        parser.read_string(document.decode("utf-8"))
    except (configparser.Error, UnicodeError):
        refuse("pgservice-format")
    require(not parser.defaults(), "pgservice-defaults")
    return parser


def validate(config: JsonObject) -> Path:
    """Validate the private service file and every cluster profile; export it as PGSERVICEFILE."""
    source = private_path(config["pgServiceFile"])
    document = _read_service_file(source)
    require(is_sha256(config["pgServiceFileSha256"]), "pgservice-digest")
    require(
        hashlib.sha256(document).hexdigest() == config["pgServiceFileSha256"], "pgservice-digest"
    )
    ambient = os.environ.get("PGSERVICEFILE")
    require(
        ambient is None or os.path.normpath(Path.cwd() / ambient) == str(source),
        "pgservice-ambient-mismatch",
    )
    parser = _parse(document)
    expected = {
        config[cluster][name]
        for cluster in ("primary", "witness")
        for name in ("metadataService", "replicationService")
    }
    require(
        set(parser.sections()) == expected and len(expected) == EXPECTED_SERVICES,
        "pgservice-inventory",
    )
    remote = config["checkpointSignerMode"] == "REMOTE_SSH"
    require(
        (
            isinstance(config["pgTlsRootSha256"], dict)
            and set(config["pgTlsRootSha256"]) == {"primary", "witness"}
        )
        if remote
        else config["pgTlsRootSha256"] is None,
        "pgservice-tls-policy",
    )
    for cluster in ("primary", "witness"):
        _cluster(config, parser, cluster, remote=remote)
    os.environ["PGSERVICEFILE"] = str(source)
    return source
