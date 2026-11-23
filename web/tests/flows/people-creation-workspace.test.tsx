import { openPersonEditor } from "../support/people-drawer.fixtures";
import { expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { fireEvent, within } from "@testing-library/react";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { setupActorTargets, listRoute } from "../support/actor-target.fixtures";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { PeoplePanel } from "../../src/views/PeoplePanel";
import { service } from "../../src/api/service";
const start = async () => {
  const preview = setupActorTargets(["people.register", "people.admit"]);
  listRoute();
  render(<PeoplePanel onLockChange={vi.fn()} />);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "Add person" }));
  await user.click(screen.getByRole("menuitem", { name: "Register account" }));
  await screen.findByRole("dialog", { name: "Register account" });
  return { user, preview };
};
const author = (subject: string) => {
  for (const [name, value] of Object.entries({
    "input:principal.issuer": "https://issuer.example/",
    "input:principal.subject": subject,
    "input:profile.displayName": "Authored account",
  })) {
    fireEvent.change(document.querySelector(`[name="${name}"]`)!, { target: { value } });
  }
};
it("registers an account through People without granting a role and renders Core refusal", async () => {
  const { user, preview } = await start();
  const execute = vi.spyOn(service, "execute").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REJECTED",
          rejection: {
            kind: "REFUSED",
            advice: {
              message: { messageId: "diagnostic.ACTOR_ALREADY_REGISTERED", arguments: [] },
              action: "REVIEW_LATEST",
            },
          },
        },
      },
    }),
  );
  author("account-without-rights");
  expect(screen.queryByRole("radio", { name: "Case reader" })).toBeNull();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  const [prepared] = preview.mock.calls[0]!;
  expect(prepared.input).toMatchObject({
    task: "people.register",
    binding: { kind: "PEOPLE", expectedRevision: 0 },
    inputs: { "input:principal.subject": "account-without-rights" },
  });
  expect(prepared.input.inputs).not.toHaveProperty("input:grants");
  expect(prepared.input.inputs).not.toHaveProperty("input:role");
  await user.click(reviewConfirm());
  await waitFor(() => {
    expect(execute).toHaveBeenCalledOnce();
  });
  expect(execute.mock.calls[0]![0]).toBe(prepared);
  expect(await screen.findByText("This account is already registered.")).toBeVisible();
  expect(screen.queryByRole("button", { name: "Send again" })).toBeNull();
});
it("protects dirty and prepared registration until explicit discard creates a distinct admission identity", async () => {
  const { user, preview } = await start();
  author("keep-registration");
  await user.click(
    within(screen.getByRole("dialog")).getAllByRole("button", { name: "Close" })[0]!,
  );
  await screen.findByRole("dialog", { name: "Discard these draft changes?" });
  await user.click(screen.getByRole("button", { name: "Keep editing" }));
  expect(document.querySelector('[name="input:principal.subject"]')).toHaveValue(
    "keep-registration",
  );
  expect(preview).not.toHaveBeenCalled();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  const [first] = preview.mock.calls[0]!;
  await user.click(screen.getByRole("button", { name: "Return to editing" }));
  await user.click(
    within(screen.getByRole("dialog")).getAllByRole("button", { name: "Close" })[0]!,
  );
  await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
  await openPersonEditor();
  author("new-admission");
  await user.click(screen.getByRole("radio", { name: "Case reader" }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  const [second] = preview.mock.calls[1]!;
  expect(second.input.task).toBe("people.admit");
  expect(second.input.operationId).not.toBe(first.input.operationId);
  expect(second.input.inputs).toMatchObject({
    "input:principal.subject": "new-admission",
    "input:grants": [{ "input:role": "CASE_READER", "input:scope": "INSTALLATION" }],
  });
  expect(first.input.task).toBe("people.register");
  expect(first.input.inputs).toMatchObject({ "input:principal.subject": "keep-registration" });
});
it("cannot switch uncertain registration and preserves its exact reviewed request through keep and retry", async () => {
  const { user, preview } = await start();
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
    dispatched: true,
  });
  author("uncertain-account");
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
  expect(screen.getByRole("button", { name: "Add person", hidden: true })).toBeDisabled();
  await user.click(
    within(screen.getByRole("dialog")).getAllByRole("button", { name: "Close" })[0]!,
  );
  await user.click(await screen.findByRole("button", { name: "Keep editing" }));
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(2);
  });
  expect(preview).toHaveBeenCalledOnce();
  expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  const [original] = execute.mock.calls[0]!;
  await user.click(
    within(screen.getByRole("dialog")).getAllByRole("button", { name: "Close" })[0]!,
  );
  await user.click(await screen.findByRole("button", { name: "Discard and leave" }));
  await openPersonEditor();
  author("after-explicit-uncertain-discard");
  await user.click(screen.getByRole("radio", { name: "Case reader" }));
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  expect(preview.mock.calls[1]![0].input.task).toBe("people.admit");
  expect(preview.mock.calls[1]![0].input.operationId).not.toBe(original.input.operationId);
  expect(original.input.task).toBe("people.register");
  expect(execute).toHaveBeenCalledTimes(2);
});
