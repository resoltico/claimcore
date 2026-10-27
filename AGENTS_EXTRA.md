# Changelog rules

- Keep release summaries in CHANGELOG.md; derive published summaries from it and leave unrelated history unchanged.
- Unreleased covers implemented changes cumulatively against the preceding published non-prerelease version, including relevant uncommitted work.
- Verify publication; headings, tags and workflow status are insufficient, and prereleases do not reset that baseline.
- Check claims and omissions against implementation, contracts, tests and documentation; commits and plans are leads.
- Describe consequences and required action for users, operators, integrators and contributors, including compatibility effects and limitations.
- Exclude development chronology, superseded changes and verification logs.
- Use concise bullets without category labels or subheadings; group related changes and put breaks and required action first.
- Each bullet is one physical line, at most 200 characters excluding its `- ` marker; shorten or split without losing consequences.
- Use technical names where useful and link existing relevant documentation; do not invent paths or documents for this rule.
- Keep Unreleased above dated version headings, newest first, and retain release-note links.
- Review accuracy, completeness, duplication, compatibility and format; report unresolved evidence gaps outside CHANGELOG.md and run applicable checks.
- Versioning, dating and publication require release authorization.
