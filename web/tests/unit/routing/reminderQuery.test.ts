import { expect, it } from "vitest";
import {
  parseReminderQuery,
  remindersPath,
  parseReminderPageQuery,
  reminderPagePath,
} from "../../../src/routing/reminderQuery";
it.each(["PENDING", "MINE", "COMPLETED", "CANCELLED"] as const)(
  "forwards %s without constructing state or assignment rules",
  (view) => {
    expect(parseReminderQuery(`?view=${view}`)).toEqual({ view });
  },
);
it("round trips independently chosen filters and authored text without cursors", () => {
  const query = parseReminderQuery(
    "?view=COMPLETED&state=PENDING&due=TODAY&assignment=MINE&includeInactive=true&q=A%26B&cursor=ignored",
  );
  expect(query).toEqual({
    view: "COMPLETED",
    state: "PENDING",
    due: "TODAY",
    assignment: "MINE",
    includeInactive: true,
    text: "A&B",
  });
  expect(remindersPath(query)).toBe(
    "/reminders?view=COMPLETED&state=PENDING&due=TODAY&assignment=MINE&q=A%26B&includeInactive=true",
  );
});
it("does not alias retired or unknown vocabulary", () => {
  expect(() =>
    parseReminderQuery(
      "?view=mine&state=unknown&due=INACTIVE&assignment=unknown&includeInactive=false",
    ),
  ).toThrow("Invalid reminder selection.");
});

it("keeps manual interface mode separate from the Core filter and drops only a selected view", () => {
  const source =
    "?view=PENDING&state=PENDING&due=TODAY&assignment=MINE&includeInactive=true&q=A%26B&filters=manual";
  const page = parseReminderPageQuery(source);
  expect(page).toEqual({
    manual: true,
    query: {
      state: "PENDING",
      due: "TODAY",
      assignment: "MINE",
      includeInactive: true,
      text: "A&B",
    },
  });
  expect(reminderPagePath({ view: "PENDING", ...page.query }, true)).toBe(
    "/reminders?state=PENDING&due=TODAY&assignment=MINE&q=A%26B&includeInactive=true&filters=manual",
  );
  expect(reminderPagePath({ view: "PENDING", ...page.query }, false)).toBe(
    "/reminders?view=PENDING&state=PENDING&due=TODAY&assignment=MINE&q=A%26B&includeInactive=true",
  );
  expect(parseReminderPageQuery("?filters=manual")).toEqual({ manual: true, query: {} });
  expect(() => parseReminderPageQuery("?filters=unknown")).toThrow("Invalid reminder selection.");
});

it("round trips an explicit case selector and refuses conflicting or duplicate selection", () => {
  const target = { kind: "ID" as const, caseId: "10000000-0000-4000-8000-000000000001" };
  expect(
    parseReminderQuery(remindersPath({ target, assignment: "MINE", due: "TODAY" }).split("?")[1]!),
  ).toEqual({ target, assignment: "MINE", due: "TODAY" });
  for (const query of [
    "?view=MINE&view=PENDING",
    "?caseId=bad",
    "?caseReference=A&caseReference=B",
    "?caseReference=A&caseId=10000000-0000-4000-8000-000000000001",
  ]) {
    expect(() => parseReminderQuery(query)).toThrow();
  }
});
