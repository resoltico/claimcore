import userEvent from "@testing-library/user-event";
import { expect, it } from "vitest";
import { render, screen } from "../../support/presentation-test-support";
import { ActorSummary } from "../../../src/components/ActorSummary";
import type { ActorReviewItem } from "@contracts/client";
const actorId = "10000000-0000-4000-8000-000000000001";
const actor: ActorReviewItem = {
  actor: {
    actorId,
    displayName: { kind: "NAMED", value: "Authority owner" },
    principalKind: "HUMAN",
    availability: "NOT_ELIGIBLE",
  },
  principal: { kind: "HUMAN", issuer: "https://issuer.example/", subject: "immutable-subject" },
  contact: null,
  state: "ACTIVE",
  roles: [{ role: "OWNER", installation: true, caseCount: 0 }],
};
it("omits responsibility facts when the core does not disclose a descriptor", () => {
  render(<ActorSummary actor={actor} />);
  expect(screen.queryByText("Responsible for")).toBeNull();
  expect(screen.queryByRole("link")).toBeNull();
  expect(screen.getByText("Enabled with roles", { exact: true })).toBeVisible();
});
it.each([0, 3])(
  "renders known responsibility count %s and executes the exact supplied filter",
  (count) => {
    render(
      <ActorSummary
        actor={{ ...actor, responsibleCases: { count, view: "ALL", responsibleActor: actorId } }}
      />,
    );
    expect(screen.getByText("Responsible for")).toBeVisible();
    expect(screen.getByRole("link", { name: `Matching cases: ${count}` })).toHaveAttribute(
      "href",
      `/cases?view=ALL&responsibleActor=${actorId}`,
    );
  },
);

it("keeps the provided name visible while issuer, subject and account ID remain in collapsed Record details", async () => {
  render(<ActorSummary actor={actor} />);
  expect(screen.getByText("Authority owner")).toBeVisible();
  for (const value of [actor.actor.actorId, actor.principal.issuer, actor.principal.subject]) {
    expect(screen.getByText(value, { exact: true })).not.toBeVisible();
  }
  await userEvent.setup().click(screen.getByText("Record details", { exact: true }));
  for (const value of [actor.actor.actorId, actor.principal.issuer, actor.principal.subject]) {
    expect(screen.getByText(value, { exact: true })).toBeVisible();
  }
});
