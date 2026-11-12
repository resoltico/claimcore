import { beforeEach, expect, it, vi } from "vitest";
import userEvent from "@testing-library/user-event";
import { render, screen } from "../support/presentation-test-support";
import { current } from "../support/operation-form.fixtures";
import { activeRequester } from "../support/human-requester.fixtures";
import { closeReview } from "../support/operation-documents.fixtures";
import { authorityReview } from "../support/authority-operation.fixtures";
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
      "input:principal.kind": principal.kind,
      "input:principal.issuer": principal.issuer,
      "input:principal.subject": principal.subject,
      "input:role": "CASE_READER",
      "input:scope": "INSTALLATION",
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
      const reply = {
        representation: "HUMAN",
        operationId: bad ? "20000000-0000-7000-8000-000000000099" : input.operationId,
        outcome: {
          kind: "REVIEWED",
          review: input.kind === "CASE" ? closeReview : authorityReview(input),
        },
      };
      expect(isResponse("operation.preview", reply)).toBe(true);
      return Promise.resolve(
        new Response(JSON.stringify(reply), { headers: { "content-type": "application/json" } }),
      );
    });
    const context = {
      ...activeRequester,
      context: {
        ...activeRequester.context,
        views: ["AUTHORITY"] as const,
        tasks: ["AUTHORITY:GRANT"] as const,
      },
    };
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
    const first: unknown = JSON.parse(bodies[0]!);
    const next: unknown = JSON.parse(bodies[1]!);
    if (!isRequest("operation.preview", first) || !isRequest("operation.preview", next)) {
      throw new Error("Two typed review commands required");
    }
    const { operationId: firstId, ...firstCommand } = first;
    const { operationId: nextId, ...nextCommand } = next;
    expect(nextCommand).toEqual(firstCommand);
    expect(nextId).not.toBe(firstId);
    expect(vi.mocked(fetch).mock.calls.every(([path]) => path === "/api/operation.preview")).toBe(
      true,
    );
  },
);
