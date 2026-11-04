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
        : item.subject === subject &&
          item.constants.some(
            (constant) => constant.path.join(".") === "act.kind" && constant.value === act,
          ),
    )!;
    const values: FormValues = Object.fromEntries([
      ...entry.inputs.flatMap((input) => {
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
      ...entry.collections.map((collection) => {
        const collectionRows = atPath(expected.input, collection.path) as ReadonlyArray<unknown>;
        return [
          collection.path.join("."),
          {
            kind: "COLLECTION",
            rows: collectionRows.map((collectionRow, index) => ({
              key: String(index),
              values: Object.fromEntries(
                collection.rowInputs.flatMap((input) => {
                  const value = atPath(collectionRow, input.path);
                  return value === undefined ? [] : [[input.path.join("."), value]];
                }),
              ),
            })),
          },
        ];
      }),
    ]) as FormValues;
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

it("keeps optional narrative omission, explicit clear and exact authored text distinct", () => {
  const entry = productMetadata.catalog.entries.find(
    (item) => item.subject === "CASE" && item.kind === "AMEND_REGISTRATION",
  )!;
  const description = "act.values.incidentDescription";
  const omitted = assembleCatalogInput(entry, {});
  expect(atPath(omitted, ["act", "values", "incidentDescription"])).toBeUndefined();
  const cleared = assembleCatalogInput(entry, { [description]: null });
  expect(atPath(cleared, ["act", "values", "incidentDescription"])).toBeNull();
  const exact = "  العربية\n\n😀  ";
  expect(
    atPath(assembleCatalogInput(entry, { [description]: exact }), [
      "act",
      "values",
      "incidentDescription",
    ]),
  ).toBe(exact);
  expect(
    atPath(assembleCatalogInput(entry, { [description]: "" }), [
      "act",
      "values",
      "incidentDescription",
    ]),
  ).toBe("");
});
it("a work summary clear is an independent work command with no financial body", () => {
  const entry = productMetadata.catalog.entries.find(
    (item) => item.subject === "WORK" && item.kind === "UPDATE_WORK",
  )!;
  const input = assembleCatalogInput(entry, {
    "act.values.handlingSummary": null,
    expectedRevision: "7",
    "target.caseReference": "WORK-1",
    operationId: "10000000-0000-4000-8000-000000000001",
  });
  expect(input).toEqual({
    kind: "WORK",
    operationId: "10000000-0000-4000-8000-000000000001",
    target: { kind: "REFERENCE", caseReference: "WORK-1" },
    expectedRevision: 7,
    act: { kind: "UPDATE_WORK", values: { handlingSummary: null } },
  });
});

it("assembles atomic admission rows with their own scope branches and preserves authored bytes", () => {
  const entry = productMetadata.catalog.entries.find((item) => item.kind === "ADMIT")!;
  const input = assembleCatalogInput(entry, {
    "target.kind": "HUMAN",
    "target.issuer": "https://identity.invalid",
    "target.subject": "person",
    "act.profile.displayName": "Anna Ozola",
    "act.profile.contact": "anna@example.invalid",
    "act.grants": {
      kind: "COLLECTION",
      rows: [
        {
          key: "owner",
          values: {
            role: "OWNER",
            "scope.kind": "INSTALLATION",
            "scope.target.caseReference": "ignored",
          },
        },
        {
          key: "reader",
          values: {
            role: "CASE_READER",
            "scope.kind": "CASE",
            "scope.target.caseReference": "  Exact/مرجع  ",
          },
        },
      ],
    },
  });
  expect(input["act"]).toEqual({
    kind: "ADMIT",
    profile: { displayName: "Anna Ozola", contact: "anna@example.invalid" },
    grants: [
      { role: "OWNER", scope: { kind: "INSTALLATION" } },
      {
        role: "CASE_READER",
        scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "  Exact/مرجع  " } },
      },
    ],
  });
  expect(
    assembleCatalogInput(entry, { "act.grants": { kind: "COLLECTION", rows: [] } })["act"],
  ).toMatchObject({ grants: [] });
});
