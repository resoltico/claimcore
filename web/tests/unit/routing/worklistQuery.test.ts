import { expect, it } from "vitest";
import { listRequest, parseWorklistQuery, worklistPath } from "../../../src/routing/worklistQuery";
it("forwards a core view without constructing competing lifecycle or progress predicates", () => {
  const path = worklistPath({ view: "UNDECIDED" });
  expect(path).toBe("/cases?view=UNDECIDED");
  expect(
    listRequest(parseWorklistQuery(new URL(path, "https://claimcore.example").search), null),
  ).toEqual({ limit: 50, view: "UNDECIDED" });
  expect(listRequest({ view: "DECIDED_UNPAID" }, null)).toEqual({
    limit: 50,
    view: "DECIDED_UNPAID",
  });
});
it("keeps ordering separate from the closed view vocabulary", () => {
  expect(listRequest({ view: "ALL", order: "RECENT" }, null)).toEqual({
    limit: 50,
    view: "ALL",
    order: "RECENT",
  });
});
it("round trips independent filters and authored search without carrying a cursor", () => {
  const query = parseWorklistQuery(
    "?view=UNDECIDED&lifecycle=ACTIVE&lifecycle=VOIDED&status=CLOSED&progress=PAID&q=A%26B%25%20%2F&order=RECENT&cursor=untrusted",
  );
  expect(listRequest(query, "opaque-page")).toEqual({
    limit: 50,
    view: "UNDECIDED",
    lifecycle: ["ACTIVE", "VOIDED"],
    status: ["CLOSED"],
    progress: ["PAID"],
    text: "A&B% /",
    order: "RECENT",
    cursor: "opaque-page",
  });
  expect(worklistPath(query)).not.toContain("cursor");
});
it.each(["unknown", "mine", "recent", "open-undecided"])(
  "does not alias the unsupported view %s",
  (view) => {
    expect(
      listRequest(
        parseWorklistQuery(
          `?status=OPENED&status=OPENED&status=UNKNOWN&order=unknown&view=${view}`,
        ),
        null,
      ),
    ).toEqual({ limit: 50, status: ["OPENED"] });
  },
);
it("retains responsibility filters independently of the selected view", () => {
  expect(worklistPath({ view: "MINE", assignment: "UNASSIGNED", text: "SIA" })).toBe(
    "/cases?view=MINE&q=SIA&assignment=UNASSIGNED",
  );
});
