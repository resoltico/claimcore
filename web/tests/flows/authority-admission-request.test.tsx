import { it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { AuthorityPanel } from "../../src/views/AuthorityPanel";
import { context } from "../support/service-ui.fixtures";
import { mockEndpoint, getEndpointMock } from "../support/endpoint-mocks";
import { actorContextInput, workBasis } from "../support/inputs";
import { authorityPreview } from "../support/authority-operation.fixtures";
import { chooseAdmissionRole } from "../support/people-drawer.fixtures";
import { service } from "../../src/api/service";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
const request = {
  principal: {
    kind: "SERVICE" as const,
    issuer: "https://issuer.example/",
    subject: "exact-request-subject",
  },
  firstSeen: "2026-11-01T10:00:00.000000Z",
  lastSeen: "2026-11-02T12:00:00.000000Z",
  claims: { name: "Unverified name", email: "unverified@example.test" },
};
const setup = (manage: boolean) => {
  mockEndpoint("context").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: {
          requester: actorContextInput({
            ...context,
            capabilities: manage ? ["MANAGE_AUTHORITY", "READ_AUTHORITY"] : ["READ_AUTHORITY"],
            offers: manage ? [{ kind: "AUTHORITY_ACT", act: "ADMIT" }] : [],
            views: ["AUTHORITY"],
          }),
        },
      },
    },
  });
  mockEndpoint("authority.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: { kind: "SUCCEEDED", data: { basis: workBasis, items: [], nextCursor: null } },
    },
  });
  const requests = mockEndpoint("authority.requests").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      outcome: {
        kind: "SUCCEEDED",
        data: { basis: workBasis, items: [request], nextCursor: null },
      },
    },
  });
  render(<AuthorityPanel onLockChange={vi.fn()} />);
  return requests;
};
it("authority read alone never requests or exposes pending principals", async () => {
  const requests = setup(false);
  await screen.findByRole("heading", { name: "People & access" });
  await waitFor(() => {
    expect(getEndpointMock("authority.list")).toHaveBeenCalled();
  });
  expect(requests).not.toHaveBeenCalled();
  expect(document.body).not.toHaveTextContent(request.principal.subject);
});
it("binds request admission identity while profile hints stay editable through review and exact retry", async () => {
  const preview = authorityPreview();
  const execute = vi.spyOn(service, "execute").mockResolvedValue({
    kind: "deliveryFailure",
    notice: { kind: "local", reason: "unreachable" },
    dispatched: true,
  });
  setup(true);
  const user = userEvent.setup();
  await screen.findByText("Unverified name");
  await user.click(screen.getAllByRole("button", { name: "Add person" }).at(-1)!);
  await screen.findByRole("dialog", { name: "Add person" });
  for (const [name, value] of [
    ["target.issuer", request.principal.issuer],
    ["target.subject", request.principal.subject],
  ]) {
    expect(document.querySelector(`[name="${name}"]`)).toHaveValue(value);
    expect(document.querySelector(`[name="${name}"]`)).toBeDisabled();
  }
  expect(document.querySelector('[name="target.kind"][value="SERVICE"]')).toBeChecked();
  for (const control of document.querySelectorAll('[name="target.kind"]')) {
    expect(control).toBeDisabled();
  }
  const name = screen.getByRole("textbox", { name: "Name" });
  await user.clear(name);
  await user.type(name, "Authored service name");
  await chooseAdmissionRole();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  expect(preview.mock.calls[0]![0].input).toMatchObject({
    target: request.principal,
    act: {
      kind: "ADMIT",
      profile: { displayName: "Authored service name", contact: request.claims.email },
    },
  });
  await user.click(screen.getByRole("button", { name: "Return to editing" }));
  expect(screen.getByRole("textbox", { name: "Name" })).toHaveValue("Authored service name");
  expect(document.querySelector('[name="target.subject"]')).toBeDisabled();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(2);
  });
  expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  expect(execute.mock.calls[1]![0].input).toMatchObject({ target: request.principal });
});
