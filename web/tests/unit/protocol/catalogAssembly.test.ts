import { inventory } from "../../support/inventory";
import { resolve } from "node:path";
import { readFileSync } from "node:fs";
import { expect, it } from "vitest";
import { productMetadata } from "../../../src/protocol/productMetadata";
import { assembleCatalogInput, atPath } from "../../../src/protocol/catalogAssembly";
import type { FormValues } from "../../../src/protocol/forms";

it("assembles all inventoried captured frames including reads and selected correction branches", () => {
  const rows = readFileSync(resolve("../tests/fixtures/cli-frames.tsv"), "utf8")
    .trimEnd()
    .split("\n");
  expect(rows).toHaveLength(inventory.cliFrameCount);
  for (const row of rows) {
    const [arguments_, expectedJson] = row.split("\t");
    const expected = JSON.parse(expectedJson!) as {
      endpoint: string;
      input: Record<string, unknown>;
    };
    const act = atPath(expected.input, ["act", "kind"]);
    const subject = expected.input["kind"];
    const entry = productMetadata.catalog.entries.find((item) =>
      act === undefined
        ? item.endpoint === expected.endpoint
        : item.subject === subject && item.kind === act,
    )!;
    const values: FormValues = Object.fromEntries(
      entry.inputs.flatMap((input) => {
        const value = atPath(expected.input, input.path);
        return value === undefined
          ? []
          : [
              [
                input.path.join("."),
                typeof value === "number" ? String(value) : (value as string | boolean),
              ],
            ];
      }),
    );
    expect(assembleCatalogInput(entry, values), arguments_).toEqual(expected.input);
  }
});

it("omits absent optional read inputs and uses catalog defaults", () => {
  const entry = productMetadata.catalog.entries.find((item) => item.endpoint === "case.list")!;
  expect(assembleCatalogInput(entry, {})).toEqual({ limit: 50 });
  expect(assembleCatalogInput(entry, { cursor: "", limit: "5" })).toEqual({
    limit: 5,
  });
});

it("assembles finite worklist sets and omits empty optional sets", () => {
  const entry = productMetadata.catalog.entries.find((item) => item.endpoint === "case.list")!;
  expect(
    assembleCatalogInput(entry, {
      lifecycle: ["ACTIVE", "VOIDED"],
      status: ["OPENED"],
      progress: ["DECIDED"],
      text: "Rīga",
      order: "RECENT",
    }),
  ).toEqual({
    limit: 50,
    lifecycle: ["ACTIVE", "VOIDED"],
    status: ["OPENED"],
    progress: ["DECIDED"],
    text: "Rīga",
    order: "RECENT",
  });
  expect(assembleCatalogInput(entry, { lifecycle: [], status: [], progress: [] })).toEqual({
    limit: 50,
  });
  expect(assembleCatalogInput(entry, { order: "REFERENCE" })).toEqual({ limit: 50 });
  expect(assembleCatalogInput(entry, { order: "RECENT" })).toEqual({ limit: 50, order: "RECENT" });
});
