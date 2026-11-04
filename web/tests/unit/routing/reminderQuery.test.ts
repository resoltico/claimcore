import { expect, it } from "vitest";
import { parseReminderQuery, remindersPath } from "../../../src/routing/reminderQuery";
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
  expect(
    parseReminderQuery(
      "?view=mine&state=unknown&due=INACTIVE&assignment=unknown&includeInactive=false",
    ),
  ).toEqual({});
});
