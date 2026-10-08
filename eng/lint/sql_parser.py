"""Expose native PostgreSQL syntax trees; policy decisions belong to the typed Node gate."""

import json
import sys
from typing import cast

from pglast import ast, parse_plpgsql, parse_sql, scan
from pglast.parser import get_postgresql_version, parse_sql_json
from pglast.stream import RawStream


def project_type(type_name: ast.TypeName) -> None:
    """Replace only declared custom parameter/return types for procedural syntax parsing.

    libpg_query cannot bind user schemas. The original SQL tree remains authoritative for declared
    types, ranges and arity; real PostgreSQL qualification owns semantic type and field binding.
    """
    names = tuple(item.sval for item in type_name.names or () if isinstance(item, ast.String))
    if len(names) > 1 and names[0] != "pg_catalog":
        type_name.names = (ast.String(sval="pg_catalog"), ast.String(sval="record"))


def byte_tokens(source: str) -> list[list[int | str]]:
    """Align the wrapper scanner's code-point positions with native SQL AST byte offsets."""
    offsets = [0]
    for scalar in source:
        offsets.append(offsets[-1] + len(scalar.encode("utf-8")))
    return [
        [offsets[token.start], offsets[token.end + 1] - 1, token.name, token.kind]
        for token in scan(source)
    ]


def expression_trees(value: object) -> list[object]:
    """Parse embedded expressions with native tokens identifying assignment boundaries."""
    if isinstance(value, list):
        return [tree for child in value for tree in expression_trees(child)]
    if not isinstance(value, dict):
        return []
    if "PLpgSQL_expr" not in value:
        return [tree for child in value.values() for tree in expression_trees(child)]
    expression = value["PLpgSQL_expr"]
    query = expression["query"]
    mode = expression.get("parseMode", 0)
    if mode in {3, 4, 5}:
        assignment = next(
            token for token in scan(query) if token.name in {"COLON_EQUALS", "ASCII_61"}
        )
        query = query[assignment.end + 1 :]
    elif mode not in {0, 2}:
        msg = "Unsupported native expression parse mode."
        raise ValueError(msg)
    return [json.loads(parse_sql_json(query if mode == 0 else "SELECT " + query))]


def function_tree(statement: ast.CreateFunctionStmt) -> dict[str, object]:
    """Return the exact procedural body tree with only custom header types projected."""
    options = statement.options or ()
    language = next(
        item.arg.sval
        for item in options
        if item.defname == "language" and isinstance(item.arg, ast.String)
    )
    for parameter in statement.parameters or ():
        project_type(parameter.argType)
    if statement.returnType:
        project_type(statement.returnType)
    procedural = parse_plpgsql(RawStream()(statement)) if language == "plpgsql" else []
    sql_body = [
        json.loads(parse_sql_json(item.arg[0].sval))
        for item in options
        if language == "sql" and item.defname == "as"
    ]
    return {
        "language": language,
        "procedural": procedural,
        "sqlBody": sql_body,
        "expressions": expression_trees(procedural),
    }


def source_tree(source: str) -> dict[str, object]:
    """Parse original SQL declarations and executable procedural bodies with the native parser."""
    original = json.loads(parse_sql_json(source))
    bodies = []
    for raw in parse_sql(source):
        statement = raw.stmt
        if isinstance(statement, ast.CreateFunctionStmt):
            bodies.append(function_tree(statement))
        elif isinstance(statement, ast.DoStmt):
            procedural = parse_plpgsql(RawStream()(statement))
            bodies.append(
                {
                    "language": "plpgsql",
                    "procedural": procedural,
                    "expressions": expression_trees(procedural),
                }
            )
    return {"sql": original, "bodies": bodies, "tokens": byte_tokens(source)}


def main() -> None:
    """Read source text through stdin and emit only native parser output as JSON."""
    sources = cast("dict[str, str]", json.load(sys.stdin))
    result = {name: source_tree(source) for name, source in sources.items()}
    sys.stdout.write(json.dumps({"version": get_postgresql_version(), "sources": result}))


if __name__ == "__main__":
    main()
