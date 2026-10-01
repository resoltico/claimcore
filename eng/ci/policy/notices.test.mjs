import assert from "node:assert/strict";
import test from "node:test";
import { renderNotices } from "./notices.mjs";

/** @param {string} file */
const license = (file) => `text of ${file}\n`;

/**
 * @param {string} name
 * @param {string[]} ids
 * @param {string} [version]
 */
const component = (name, ids, version = "1.0.0") => ({
  name,
  version,
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
        component("Npgsql", ["PostgreSQL"]),
      ],
    },
    license,
  );
  assert.ok(text.indexOf("Package: Alpha@") < text.indexOf("Package: Zeta@"));
  assert.ok(!text.includes("ClaimCore.Domain"));
  assert.equal(text.match(/text of MIT-DOTNET\.txt/gu)?.length, 1);
  assert.match(text, /text of PostgreSQL-NPGSQL\.txt/u);
  assert.match(text, /Upstream: https:\/\/example\.test\/Alpha/u);
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
    () => renderNotices({ components: [component("ClaimCore.X", ["MIT"])] }, license),
    /no third-party/u,
  );
  assert.match(
    renderNotices({ components: [component("libsodium", ["ISC"])] }, license),
    /ISC-LIBSODIUM/u,
  );
});
