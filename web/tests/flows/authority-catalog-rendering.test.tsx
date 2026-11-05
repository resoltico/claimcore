import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { chooseOption, optionValues } from "../support/choice-entry";
import { workBasis, actorContextInput } from "../support/inputs";
import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { getButton, render, screen, waitFor } from "../support/presentation-test-support";
import { useState } from "react";
import type { OperationReceipt } from "@contracts/client";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { useCredential } from "../../src/session/SessionContext";
import { OperationPanel, OperationDiscard } from "../../src/views/operation/OperationPanel";
import { prepareRequest } from "../../src/protocol/preparedRequest";
import { authorityReview, authorityAcceptance } from "../support/authority-operation.fixtures";
import { productMetadata } from "../../src/protocol/productMetadata";
import { isRequest } from "../../src/protocol/client";
import type { RequestByEndpoint } from "@contracts/client";
import { installationOffers } from "../support/installation-advice.fixtures";
import { runtime, input, response, type Dispatch } from "../support/catalog-transport.fixtures";
afterEach(() => vi.unstubAllGlobals());
type AuthorityInput = Extract<
  RequestByEndpoint["operation.execute"],
  {
    kind: "AUTHORITY";
  }
>;
const authorityActs = productMetadata.catalog.entries
  .filter((entry) => entry.endpoint === "operation.execute" && entry.subject === "AUTHORITY")
  .map((entry) => entry as typeof entry & { kind: AuthorityInput["act"]["kind"] });
const authorityReceipt = (request: AuthorityInput) => {
  const value = authorityAcceptance(prepareRequest("operation.execute", request));
  if (value.kind !== "outcome" || value.value.outcome.kind !== "ACCEPTED") {
    throw new Error("Authority acceptance fixture required");
  }
  return value.value.outcome.data;
};
const AuthorityBinding = () => {
  const credential = useCredential();
  const [recorded, setRecorded] = useState<OperationReceipt | null>(null);
  const model = useOperation({
    subject: "AUTHORITY",
    kind: "REGISTER",
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
        offered={authorityActs.map((entry) => entry.kind)}
      />
      {recorded === null ? null : <p role="status">{recorded.operationId}</p>}
    </>
  );
};
const contextReply = (): Response =>
  response("context", {
    outcome: {
      kind: "SUCCEEDED",
      data: {
        requester: actorContextInput({
          ...runtime,
          capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
          views: ["AUTHORITY", "OPERATIONS"],
          offers: installationOffers([
            "REGISTER",
            "ADMIT",
            "GRANT",
            "REVOKE",
            "ENABLE",
            "DISABLE",
            "SET_PROFILE",
            "ERASE_PROFILE",
          ]),
        }),
      },
    },
  });
const authorityPreview = (parsed: AuthorityInput) =>
  response("operation.preview", {
    operationId: parsed.operationId,
    outcome: { kind: "REVIEWED", data: { review: authorityReview(parsed) } },
  });
const authorityTransport = (calls: Dispatch<AuthorityInput>[]) =>
  vi.fn<typeof globalThis.fetch>().mockImplementation((path, init) => {
    if (path === "/api/context") {
      return Promise.resolve(contextReply());
    }
    if (path === "/api/authority.list") {
      return Promise.resolve(
        response("authority.list", {
          outcome: {
            kind: "SUCCEEDED",
            data: { basis: workBasis, items: [], nextCursor: null },
          },
        }),
      );
    }
    if (
      typeof path !== "string" ||
      !["/api/operation.execute", "/api/operation.preview"].includes(path) ||
      typeof init?.body !== "string"
    ) {
      throw new Error("Unexpected authority endpoint.");
    }
    const parsed: unknown = JSON.parse(init.body);
    if (!isRequest("operation.execute", parsed) || parsed.kind !== "AUTHORITY") {
      throw new Error("Production authority form did not produce a complete request.");
    }
    calls.push({
      path,
      bytes: init.body,
      input: parsed,
    });
    if (path === "/api/operation.preview") {
      return Promise.resolve(authorityPreview(parsed));
    }
    return Promise.resolve(
      response("operation.execute", {
        operationId: parsed.operationId,
        outcome: {
          kind: "ACCEPTED",
          data: authorityReceipt(parsed),
        },
      }),
    );
  });
const authorAuthority = async (kind: AuthorityInput["act"]["kind"]) => {
  const user = userEvent.setup();
  const control = await screen.findByRole("combobox", { name: "Authority action" });
  await user.click(control);
  await waitFor(() => {
    expect(optionValues(control)).toHaveLength(8);
  });
  await chooseOption(user, control, kind);
  input("target.issuer", "https://issuer.example/");
  input("target.subject", "synthetic-catalogue-human");
  input("target.kind", "HUMAN");
  if (kind === "REGISTER" || kind === "SET_PROFILE" || kind === "ADMIT") {
    input("act.profile.displayName", "Synthetic profile name");
  }
  if (kind === "ADMIT") {
    await user.click(screen.getByRole("radio", { name: "Case reader" }));
  }
  if (kind === "GRANT" || kind === "REVOKE") {
    input("act.role", "CASE_READER");
  }
};
it.each(authorityActs)(
  "renders, reviews and submits real authority act $kind through production transport",
  async (act) => {
    expect(authorityActs).toHaveLength(8);
    const calls: Dispatch<AuthorityInput>[] = [];
    vi.stubGlobal("fetch", authorityTransport(calls));
    render(<AuthorityBinding />);
    await authorAuthority(act.kind);
    const user = userEvent.setup();
    await user.click(getButton("Review changes"));
    await waitForReviewConfirm();
    const before = screen.getByRole("dialog").textContent;
    await waitForReviewConfirm();
    if (act.kind === "ERASE_PROFILE") {
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
    await screen.findByText(calls[0]!.input.operationId, {
      exact: true,
    });
    expect(before).toContain(calls[0]!.input.operationId);
    expect(calls[0]!.input.act.kind).toBe(act.kind);
    expect(calls[0]!.input.target).toEqual({
      kind: "HUMAN",
      issuer: "https://issuer.example/",
      subject: "synthetic-catalogue-human",
    });
    if (calls[0]!.input.act.kind === "GRANT" || calls[0]!.input.act.kind === "REVOKE") {
      expect(calls[0]!.input.act).toMatchObject({
        role: "CASE_READER",
        scope: {
          kind: "INSTALLATION",
        },
      });
    }
    if (calls[0]!.input.act.kind === "ENABLE") {
      expect(calls[0]!.input.act).toEqual({
        kind: "ENABLE",
      });
    }
  },
);

it("requires an explicit role and disables service-incompatible roles from catalog admission", async () => {
  const calls: Dispatch<AuthorityInput>[] = [];
  vi.stubGlobal("fetch", authorityTransport(calls));
  render(<AuthorityBinding />);
  const user = userEvent.setup();
  const control = await screen.findByRole("combobox", { name: "Authority action" });
  await user.click(control);
  await waitFor(() => {
    expect(optionValues(control)).toHaveLength(8);
  });
  await chooseOption(user, screen.getByRole("combobox", { name: "Authority action" }), "GRANT");
  const group = screen.getByRole("radiogroup", { name: "Role" });
  expect(group.querySelector("input:checked")).toBeNull();
  const owner = screen.getByRole("radio", { name: "Owner" });
  await user.click(owner);
  expect(owner).toBeChecked();
  await user.click(screen.getByRole("radio", { name: "Service" }));
  expect(owner).toBeDisabled();
  expect(group.querySelector("input:checked")).toBeNull();
  expect(screen.getByRole("radio", { name: "Case reader" })).not.toBeDisabled();
  expect(owner).toHaveAccessibleDescription(/Not available for service principals/u);
  expect(calls).toHaveLength(0);
});

it("disallows new case-scoped Owner grants and preserves revocation choices", async () => {
  const calls: Dispatch<AuthorityInput>[] = [];
  vi.stubGlobal("fetch", authorityTransport(calls));
  render(<AuthorityBinding />);
  await authorAuthority("GRANT");
  const user = userEvent.setup();
  await user.click(screen.getByRole("radio", { name: "Owner" }));
  expect(screen.getByRole("radio", { name: "Owner" })).toBeChecked();
  await user.click(screen.getByRole("radio", { name: "One case" }));
  expect(screen.getByRole("radio", { name: "Owner" })).toBeDisabled();
  expect(screen.getByRole("radio", { name: "Owner" })).not.toBeChecked();
  expect(screen.getByRole("radio", { name: "Owner" })).toHaveAccessibleDescription(
    /No capability applies at this scope/u,
  );
  expect(screen.getByRole("radio", { name: "Case reader" })).not.toBeDisabled();
  await chooseOption(user, screen.getByRole("combobox", { name: "Authority action" }), "REVOKE");
  await user.click(screen.getByRole("button", { name: "Discard and change action" }));
  expect(screen.getByRole("radio", { name: "Owner" })).not.toBeDisabled();
  expect(calls).toHaveLength(0);
});
