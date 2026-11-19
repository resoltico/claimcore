import { expect, it } from "vitest";
import { coreLinkPath } from "../../../src/routing/coreLinks";
import {
  listRequest,
  parseWorklistQuery,
  repairWorklistQuery,
  worklistPath,
} from "../../../src/routing/worklistQuery";

const admit = (query: string) => {
  const result = parseWorklistQuery(query);
  if (result.kind !== "valid") {
    throw new Error(`Expected an admitted Cases URL: ${query}`);
  }
  return result.selection;
};

it("forwards a core view without constructing competing lifecycle or progress predicates", () => {
  const path = worklistPath({ view: "UNDECIDED" });
  expect(path).toBe("/cases?view=UNDECIDED");
  expect(listRequest(admit("?view=UNDECIDED"), null)).toEqual({ limit: 50, view: "UNDECIDED" });
  expect(listRequest({ view: "DECIDED_UNPAID" }, null)).toEqual({
    limit: 50,
    view: "DECIDED_UNPAID",
  });
});

it("admits independent filters, UTF-8 search with a literal plus and an explicit scalar default", () => {
  const query = admit(
    "?view=ALL&lifecycle=ACTIVE&lifecycle=VOIDED&status=OPENED&status=CLOSED&progress=PAID&q=%D8%B9%2B%25&order=REFERENCE&receivedFrom=2024-02-29",
  );
  expect(listRequest(query, "opaque-page")).toEqual({
    limit: 50,
    view: "ALL",
    lifecycle: ["ACTIVE", "VOIDED"],
    status: ["OPENED", "CLOSED"],
    progress: ["PAID"],
    text: "ع+%",
    order: "REFERENCE",
    receivedFrom: "2024-02-29",
    cursor: "opaque-page",
  });
  expect(admit("?q=&view=MINE")).toEqual({ view: "MINE" });
});

it.each([
  "order=unknown",
  "q=invalid%ZZ",
  "q=%C0%AF",
  "q=%ED%A0%80",
  "q=a%00b",
  "q=+whitespace",
  "q=a%E2%80%8Bb",
  `q=${"a".repeat(101)}`,
  "receivedFrom=2026-02-29",
  "receivedFrom=2026-2-01",
  "responsibleActor=invalid",
  "responsibleActor=00000000-0000-0000-0000-000000000000",
  "assignment=",
  "receivedForm=2026-02-01",
  "text=unrecognized",
  "cursor=opaque",
  "limit=10",
  "view=ALL&view=ALL",
  "view=ALL&view=NEEDS_ATTENTION",
  "status=OPENED&status=BAD",
  "lifecycle=ACTIVE&lifecycle=ACTIVE&lifecycle=ACTIVE&lifecycle=ACTIVE",
])("rejects %s before exposing a partial selection", (invalid) => {
  expect(parseWorklistQuery(`?${invalid}`).kind).toBe("invalid");
});

it("repairs the upper field without rewriting unrelated authored fragments", () => {
  const url =
    "?view=ALL&status=OPENED&status=CLOSED&q=%D8%B9%2b&receivedFrom=2026-02-02&receivedThrough=2026-02-01";
  const result = parseWorklistQuery(url);
  if (result.kind !== "invalid") {
    throw new Error("A reversed range must await an explicit repair.");
  }
  const issue = result.issues[0]!;
  expect(issue.upper).toBe("receivedThrough");
  const updated = repairWorklistQuery(result.fragments, issue, ["2026-02-02"]);
  expect(updated).toBe(
    "/cases?view=ALL&status=OPENED&status=CLOSED&q=%D8%B9%2b&receivedFrom=2026-02-02&receivedThrough=2026-02-02",
  );
  expect(admit(updated.slice("/cases".length))).toEqual({
    view: "ALL",
    status: ["OPENED", "CLOSED"],
    text: "ع+",
    receivedFrom: "2026-02-02",
    receivedThrough: "2026-02-02",
  });
});

it("adapts typed core links while omitting paging metadata", () => {
  const actor = "10000000-0000-4000-8000-000000000001";
  const selection = {
    view: "ALL",
    status: ["CLOSED", "OPENED"],
    lifecycle: ["ACTIVE", "VOIDED"],
    order: "REFERENCE",
    responsibleActor: actor,
    receivedFrom: "2026-02-01",
    inactiveSince: "2026-02-28",
    text: "ع+A",
  };
  const path = coreLinkPath({
    endpoint: "case.list",
    input: { ...selection, limit: 19, cursor: "opaque" },
  });
  expect(path).toBe(
    `/cases?view=ALL&status=CLOSED&status=OPENED&lifecycle=ACTIVE&lifecycle=VOIDED&q=%D8%B9%2BA&order=REFERENCE&responsibleActor=${actor}&receivedFrom=2026-02-01&inactiveSince=2026-02-28`,
  );
  expect(admit(new URL(path!, "https://claimcore.example").search)).toEqual(selection);
});
