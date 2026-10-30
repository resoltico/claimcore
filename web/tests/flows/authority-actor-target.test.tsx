import { chooseRoleForReview } from "../support/authority-input.fixtures";
import { expect, it, vi } from "vitest";
import { act, fireEvent } from "@testing-library/react";
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
const grantToSelected = async () => {
  fireEvent.click(await screen.findByRole("button", { name: /Grant role: Grant a role/u }));
  await screen.findByRole("dialog", { name: "Discard these draft changes?" });
};
const expectBoundActor = async (subject = firstActor.principal.subject) => {
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
  await screen.findByText(firstActor.principal.subject);
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
  fireEvent.click(screen.getByRole("button", { name: "Continue current request" }));
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
  await screen.findByRole("checkbox", { name: "I confirm this exact request." });
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
  fireEvent.click(await screen.findByRole("checkbox", { name: "I confirm this exact request." }));
  fireEvent.click(screen.getByRole("button", { name: "Record changes" }));
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
  await screen.findByRole("checkbox", { name: "I confirm this exact request." });
  fireEvent.click(screen.getByRole("button", { name: "Return to editing" }));
  fireEvent.change(screen.getByRole("combobox", { name: "Authority action" }), {
    target: { value: "DISABLE" },
  });
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
  await openActor(secondActor.actorId);
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
          data: { code: "RESOURCE_UNAVAILABLE", action: "NONE_REQUIRED" },
        },
      },
    });
    await pending;
  });
  expect(screen.queryByDisplayValue(firstActor.principal.subject)).toBeNull();
  expect(screen.queryByText(firstActor.principal.subject)).toBeNull();
});
