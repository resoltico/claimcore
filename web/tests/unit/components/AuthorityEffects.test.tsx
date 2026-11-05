import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { AuthorityEffects } from "../../../src/components/AuthorityEffects";

it("shows every ordered core admission effect including profile and differently scoped grants", () => {
  render(
    <AuthorityEffects
      effects={[
        {
          kind: "REGISTER",
          principal: {
            kind: "HUMAN",
            issuer: "https://identity.invalid",
            subject: "authored-subject",
          },
        },
        {
          kind: "SET_PROFILE",
          profile: { displayName: "Anna Ozola", contact: "anna@example.invalid" },
        },
        { kind: "GRANT", grant: { role: "OWNER", scope: { kind: "INSTALLATION" } } },
        {
          kind: "GRANT",
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
    "Installation",
    "One case",
    "CASE/مرجع",
  ]) {
    expect(screen.getByText(value, { exact: true })).toBeVisible();
  }
});
