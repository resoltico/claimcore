import { activeRequester } from "../support/human-requester.fixtures";
import { humanReadReply } from "../support/human-read-reply.fixtures";
import { it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { PeoplePanel } from "../../src/views/PeoplePanel";
import { mockEndpoint, getEndpointMock } from "../support/endpoint-mocks";
import { workBasis } from "../support/inputs";
import { peoplePreview } from "../support/people-operation.fixtures";
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
      ...humanReadReply("context", {
        ...activeRequester,
        basis: { kind: "SNAPSHOT", observation: workBasis },
        context: {
          ...activeRequester.context,
          views: ["PEOPLE"],
          tasks: manage
            ? [
                "people.admit",
                "people.list",
                "people.grants",
                "people.history",
                "people.requests",
                "people.requests.dismiss",
              ]
            : ["people.list", "people.grants", "people.history"],
          peopleIssuerSeed: manage ? "https://issuer.example/" : null,
        },
      }),
    },
  });
  mockEndpoint("people.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("people.list", {
        screen: { regions: [] },
        context: { kind: "PEOPLE", actorIds: [], nextCursor: null },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    },
  });
  const requests = mockEndpoint("people.requests").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("people.requests", {
        screen: {
          regions: [
            {
              role: "RESULTS",
              content: {
                kind: "TABLE",
                columns: [
                  {
                    key: "name",
                    labelId: "label.requestedName",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                  {
                    key: "email",
                    labelId: "field.profile.contact.label",
                    prominence: "PRIMARY",
                    isMoney: false,
                  },
                  {
                    key: "kind",
                    labelId: "field.principal.kind.label",
                    prominence: "SECONDARY",
                    isMoney: false,
                  },
                  {
                    key: "first",
                    labelId: "label.firstSeen",
                    prominence: "SECONDARY",
                    isMoney: false,
                  },
                  {
                    key: "last",
                    labelId: "label.lastSeen",
                    prominence: "SECONDARY",
                    isMoney: false,
                  },
                ],
                rows: [
                  {
                    key: "request",
                    values: [
                      { kind: "TEXT", value: "Unverified name" },
                      { kind: "TEXT", value: "unverified@example.test" },
                      { kind: "CODE", vocabulary: "principalKind", token: "SERVICE" },
                      { kind: "INSTANT", value: "2026-11-01T10:00:00.000000Z" },
                      { kind: "INSTANT", value: "2026-11-02T12:00:00.000000Z" },
                    ],
                  },
                ],
              },
            },
          ],
        },
        context: {
          kind: "ACCESS_REQUESTS",
          requests: [
            {
              key: "request",
              principal: request.principal,
              profileSeed: { displayName: "Unverified name", contact: "unverified@example.test" },
            },
          ],
          nextCursor: null,
        },
        basis: { kind: "SNAPSHOT", observation: workBasis },
        disclosure: "CURRENT",
      }),
    },
  });
  render(<PeoplePanel onLockChange={vi.fn()} />);
  return requests;
};
it("people read alone never requests or exposes pending principals", async () => {
  const requests = setup(false);
  await screen.findByRole("heading", { name: "People & access" });
  await waitFor(() => {
    expect(getEndpointMock("people.list")).toHaveBeenCalled();
  });
  expect(requests).not.toHaveBeenCalled();
  expect(document.body).not.toHaveTextContent(request.principal.subject);
});
it("binds request admission identity while profile hints stay editable through review and exact retry", async () => {
  const preview = peoplePreview();
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
  for (const name of [
    "input:principal.kind",
    "input:principal.issuer",
    "input:principal.subject",
  ]) {
    expect(document.querySelector(`[name="${name}"]`)).toBeNull();
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
      kind: "people.admit",
      profile: { displayName: "Authored service name", contact: request.claims.email },
    },
  });
  await user.click(screen.getByRole("button", { name: "Return to editing" }));
  expect(screen.getByRole("textbox", { name: "Name" })).toHaveValue("Authored service name");
  expect(document.querySelector('[name="input:principal.subject"]')).toBeNull();
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
