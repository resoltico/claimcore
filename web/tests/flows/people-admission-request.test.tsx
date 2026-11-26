import { operationAffordance } from "../support/affordances.fixtures";
import {
  installationActions,
  requesterLinks,
  activeRequester,
} from "../support/human-requester.fixtures";

import { humanReadReply } from "../support/human-read-reply.fixtures";
import { it, expect, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen, waitFor } from "../support/presentation-test-support";
import { PeoplePanel } from "../../src/features/people/PeoplePanel";
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
        actions: installationActions(
          manage
            ? [
                "people.admit",
                "people.list",
                "people.grants",
                "people.history",
                "people.requests",
                "people.requests.dismiss",
              ]
            : ["people.list", "people.grants", "people.history"],
        ),
        links: requesterLinks(
          ["PEOPLE"],
          manage
            ? [
                "people.admit",
                "people.list",
                "people.grants",
                "people.history",
                "people.requests",
                "people.requests.dismiss",
              ]
            : ["people.list", "people.grants", "people.history"],
        ),
      }),
    },
  });
  mockEndpoint("people.list").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      ...humanReadReply("people.list", {
        screen: { regions: [] },
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
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
                    key: "issuer",
                    labelId: "field.principal.issuer.label",
                    prominence: "PRIMARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "subject",
                    labelId: "field.principal.subject.label",
                    prominence: "PRIMARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "name",
                    labelId: "label.requestedName",
                    prominence: "SECONDARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "email",
                    labelId: "label.requestedEmail",
                    prominence: "SECONDARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "emailHintStatus",
                    labelId: "label.emailHintStatus",
                    prominence: "SECONDARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "kind",
                    labelId: "field.principal.kind.label",
                    prominence: "SECONDARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "first",
                    labelId: "label.firstSeen",
                    prominence: "SECONDARY",
                    joins: null,
                    isMoney: false,
                  },
                  {
                    key: "last",
                    labelId: "label.lastSeen",
                    prominence: "SECONDARY",
                    joins: null,
                    isMoney: false,
                  },
                ],
                rows: [
                  {
                    key: "request",
                    actions: [
                      operationAffordance(
                        "people.admit",
                        { kind: "PEOPLE", expectedRevision: 0 },
                        {
                          "input:principal.kind": request.principal.kind,
                          "input:principal.issuer": request.principal.issuer,
                          "input:principal.subject": request.principal.subject,
                          "input:profile.displayName": request.claims.name,
                          "input:profile.contact": request.claims.email,
                        },
                        ["input:profile.displayName", "input:profile.contact", "input:grants"],
                      ),
                    ],
                    values: [
                      { kind: "IDENTIFIER", value: request.principal.issuer },
                      { kind: "IDENTIFIER", value: request.principal.subject },
                      { kind: "TEXT", value: "Unverified name" },
                      { kind: "TEXT", value: "unverified@example.test" },
                      { kind: "MESSAGE", messageId: "label.unverifiedProviderHint" },
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
        subject: { kind: "LIST" },
        actions: [],
        links: [],
        history: null,
        nextCursor: null,
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
  await screen.findByRole("heading", { name: "People and access" });
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
  expect(await screen.findByText(request.principal.issuer)).toBeVisible();
  expect(screen.getByText(request.principal.subject)).toBeVisible();
  expect(screen.getByText("Unverified name")).toBeVisible();
  expect(
    screen.getByText("The identity provider supplied this information without verifying it."),
  ).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Add from access request" }));
  await screen.findByRole("dialog", { name: "Add from access request" });
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
    task: "people.admit",
    binding: { kind: "PEOPLE", expectedRevision: 0 },
    inputs: {
      "input:principal.kind": request.principal.kind,
      "input:principal.issuer": request.principal.issuer,
      "input:principal.subject": request.principal.subject,
      "input:profile.displayName": "Authored service name",
      "input:profile.contact": request.claims.email,
    },
  });
  await user.click(screen.getByRole("button", { name: "Return to editing" }));
  expect(screen.getByRole("textbox", { name: "Name" })).toHaveValue("Authored service name");
  expect(document.querySelector('[name="input:principal.subject"]')).toBeNull();
  await user.click(screen.getByRole("button", { name: "Review changes" }));
  await waitForReviewConfirm();
  const confirmation = screen.getByRole("textbox", { name: "Enter the exact sign-in ID." });
  expect(reviewConfirm()).toBeDisabled();
  await user.type(confirmation, "different-subject");
  expect(reviewConfirm()).toBeDisabled();
  expect(execute).not.toHaveBeenCalled();
  await user.clear(confirmation);
  await user.type(confirmation, request.principal.subject);
  await user.click(reviewConfirm());
  await screen.findByRole("button", { name: "Send again" });
  await user.click(screen.getByRole("button", { name: "Send again" }));
  await waitFor(() => {
    expect(execute).toHaveBeenCalledTimes(2);
  });
  expect(execute.mock.calls[1]![0]).toBe(execute.mock.calls[0]![0]);
  expect(execute.mock.calls[1]![0].input).toMatchObject({
    binding: { kind: "PEOPLE", expectedRevision: 0 },
    inputs: {
      "input:principal.kind": request.principal.kind,
      "input:principal.issuer": request.principal.issuer,
      "input:principal.subject": request.principal.subject,
    },
  });
});
