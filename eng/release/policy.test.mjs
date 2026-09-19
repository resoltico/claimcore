import assert from "node:assert/strict";
import { test } from "node:test";
import { canonicalNotes, declaredVersion, extractNotes } from "./policy.mjs";
import { body, changelog, props, version } from "./test-support.mjs";

test("extracts one dated section byte-for-byte apart from transport newlines", () => {
  assert.equal(extractNotes(changelog, version), body);
  assert.equal(extractNotes(changelog.replaceAll("\n", "\r\n"), version), body);
  assert.equal(canonicalNotes(`${body}\n\n`), body);
});

for (const [name, source] of [
  ["missing section", changelog.replaceAll("[0.3.0]", "[0.4.0]")],
  ["duplicate section", `${changelog}\n${body}`],
  ["bad date", changelog.replace("2026-09-19", "2026-02-30")],
  ["empty section", "## [0.3.0] - 2026-09-19\n\n### Added\n"],
  ["bare carriage return", changelog.replace("café", "ca\rfé")],
  ["unclosed fence", `${changelog}\n~~~sh\n`],
  ["external reference", `${changelog.replace("Preserve this line.", "Read [guide].")}\n[guide]: https://example.com`],
]) {
  test(`refuses ${name}`, () => assert.throws(() => extractNotes(source, version)));
}

test("does not treat a heading inside a fenced block as a release boundary", () => {
  const source = body.replace("- Preserve this line.", "~~~text\n## [0.3.0] - 2026-09-19\n~~~");
  assert.equal(extractNotes(source, version), source);
});

test("accepts a reference definition retained by the selected section", () => {
  const source = `${body}\n\nRead [guide].\n\n[guide]: https://example.com`;
  assert.equal(extractNotes(source, version), source);
});

test("reads only the literal unconditional Version owner", () => {
  assert.equal(declaredVersion(props), version);
  assert.equal(
    declaredVersion(props.replace("<Project>", "<Project><!-- <Version>9.0.0</Version> -->")),
    version,
  );
});

for (const invalid of [
  props.replace("<Version>", '<Version Condition="true">'),
  props.replace("<PropertyGroup>", '<PropertyGroup Condition="true">'),
  props.replace(version, "$(OtherVersion)"),
  props.replace(version, "0.3.0-rc.1"),
  props.replace("</PropertyGroup>", `<Version>${version}</Version></PropertyGroup>`),
  `<!DOCTYPE Project>${props}`,
  props.replace("<Version>", "<Description><Version>").replace("</Version>", "</Version></Description>"),
]) {
  test("refuses a noncanonical Version declaration", () => assert.throws(() => declaredVersion(invalid)));
}
