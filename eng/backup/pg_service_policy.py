"""Exact private libpq profiles; remote backup endpoints require verified TLS."""

import configparser
import hashlib
import os
import re
import stat

from deployment_common import private_path, require


def _digest(value):
    return isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) is not None


def _profile(parser, name):
    require(parser.has_section(name), "pgservice-profile-missing")
    values = dict(parser.items(name))
    require(
        set(values)
        <= {
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
    require(1 <= int(values["port"]) <= 65535, "pgservice-port")
    require(
        re.fullmatch(r"[A-Za-z_][A-Za-z0-9_-]{0,63}", values.get("user", "")),
        "pgservice-user",
    )
    return values


def _cluster(config, parser, cluster, remote):
    names = config[cluster]
    metadata = _profile(parser, names["metadataService"])
    replication = _profile(parser, names["replicationService"])
    require(
        names["metadataService"] != names["replicationService"],
        "pgservice-role-separation",
    )
    database = metadata.get("dbname", "")
    # The isolated primitive drill uses postgres; the fenced fixture uses _test.
    require(
        (database.endswith("_test") or database == "postgres")
        if not remote
        else bool(database),
        "pgservice-database",
    )
    require("dbname" not in replication, "pgservice-replication-database")
    for name in ("host", "port"):
        require(
            metadata.get(name) == replication.get(name), "pgservice-cluster-endpoint"
        )
    if remote:
        for values in (metadata, replication):
            host = values.get("host", "")
            require(
                re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9.-]{0,252}", host)
                and host not in ("localhost", "127.0.0.1", "::1")
                and values.get("sslmode") == "verify-full",
                "pgservice-tls-required",
            )
            ca = private_path(values.get("sslrootcert", ""))
            require(
                _digest(config["pgTlsRootSha256"][cluster])
                and hashlib.sha256(ca.read_bytes()).hexdigest()
                == config["pgTlsRootSha256"][cluster],
                "pgservice-ca-pin",
            )
        require(
            metadata["sslrootcert"] == replication["sslrootcert"],
            "pgservice-cluster-ca",
        )
    else:
        for values in (metadata, replication):
            require(
                values.get("host") in ("localhost", "127.0.0.1"),
                "pgservice-synthetic-loopback",
            )


def validate(config):
    raw = config["pgServiceFile"]
    source = private_path(raw)
    try:
        descriptor = os.open(source, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC)
    except OSError:
        require(False, "pgservice-private-file")
    try:
        status = os.fstat(descriptor)
        require(
            stat.S_ISREG(status.st_mode)
            and status.st_nlink == 1
            and status.st_uid == os.geteuid()
            and status.st_mode & 0o077 == 0,
            "pgservice-private-file",
        )
        require(status.st_size <= 32768, "pgservice-size")
        with os.fdopen(descriptor, "rb", closefd=False) as stream:
            document = stream.read(32769)
        require(len(document) <= 32768, "pgservice-size")
    finally:
        os.close(descriptor)
    require(_digest(config["pgServiceFileSha256"]), "pgservice-digest")
    require(
        hashlib.sha256(document).hexdigest() == config["pgServiceFileSha256"],
        "pgservice-digest",
    )
    ambient = os.environ.get("PGSERVICEFILE")
    require(
        ambient is None or os.path.abspath(ambient) == str(source),
        "pgservice-ambient-mismatch",
    )
    parser = configparser.ConfigParser(interpolation=None, strict=True)
    parser.optionxform = str
    try:
        parser.read_string(document.decode("utf-8"))
    except (configparser.Error, UnicodeError):
        require(False, "pgservice-format")
    require(not parser.defaults(), "pgservice-defaults")
    expected = {
        config[cluster][name]
        for cluster in ("primary", "witness")
        for name in ("metadataService", "replicationService")
    }
    require(
        set(parser.sections()) == expected and len(expected) == 4, "pgservice-inventory"
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
        _cluster(config, parser, cluster, remote)
    os.environ["PGSERVICEFILE"] = str(source)
    return source
