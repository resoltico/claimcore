import { expect, it } from "vitest";
import {
  listRequest,
  parseWorklistQuery,
  viewQuery,
  worklistPath,
} from "../../../src/routing/worklistQuery";
it("reproduces open undecided work from a shareable URL without carrying a cursor", () => {
  const path = worklistPath(viewQuery("open-undecided"));
  expect(path).toBe("/cases?view=open-undecided&status=OPENED&progress=UNDECIDED&lifecycle=ACTIVE");
  expect(
    listRequest(parseWorklistQuery(new URL(path, "https://claimcore.example").search), null),
  ).toEqual({ limit: 50, lifecycle: ["ACTIVE"], status: ["OPENED"], progress: ["UNDECIDED"] });
});
it("uses current progress for unpaid work and recent ordering for recent changes", () => {
  expect(listRequest(viewQuery("decided-unpaid"), null)).toEqual({
    limit: 50,
    lifecycle: ["ACTIVE"],
    progress: ["DECIDED"],
  });
  expect(listRequest(viewQuery("recent"), null)).toEqual({ limit: 50, order: "RECENT" });
});
it("restores multi-select filters and authored search without interpreting search as query syntax", () => {
  const query = parseWorklistQuery(
    "?lifecycle=ACTIVE&lifecycle=VOIDED&status=CLOSED&progress=PAID&q=A%26B%25%20%2F&order=RECENT&cursor=untrusted",
  );
  expect(listRequest(query, "opaque-page")).toEqual({
    limit: 50,
    lifecycle: ["ACTIVE", "VOIDED"],
    status: ["CLOSED"],
    progress: ["PAID"],
    text: "A&B% /",
    order: "RECENT",
    cursor: "opaque-page",
  });
  expect(worklistPath(query)).not.toContain("cursor");
});
it("does not forward unknown query vocabulary or duplicate set entries", () => {
  expect(
    listRequest(
      parseWorklistQuery("?status=OPENED&status=OPENED&status=UNKNOWN&order=unknown&view=unknown"),
      null,
    ),
  ).toEqual({ limit: 50, status: ["OPENED"] });
});
it("keeps manual work unassociated with a saved view", () => {
  expect(worklistPath({ progress: ["DECIDED"], text: "SIA" })).toBe(
    "/cases?progress=DECIDED&q=SIA",
  );
});
