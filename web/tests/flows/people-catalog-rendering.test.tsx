import { taskDefinitions } from "@contracts/client";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { chooseOption, optionValues } from "../support/choice-entry";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { getButton, render, screen, waitFor } from "../support/presentation-test-support";
import { useState } from "react";
import type { HumanReceiptDocument, RequestByEndpoint } from "@contracts/client";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { useCredential } from "../../src/session/SessionContext";
import { OperationPanel, OperationDiscard } from "../../src/views/operation/OperationPanel";
import { prepareRequest } from "../../src/protocol/preparedRequest";
import { peopleReview, peopleAcceptance } from "../support/people-operation.fixtures";
import { isRequest } from "../../src/protocol/client";
import { input, response, type Dispatch } from "../support/catalog-transport.fixtures";
afterEach(() => vi.unstubAllGlobals());
type PeopleInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "PEOPLE";
  }
>;
const peopleActs = taskDefinitions.flatMap((task) =>
  task.kind === "OPERATION" && task.subject === "PEOPLE" ? [{ kind: task.act }] : [],
);
const peopleReceipt = (request: PeopleInput) => {
  const value = peopleAcceptance(prepareRequest("operation.execute", request));
  if (value.kind !== "outcome" || value.value.outcome.kind !== "ACCEPTED") {
    throw new Error("People acceptance fixture required");
  }
  return value.value.outcome.document;
};
const PeopleBinding = () => {
  const credential = useCredential();
  const [recorded, setRecorded] = useState<HumanReceiptDocument | null>(null);
  const model = useOperation({
    subject: "PEOPLE",
    kind: "people.register",
    current: null,
    credential,
    onLockChange: () => undefined,
    onRecorded: setRecorded,
  });
  return (
    <>
      <OperationDiscard model={model} />
      <OperationPanel
        model={model}
        current={null}
        offered={peopleActs.map((entry) => entry.kind)}
      />
      {recorded === null ? null : <p role="status">People receipt accepted</p>}
    </>
  );
};
const peoplePreview = (parsed: PeopleInput) =>
  response("operation.preview", {
    representation: "HUMAN",
    operationId: parsed.operationId,
    outcome: { kind: "REVIEWED", review: peopleReview(parsed) },
  });
const peopleTransport = (calls: Dispatch<PeopleInput>[]) =>
  vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (
      typeof path !== "string" ||
      !["/api/operation.execute", "/api/operation.preview"].includes(path) ||
      typeof init?.body !== "string"
    ) {
      throw new Error("Unexpected people endpoint.");
    }
    const parsed: unknown = JSON.parse(init.body);
    if (!isRequest("operation.execute", parsed) || parsed.kind !== "PEOPLE") {
      throw new Error("Production people form did not produce a complete request.");
    }
    calls.push({
      path,
      bytes: init.body,
      input: parsed,
    });
    if (path === "/api/operation.preview") {
      return Promise.resolve(peoplePreview(parsed));
    }
    return Promise.resolve(
      response("operation.execute", {
        representation: "HUMAN",
        operationId: parsed.operationId,
        outcome: {
          kind: "ACCEPTED",
          document: peopleReceipt(parsed),
        },
      }),
    );
  });
const authorPeople = async (kind: PeopleInput["act"]["kind"]) => {
  const user = userEvent.setup();
  const control = await screen.findByRole("combobox", { name: "Access change" });
  await user.click(control);
  await waitFor(() => {
    expect(optionValues(control)).toHaveLength(8);
  });
  await chooseOption(user, control, kind);
  input("input:principal.issuer", "https://issuer.example/");
  input("input:principal.subject", "synthetic-catalogue-human");
  if (kind === "people.register" || kind === "people.setProfile" || kind === "people.admit") {
    input("input:profile.displayName", "Synthetic profile name");
  }
  if (kind === "people.admit") {
    await user.click(screen.getByRole("radio", { name: "Case reader" }));
  }
  if (kind === "people.grant" || kind === "people.revoke") {
    await user.click(screen.getByRole("radio", { name: "Case reader" }));
  }
};
it.each(peopleActs)(
  "renders, reviews and submits real people act $kind through production transport",
  async (act) => {
    expect(peopleActs).toHaveLength(8);
    const calls: Dispatch<PeopleInput>[] = [];
    vi.stubGlobal("fetch", peopleTransport(calls));
    render(<PeopleBinding />);
    await authorPeople(act.kind);
    const user = userEvent.setup();
    await user.click(getButton("Review changes"));
    await waitForReviewConfirm();
    await waitForReviewConfirm();
    if (act.kind === "people.eraseProfile") {
      await user.type(
        screen.getByRole("textbox", { name: "Exact operation ID" }),
        calls[0]!.input.operationId,
      );
    }
    await user.click(reviewConfirm());
    await waitFor(() => {
      expect(calls).toHaveLength(2);
      expect(calls[1]!.bytes).toBe(calls[0]!.bytes);
    });
    await screen.findByText("People receipt accepted", { exact: true });
    expect(calls[0]!.input.act.kind).toBe(act.kind);
    expect(calls[0]!.input.target).toEqual({
      kind: "HUMAN",
      issuer: "https://issuer.example/",
      subject: "synthetic-catalogue-human",
    });
    if (calls[0]!.input.act.kind === "people.grant" || calls[0]!.input.act.kind === "people.revoke") {
      expect(calls[0]!.input.act).toMatchObject({
        role: "CASE_READER",
        scope: {
          kind: "INSTALLATION",
        },
      });
    }
    if (calls[0]!.input.act.kind === "people.enable") {
      expect(calls[0]!.input.act).toEqual({
        kind: "people.enable",
      });
    }
  },
);

it("requires an explicit role and disables service-incompatible roles from catalog admission", async () => {
  const calls: Dispatch<PeopleInput>[] = [];
  vi.stubGlobal("fetch", peopleTransport(calls));
  render(<PeopleBinding />);
  const user = userEvent.setup();
  const control = await screen.findByRole("combobox", { name: "Access change" });
  await user.click(control);
  await waitFor(() => {
    expect(optionValues(control)).toHaveLength(8);
  });
  await chooseOption(user, screen.getByRole("combobox", { name: "Access change" }), "people.grant");
  const group = screen.getByRole("group", { name: "Role" });
  expect(group.querySelector("input:checked")).toBeNull();
  const owner = screen.getByRole("radio", { name: "Owner" });
  await user.click(owner);
  expect(owner).toBeChecked();
  await user.click(screen.getByRole("radio", { name: "Service account" }));
  expect(owner).toBeDisabled();
  expect(group.querySelector("input:checked")).toBeNull();
  expect(screen.getByRole("radio", { name: "Case reader" })).not.toBeDisabled();
  expect(owner).toHaveAccessibleDescription(/Not available for service accounts/u);
  expect(calls).toHaveLength(0);
});

it("disallows new case-scoped Owner grants and preserves revocation choices", async () => {
  const calls: Dispatch<PeopleInput>[] = [];
  vi.stubGlobal("fetch", peopleTransport(calls));
  render(<PeopleBinding />);
  await authorPeople("people.grant");
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "Owner" }));
  expect(screen.getByRole("radio", { name: "Owner" })).toBeChecked();
  await user.click(screen.getByRole("radio", { name: "One case" }));
  expect(screen.getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(screen.getByRole("radio", { name: "Owner" })).not.toBeChecked();
  expect(screen.getByRole("radio", { name: "Owner" })).toHaveAccessibleDescription(
    /Not available for the selected cases/u,
  );
  expect(screen.getByRole("radio", { name: "Case reader" })).not.toBeDisabled();
  await chooseOption(user, screen.getByRole("combobox", { name: "Access change" }), "people.revoke");
  await user.click(screen.getByRole("button", { name: "Discard and change action" }));
  expect(screen.getByRole("radio", { name: "Owner" })).not.toBeDisabled();
  expect(calls).toHaveLength(0);
});
