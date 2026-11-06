import { isResponse } from "../../src/protocol/client";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { expect, it } from "vitest";
import { assertUniqueCorpusIdentities, corpusCases } from "../support/contract-corpus.fixtures";
import { positiveValue, workBasis } from "../support/inputs";

it("passes every authored wire example through unchanged, including positive frames", () => {
  const cases = corpusCases();
  for (const direction of ["request", "response"] as const) {
    const file = JSON.parse(
      readFileSync(resolve(`../tests/fixtures/wire-${direction}-examples.json`), "utf8"),
    ) as { cases: ReadonlyArray<{ name: string; id?: string; value: unknown }> };
    const actual = cases.filter((item) => item.direction === direction);
    expect(actual).toHaveLength(file.cases.length);
    for (const authored of file.cases) {
      expect(
        actual.find((item) => item.id === (authored.id ?? authored.name))?.value,
      ).toStrictEqual(authored.value);
    }
  }
});
it("rejects duplicate authored identities without changing or collapsing their values", () => {
  const [original] = corpusCases();
  if (original === undefined) {
    throw new Error("An authored corpus case is required.");
  }
  const duplicate = { ...original, value: { distinct: "raw negative control" } };
  expect(() => {
    assertUniqueCorpusIdentities([original, duplicate]);
  }).toThrow("Duplicate authored corpus identity");
  expect(duplicate.value).toStrictEqual({ distinct: "raw negative control" });
  expect(() => {
    assertUniqueCorpusIdentities([
      original,
      { ...duplicate, direction: original.direction === "request" ? "response" : "request" },
    ]);
  }).not.toThrow();
});
it("completes a nested receipt without adding observation metadata to its activity header", () => {
  const position = { sequence: 5, entryHash: "a".repeat(64) };
  const identity = {
    operationId: "10000000-0000-4000-8000-000000000001",
    position,
    recordedAt: "2026-08-31T04:00:00.000000Z",
    recordedBy: {
      actorId: "40000000-0000-4000-8000-000000000001",
      displayName: { kind: "NAMED", value: "Recorder" },
      principalKind: "HUMAN",
      availability: "AVAILABLE",
    },
  };
  const subject = { kind: "CASE", revision: 2, act: "CLOSE" };
  const receipt = {
    ...identity,
    subject: {
      ...subject,
      caseId: "20000000-0000-4000-8000-000000000001",
      caseReference: "CASE-1",
      snapshot: { kind: "WITHHELD" },
    },
  };
  const input = { ...identity, subject, detail: receipt };
  const completed = positiveValue(input) as Record<string, unknown>;
  expect(completed).not.toHaveProperty("basis");
  expect(completed["detail"]).toHaveProperty("basis", workBasis);
  expect(completed["recordedAt"]).toBe("2026-08-31T04:00:00.000000Z");
  expect(
    isResponse("case.activity", {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          caseId: "20000000-0000-4000-8000-000000000001",
          caseReference: "CASE-1",
          items: [completed],
          nextCursor: null,
        },
      },
    }),
  ).toBe(true);
});
