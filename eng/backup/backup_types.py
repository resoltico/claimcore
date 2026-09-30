"""Type names shared by the backup tooling."""

from typing import Any

# A decoded JSON value. Every reader validates the exact shape it needs before use.
type Json = Any
# A decoded JSON object.
type JsonObject = dict[str, Any]
