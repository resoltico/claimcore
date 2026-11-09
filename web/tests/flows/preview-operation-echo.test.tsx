import { beforeEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { current } from "../support/operation-form.fixtures";
import { actorContextInput, workBasis } from "../support/inputs";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { productMetadata } from "../../src/protocol/productMetadata";
import { isRequest, isResponse } from "../../src/protocol/client";
import { RequesterContext } from "../../src/hooks/useRequesterContext";
import { useCredential } from "../../src/session/SessionContext";
import { useOperation } from "../../src/hooks/operation/useOperation";
import { OperationEditor } from "../../src/views/OperationEditor";
import { OperationPanel } from "../../src/views/operation/OperationPanel";

beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
const noLock = () => undefined;
const principal = {
  kind: "HUMAN",
  issuer: "https://issuer.example/",
  subject: "Immutable-A",
} as const;
const AuthorityEditor = () => {
  const credential = useCredential();
  const model = useOperation({
    subject: "AUTHORITY",
    kind: "GRANT",
    current: null,
    credential,
    prefill: {
      "target.kind": principal.kind,
      "target.issuer": principal.issuer,
      "target.subject": principal.subject,
      "act.role": "CASE_READER",
      "act.scope.kind": "INSTALLATION",
    },
    onLockChange: noLock,
  });
  return <OperationPanel model={model} current={null} offered={["GRANT"]} />;
};

it.each(["CASE", "AUTHORITY"] as const)(
  "rejects a mismatched %s operation ID echo and preserves its exact draft for a fresh review",
  async (kind) => {
    const bodies: string[] = [];
    vi.mocked(fetch).mockImplementation((_path, init) => {
      const raw = init?.body;
      if (typeof raw !== "string") {
        throw new Error("Expected frozen preview bytes");
      }
      const input: unknown = JSON.parse(raw);
      if (!isRequest("operation.preview", input) || input.kind !== kind) {
        throw new Error("Expected the authored operation family");
      }
      bodies.push(raw);
      const bad = bodies.length === 1;
      const snapshot = {
        caseId: current.caseId,
        caseReference: current.caseReference,
        revision: current.revision,
        state: current.state,
      };
      const target = {
        actor: {
          actorId: "10000000-0000-4000-8000-000000000001",
          displayName: { kind: "NAMED", value: "Chosen actor" },
          principalKind: "HUMAN",
          availability: "NOT_ELIGIBLE",
        },
        principal,
        contact: null,
        state: "NO_ROLES",
        roles: [],
      };
      const state = {
        enabled: true,
        profile: { displayName: "Chosen actor", contact: null },
        grants: [],
        access: [],
      };
      const reply = {
        operationId: bad ? "20000000-0000-7000-8000-000000000099" : input.operationId,
        outcome: {
          kind: "REVIEWED",
          data: {
            review: {
              view: { regions: [] },
              presentation: { changes: [], unchanged: [] },
              subject:
                kind === "CASE"
                  ? {
                      kind: "CASE",
                      before: snapshot,
                      after: { ...snapshot, revision: snapshot.revision + 1 },
                      context: { ...workBasis, productVersion: "synthetic" },
                    }
                  : {
                      kind: "AUTHORITY",
                      basis: workBasis,
                      target,
                      accessScope: { kind: "INSTALLATION" },
                      before: state,
                      after: state,
                      effects: [
                        {
                          kind: "GRANT",
                          grant: { role: "CASE_READER", scope: { kind: "INSTALLATION" } },
                        },
                      ],
                    },
            },
          },
        },
      };
      expect(isResponse("operation.preview", reply)).toBe(true);
      return Promise.resolve(
        new Response(JSON.stringify(reply), { headers: { "content-type": "application/json" } }),
      );
    });
    const context = actorContextInput({
      ...workBasis,
      productVersion: "synthetic",
      capabilities: ["MANAGE_AUTHORITY", "READ_AUTHORITY"],
      views: ["AUTHORITY"],
      offers: [{ kind: "AUTHORITY_ACT", act: "GRANT" }],
    });
    render(
      kind === "CASE" ? (
        <OperationEditor
          definition={productMetadata}
          current={current}
          initialAct="CLOSE"
          onClose={noLock}
          onCommitted={noLock}
          onMutationLockChange={noLock}
        />
      ) : (
        <RequesterContext value={context}>
          <AuthorityEditor />
        </RequesterContext>
      ),
    );
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    await screen.findByRole("alert");
    expect(document.querySelector("[data-confirm-operation]")).toBeNull();
    expect(bodies).toHaveLength(1);
    await user.click(screen.getByRole("button", { name: "Review changes" }));
    await waitForReviewConfirm();
    expect(reviewConfirm()).not.toBeDisabled();
    expect(bodies).toHaveLength(2);
    expect(bodies[1]).toBe(bodies[0]);
    expect(vi.mocked(fetch).mock.calls.every(([path]) => path === "/api/operation.preview")).toBe(
      true,
    );
  },
);
