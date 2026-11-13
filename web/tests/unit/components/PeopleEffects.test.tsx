import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { PeopleEffects } from "../../../src/components/PeopleEffects";

it("shows every ordered core admission effect including profile and differently scoped grants", () => {
  render(
    <PeopleEffects
      effects={[
        {
          kind: "people.register",
          principal: {
            kind: "HUMAN",
            issuer: "https://identity.invalid",
            subject: "authored-subject",
          },
        },
        {
          kind: "people.setProfile",
          profile: { displayName: "Anna Ozola", contact: "anna@example.invalid" },
        },
        { kind: "people.grant", grant: { role: "OWNER", scope: { kind: "INSTALLATION" } } },
        {
          kind: "people.grant",
          grant: {
            role: "CASE_READER",
            scope: {
              kind: "CASE",
              caseId: "20000000-0000-4000-8000-000000000001",
              caseReference: "CASE/مرجع",
            },
          },
        },
      ]}
    />,
  );
  expect(screen.getAllByRole("listitem")).toHaveLength(4);
  expect(screen.getAllByRole("heading").map((item) => item.textContent)).toEqual([
    "Register account",
    "Update profile",
    "Grant role",
    "Grant role",
  ]);
  for (const value of [
    "authored-subject",
    "Anna Ozola",
    "anna@example.invalid",
    "Owner",
    "Case reader",
    "All cases",
    "One case",
    "CASE/مرجع",
  ]) {
    expect(screen.getByText(value, { exact: true })).toBeVisible();
  }
});
