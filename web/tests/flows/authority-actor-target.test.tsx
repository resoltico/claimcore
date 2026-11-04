import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { stubViewCounts } from "../support/dashboard-context.fixtures";
import userEvent from "@testing-library/user-event";
import { chooseOption } from "../support/choice-entry";
import { chooseRoleForReview } from "../support/authority-input.fixtures";
import { beforeEach, expect, it, vi } from "vitest";
import { act, fireEvent, within } from "@testing-library/react";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { AuthorityPanel } from "../../src/views/AuthorityPanel";
import { Dashboard } from "../../src/views/Dashboard";
import { service } from "../../src/api/service";
import { getEndpointMock } from "../support/endpoint-mocks";
import {
  actorRoute,
  authorDifferentTarget,
  firstActor,
  secondActor,
  grantsFor,
  listRoute,
  openActor,
  setupActorTargets,
} from "../support/actor-target.fixtures";
beforeEach(() => {
  stubViewCounts();
});
const grantToSelected = async () => {
  fireEvent.click(await screen.findByRole("button", { name: /Grant role:/u }));
  await screen.findByRole("dialog", { name: "Discard these draft changes?" });
};
const expectBoundActor = async (subject = firstActor.principal.subject) => {
  if (document.querySelector('[name="target.subject"]') === null) {
    expect(await screen.findAllByRole("dialog")).toHaveLength(1);
    const actions = await screen.findAllByRole("button", { name: /^(?:Grant|Revoke) role:/u });
    fireEvent.click(actions[0]!);
  }
  await waitFor(() => {
    expect(document.querySelector('[name="target.subject"]')).toHaveValue(subject);
  });
  expect(document.querySelector('[name="target.subject"]')).toBeDisabled();
  expect(document.querySelector('[name="target.issuer"]')).toBeDisabled();
  expect(screen.queryByRole("option", { name: "Register actor" })).toBeNull();
};
it("prefills a deep-linked actor from disclosed identity rather than a stale list row and previews GRANT", async () => {
  const preview = setupActorTargets();
  actorRoute();
  render(<Dashboard />);
  await expectBoundActor();
  expect(screen.getByRole("combobox", { name: "Authority action" })).toHaveValue("GRANT");
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input).toMatchObject({
    kind: "AUTHORITY",
    target: firstActor.principal,
    act: { kind: "GRANT" },
  });
});
it("uses an offered alternative instead of inventing GRANT on actor detail", async () => {
  const preview = setupActorTargets(["REGISTER", "REVOKE"]);
  actorRoute();
  render(<Dashboard />);
  await expectBoundActor();
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input.act.kind).toBe("REVOKE");
});
it("does not offer REGISTER or manufacture mutation inputs for an actor with no offered existing-actor act", async () => {
  const preview = setupActorTargets(["REGISTER"]);
  actorRoute();
  render(<Dashboard />);
  await screen.findAllByText(firstActor.principal.subject);
  expect(screen.queryByRole("button", { name: "Review changes" })).toBeNull();
  expect(screen.queryByRole("option", { name: "Register actor" })).toBeNull();
  expect(preview).not.toHaveBeenCalled();
});
it("declining retarget consent retains the authored principal and returns to its request", async () => {
  const preview = setupActorTargets();
  listRoute();
  render(<Dashboard />);
  await authorDifferentTarget();
  await openActor();
  await grantToSelected();
  fireEvent.click(screen.getByRole("button", { name: "Keep editing" }));
  expect(screen.getByRole("dialog", { name: "Add person" })).toBeVisible();
  expect(document.querySelector('[name="target.subject"]')).toHaveValue("authored-other");
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input).toMatchObject({ target: { subject: "authored-other" } });
});
it("explicit retarget consent creates a fresh request for the disclosed actor", async () => {
  const preview = setupActorTargets();
  listRoute();
  render(<Dashboard />);
  await authorDifferentTarget();
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  const [previous] = preview.mock.calls[0]!;
  fireEvent.click(screen.getByRole("button", { name: "Return to editing" }));
  await openActor();
  await grantToSelected();
  fireEvent.click(screen.getByRole("button", { name: "Discard and change action" }));
  await expectBoundActor();
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledTimes(2);
  });
  const [next] = preview.mock.calls[1]!;
  expect(next.input).toMatchObject({ target: firstActor.principal, act: { kind: "GRANT" } });
  expect(next.input.operationId).not.toBe(previous.input.operationId);
});
it("declining an uncertain request retarget preserves the exact request and retry identity", async () => {
  const preview = setupActorTargets();
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
  });
  listRoute();
  render(<Dashboard />);
  await authorDifferentTarget();
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  fireEvent.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
  const [original] = execute.mock.calls[0]!;
  await openActor();
  await grantToSelected();
  expect(screen.getByText(/does not cancel it/u)).toBeVisible();
  fireEvent.click(screen.getByRole("button", { name: "Keep editing" }));
  fireEvent.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(2);
  });
  expect(execute.mock.calls[1]![0]).toBe(original);
  expect(execute.mock.calls[1]![0].bytes).toBe(original.bytes);
  expect(original.input).toMatchObject({ target: { subject: "authored-other" } });
  expect(preview).toHaveBeenCalledOnce();
});
it("changing acts retains the bound actor identity after discard consent", async () => {
  const preview = setupActorTargets();
  actorRoute();
  render(<Dashboard />);
  await expectBoundActor();
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  fireEvent.click(screen.getByRole("button", { name: "Return to editing" }));
  await chooseOption(
    userEvent.setup(),
    screen.getByRole("combobox", { name: "Authority action" }),
    "DISABLE",
  );
  fireEvent.click(await screen.findByRole("button", { name: "Discard and change action" }));
  await expectBoundActor();
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledTimes(2);
  });
  expect(preview.mock.calls[1]![0].input).toMatchObject({
    target: firstActor.principal,
    act: { kind: "DISABLE" },
  });
});
it("ignores a late actor disclosure when a different actor is selected", async () => {
  const preview = setupActorTargets();
  let release!: (reply: Awaited<ReturnType<typeof service.authorityGrants>>) => void;
  const pending = new Promise<Awaited<ReturnType<typeof service.authorityGrants>>>((resolve) => {
    release = resolve;
  });
  getEndpointMock("authority.grants")
    .mockReturnValueOnce(pending)
    .mockResolvedValue(grantsFor(secondActor));
  listRoute();
  render(<Dashboard />);
  await screen.findAllByRole("link", { name: / ·/u });
  await openActor();
  await openActor(secondActor.actor.actorId);
  await expectBoundActor(secondActor.principal.subject);
  await act(async () => {
    release(grantsFor());
    await pending;
  });
  chooseRoleForReview();
  fireEvent.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input).toMatchObject({ target: secondActor.principal });
});
it("waits for fresh actor disclosure after availability loss and never restores refused prefills", async () => {
  setupActorTargets();
  actorRoute();
  const onLockChange = vi.fn();
  const view = render(<AuthorityPanel onLockChange={onLockChange} disclosureAvailable />);
  await expectBoundActor();
  view.rerender(<AuthorityPanel onLockChange={onLockChange} disclosureAvailable={false} />);
  expect(document.querySelector('[name="target.subject"]')).toBeNull();
  let release!: (reply: Awaited<ReturnType<typeof service.authorityGrants>>) => void;
  const pending = new Promise<Awaited<ReturnType<typeof service.authorityGrants>>>((resolve) => {
    release = resolve;
  });
  getEndpointMock("authority.grants").mockReturnValueOnce(pending);
  view.rerender(<AuthorityPanel onLockChange={onLockChange} disclosureAvailable />);
  await waitFor(() => {
    expect(getEndpointMock("authority.grants")).toHaveBeenCalledTimes(2);
  });
  expect(document.querySelector('[name="target.subject"]')).toBeNull();
  await act(async () => {
    release({
      kind: "outcome",
      status: 200,
      value: {
        outcome: {
          kind: "REJECTED",
          data: { code: "RESOURCE_UNAVAILABLE", action: "ASK_FOR_ACCESS" },
        },
      },
    });
    await pending;
  });
  expect(screen.queryByDisplayValue(firstActor.principal.subject)).toBeNull();
  expect(screen.queryByText(firstActor.principal.subject)).toBeNull();
});

it("opens one named person panel and prefills profile edits only from its current disclosed profile", async () => {
  setupActorTargets(["SET_PROFILE"]);
  actorRoute();
  render(<Dashboard />);
  const person = await screen.findByRole("dialog", { name: "server-first" });
  expect(screen.getAllByRole("dialog")).toHaveLength(1);
  expect(person).not.toHaveTextContent("stale-list-first");
  expect(
    within(person.querySelector<HTMLDetailsElement>(".actor-record-details")!).getByText(
      firstActor.principal.subject,
      { selector: "bdi", exact: true },
    ),
  ).not.toBeVisible();
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: /^Update profile:/u }));
  await waitFor(() => {
    expect(document.querySelector('[name="act.profile.displayName"]')).toHaveValue("server-first");
  });
  expect(document.querySelector('[name="act.profile.contact"]')).toHaveValue("");
  expect(document.querySelector('[name="target.subject"]')).toHaveValue(
    firstActor.principal.subject,
  );
  expect(document.querySelector('[name="target.subject"]')).toBeDisabled();
  expect(document.querySelector('[name="target.subject"]')).not.toBeVisible();
  expect(screen.getAllByRole("dialog")).toHaveLength(1);
  expect(getEndpointMock("authority.grants")).toHaveBeenCalledOnce();
});

it("opens a row revocation with the exact supplied principal, role and case reference without another grant read", async () => {
  const preview = setupActorTargets(["REVOKE"]);
  const reply = grantsFor();
  if (reply.kind !== "outcome" || reply.value.outcome.kind !== "SUCCEEDED") {
    throw new Error("Grant page fixture required");
  }
  getEndpointMock("authority.grants").mockResolvedValue({
    ...reply,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          ...reply.value.outcome.data,
          items: [
            {
              role: "CASE_READER",
              scope: {
                kind: "CASE",
                caseId: "20000000-0000-4000-8000-000000000001",
                caseReference: "CASE-FROM-GRANT",
              },
            },
          ],
        },
      },
    },
  });
  actorRoute();
  render(<Dashboard />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Revoke role" }));
  expect(document.querySelector('[name="act.scope.target.caseReference"]')).toHaveValue(
    "CASE-FROM-GRANT",
  );
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitFor(() => {
    expect(preview).toHaveBeenCalledOnce();
  });
  expect(preview.mock.calls[0]![0].input).toMatchObject({
    target: firstActor.principal,
    act: {
      kind: "REVOKE",
      role: "CASE_READER",
      scope: { kind: "CASE", target: { kind: "REFERENCE", caseReference: "CASE-FROM-GRANT" } },
    },
  });
  expect(getEndpointMock("authority.grants")).toHaveBeenCalledOnce();
});
