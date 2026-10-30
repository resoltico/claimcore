"""Narrow, recorded harness adaptations for source-stable comparison checkouts."""

import difflib


def replace_once(text, before, after):
    if text.count(before) != 1:
        raise ValueError(f"Unsupported historical harness: expected one {before!r}")
    return text.replace(before, after, 1)


def prepare(root, owner):
    changes = []

    def update(relative, transform):
        path = root / relative
        original = path.read_text()
        changed = transform(original)
        path.write_text(changed)
        changes.extend(difflib.unified_diff(
            original.splitlines(True), changed.splitlines(True),
            fromfile="a/" + relative, tofile="b/" + relative,
        ))

    # Both revisions get the same ownership label. It changes no workload or product code.
    update("tests/ClaimCore.TestSupport/PostgresFixture.fs", lambda text: replace_once(
        text, '.WithTmpfsMount("/var/lib/postgresql")',
        f'.WithLabel("claimcore.growth-compare", "{owner}")\n'
        '            .WithTmpfsMount("/var/lib/postgresql")',
    ))
    modern = (root / "tests/ClaimCore.IntegrationTests/GrowthWorkloadPlan.fs").exists()
    reads = (root / "tests/ClaimCore.IntegrationTests/GrowthReadWorkloads.fs").exists()
    if not modern:
        def qualification(text):
            selector = '''let private comparison =
    match Environment.GetEnvironmentVariable "CLAIMCORE_GROWTH_COMPARISON" with
    | null -> false
    | "1" -> true
    | _ -> invalidOp "CLAIMCORE_GROWTH_COMPARISON must be unset or 1."

'''
            text = replace_once(text, "let private cardinalityWrites", selector + "let private cardinalityWrites")
            text = replace_once(text, "| None -> required", "| None -> if comparison then [ 10000, 1; 100000, 1; 100000, 16 ] else required")
            text = replace_once(text, "for cases in [ 1; 100; 1000 ] do", "for cases in (if comparison then [ 1000 ] else [ 1; 100; 1000 ]) do")
            if reads:
                text = replace_once(text, "if selected.Length = 9 then", "if selected.Length = 9 || comparison then")
                text = replace_once(text, "                    6\n", "                    (if comparison then 2 else 6)\n")
                text = replace_once(text, "requiredReadProfiles = 6", "requiredReadProfiles = (if comparison then 2 else 6)")
            return text
        update("tests/ClaimCore.IntegrationTests/GrowthQualificationTests.fs", qualification)
        if reads:
            update("tests/ClaimCore.IntegrationTests/GrowthReadWorkloads.fs", lambda text: replace_once(
                text, "for cases in [ 1000; 10000; 100000 ] do",
                'for cases in (if Environment.GetEnvironmentVariable "CLAIMCORE_GROWTH_COMPARISON" = "1" then [ 100000 ] else [ 1000; 10000; 100000 ]) do',
            ))
    return {"historical": not modern, "hasReadWorkloads": reads, "patch": "".join(changes)}
