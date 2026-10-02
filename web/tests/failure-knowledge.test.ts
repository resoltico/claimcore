import { expect, it, vi } from "vitest";
import { isMutationUncertain, resultNotice, v3 } from "../src/api/v3";
import { createPresenter } from "../src/presentation/presenter";
import { operationId, preparation, response } from "./v3-ui.fixtures";
import { createDraft } from "../src/domain/metadata";

it("admits definite session refusal and conservative export failure, rejecting contradictory phases", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const fetch = vi.mocked(globalThis.fetch);
  const draft = createDraft(operationId, "SYNTHETIC", "1", "CLOSE", {});
  for (const [id, code, status, phase, kind, uncertain] of [
    ["WEB_HOST_SESSION_REJECTED", "WEB_SESSION_REJECTED", 401, "NOT_STARTED", "hostFailure", false],
    ["WEB_HOST_SESSION_REJECTED", "WEB_SESSION_REJECTED", 401, null, "deliveryFailure", true],
    ["WEB_HOST_EXPORT_METADATA_INVALID", "WEB_PROTOCOL", 500, null, "hostFailure", true],
    [
      "WEB_HOST_EXPORT_METADATA_INVALID",
      "WEB_PROTOCOL",
      500,
      "NOT_STARTED",
      "deliveryFailure",
      true,
    ],
  ] as const) {
    fetch.mockResolvedValueOnce(
      new Response(
        JSON.stringify({
          kind: "HOST_FAILURE",
          code,
          status,
          executionPhase: phase,
          diagnostic: { id, parameters: {} },
          message: "Synthetic host failure",
        }),
        { status, headers: { "content-type": "application/json" } },
      ),
    );
    const result = await v3.submit(draft, "token");
    expect(result.kind).toBe(kind);
    expect(isMutationUncertain(result)).toBe(uncertain);
  }
});

it("preserves exact recovery guidance when dismissal refuses an already-started submission", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const fetch = vi.mocked(globalThis.fetch);
  for (const [code, id, action, uncertain] of [
    ["SUBMISSION_ALREADY_STARTED", "RECOVERY_SUBMISSION_ALREADY_STARTED", "RECOVER_EXACT", true],
    ["INVALID_RECOVERY_INPUT", "RECOVERY_DISMISSAL_CONFIRMATION_REQUIRED", "CORRECT_INPUT", false],
  ]) {
    fetch.mockResolvedValueOnce(
      response("recovery.dismiss", "REFUSED", {
        details: preparation,
        rejection: {
          code,
          diagnostic: { id, parameters: {} },
          message: "Synthetic refusal",
          recommendedAction: action,
        },
      }),
    );
    const result = await v3.recoveryDismiss(operationId, "a".repeat(64), "token");
    expect(result.kind).toBe("outcome");
    expect(isMutationUncertain(result)).toBe(uncertain);
  }
});

it("distinguishes cancellation boundaries from generic incomplete results in every language", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const fetch = vi.mocked(globalThis.fetch);
  const examples = [
    ["CANCELLED_BEFORE_ADMISSION", { operationId }, "cancelledBeforeAdmission"],
    ["CANCELLED_BEFORE_ATTEMPT", { preparation: preparation.summary }, "cancelledBeforeAttempt"],
  ] as const;
  for (const [tag, data, reason] of examples) {
    fetch.mockResolvedValueOnce(response("recovery.resolve", tag, data));
    const result = await v3.recoveryResolve(operationId, "a".repeat(64), "token");
    expect(result.kind).toBe("outcome");
    const notice = resultNotice(result);
    expect(notice).toEqual({ kind: "local", reason });
    expect(isMutationUncertain(result)).toBe(false);
    for (const language of ["en", "lv", "ar"] as const) {
      const presenter = createPresenter({ language, displayLocale: "en-GB" });
      expect(presenter.notice(notice)).not.toBe(
        presenter.notice({ kind: "local", reason: "incomplete" }),
      );
    }
  }
});
