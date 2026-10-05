import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { renderNotices } from "./notices.mjs";

/** @param {import("./notices.mjs").Component} component */
const license = (component) => `text of ${component.licenses?.[0]?.license?.id}.txt\n`;

/**
 * @param {string} name
 * @param {string[]} ids
 * @param {string} [version]
 */
const component = (name, ids, version = "1.0.0") => ({
  name,
  version,
  copyright: `Copyright ${name} owners`,
  licenses: ids.map((id) => ({ license: { id } })),
  externalReferences: [{ type: "website", url: `https://example.test/${name}` }],
});

test("components are grouped under their license text, sorted, excluding ClaimCore itself", () => {
  const text = renderNotices(
    {
      components: [
        component("Zeta", ["MIT"]),
        component("ClaimCore.Domain", ["MIT"]),
        component("Alpha", ["MIT"]),
        component("ClaimCore.ExternalDependency", ["MIT"]),
        component("Npgsql", ["PostgreSQL"]),
      ],
    },
    license,
  );
  assert.ok(text.indexOf("Package: Alpha@") < text.indexOf("Package: Zeta@"));
  assert.ok(!text.includes("ClaimCore.Domain"));
  assert.match(text, /Package: ClaimCore\.ExternalDependency@/u);
  assert.equal(text.match(/text of MIT\.txt/gu)?.length, 1);
  assert.match(text, /text of PostgreSQL\.txt/u);
  assert.match(text, /Upstream: https:\/\/example\.test\/Alpha/u);
  assert.match(text, /Copyright: Copyright Alpha owners/u);
  assert.match(text, /Copyright: Copyright Zeta owners/u);
  assert.ok(text.endsWith("\n"));
});

test("a component without one exact reviewed license is refused", () => {
  const refuse = (/** @type {object} */ item, /** @type {RegExp} */ pattern) =>
    assert.throws(() => renderNotices({ components: [item] }, license), pattern);
  refuse(component("A", []), /lacks one exact/u);
  refuse(component("A", ["MIT", "ISC"]), /lacks one exact/u);
  refuse(component("A", ["GPL-3.0"]), /unreviewed/u);
  refuse(component("A", ["ISC"]), /ISC package identity/u);
  refuse({ name: "A", licenses: [{ license: { id: "MIT" } }] }, /lacks identity/u);
  assert.throws(
    () => renderNotices({ components: [component("ClaimCore.Domain", ["MIT"])] }, license),
    /no third-party/u,
  );
  assert.match(renderNotices({ components: [component("libsodium", ["ISC"])] }, license), /ISC/u);
});

test("project license metadata agrees across .NET and npm without relicensing dependencies", () => {
  const props = readFileSync(new URL("../../../Directory.Build.props", import.meta.url), "utf8");
  const projectLicense = props.match(
    /<PackageLicenseExpression>([^<]+)<\/PackageLicenseExpression>/u,
  )?.[1];
  assert.equal(projectLicense, "MPL-2.0");
  for (const directory of ["eng", "web"]) {
    const base = new URL(`../../../${directory}/`, import.meta.url);
    const manifest = JSON.parse(readFileSync(new URL("package.json", base), "utf8"));
    const lock = JSON.parse(readFileSync(new URL("package-lock.json", base), "utf8"));
    assert.equal(manifest.license, projectLicense);
    assert.equal(lock.packages[""].license, projectLicense);
  }
  const text = renderNotices({ components: [component("Dependency", ["MIT"])] }, license);
  assert.match(text, /source availability.*LICENSE/u);
  assert.match(text, /Declared license: MIT/u);
});

test("redistribution refuses missing attribution and empty package material", () => {
  assert.throws(
    () => renderNotices({ components: [{ ...component("A", ["MIT"]), copyright: "" }] }, license),
    /copyright attribution/u,
  );
  assert.throws(
    () => renderNotices({ components: [component("A", ["MIT"])] }, () => ""),
    /material is empty/u,
  );
});
