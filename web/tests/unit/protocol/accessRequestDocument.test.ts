import { expect, it } from "vitest";
import { isResponse } from "@contracts/client";

const key = "request-row";
const handle = {
  key,
  principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "opaque-submit-target" },
  profileSeed: { displayName: "Unverified editable hint", contact: "contact@example.test" },
};
const document = {
  screen: {
    regions: [
      {
        role: "RESULTS",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "caption",
              labelId: "label.requestedName",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [{ key, values: [{ kind: "TEXT", value: "Opaque core request caption Ω" }] }],
        },
      },
    ],
  },
  context: { kind: "ACCESS_REQUESTS", nextCursor: "next", requests: [handle] },
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
  disclosure: "CURRENT",
};
const reply = (value: unknown) => ({
  representation: "HUMAN",
  outcome: { kind: "SUCCEEDED", document: value },
});

it("admits typed request identities and unverified editable hints independently of opaque captions", () => {
  expect(isResponse("authority.requests", reply(document))).toBe(true);
  expect(isResponse("authority.list", reply(document))).toBe(false);
});

it("refuses missing, duplicate or unbound request handles", () => {
  for (const requests of [[], [handle, handle], [{ ...handle, key: "unbound-row" }]]) {
    expect(
      isResponse(
        "authority.requests",
        reply({ ...document, context: { ...document.context, requests } }),
      ),
    ).toBe(false);
  }
});

it("refuses request identities and input seeds after disclosure tightens", () => {
  expect(isResponse("authority.requests", reply({ ...document, disclosure: "WITHHELD" }))).toBe(
    false,
  );
});
