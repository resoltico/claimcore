import userEvent from "@testing-library/user-event";
import { afterEach, expect, it, vi } from "vitest";
import { render, screen } from "./presentation-test-support";
import { v3 } from "../src/api/v3";
import { LifecycleStatus } from "../src/views/LifecycleStatus";
import type { WebV3Response } from "../src/api/v3";

const review = (
  privacyPhase:
    | "ACTIVE"
    | "ERASURE_REQUESTED"
    | "ERASURE_PENDING"
    | "PAYLOAD_ERASED_SUPPRESSION_RETAINED"
    | "ERASURE_FINAL",
): WebV3Response<"lifecycle.review"> => ({
  endpoint: "lifecycle.review",
  outcome: {
    tag: "AVAILABLE",
    data: {
      businessRevision: "3",
      lifecycleSequence: "4",
      lifecycleHash: "a".repeat(64),
      disposition: "ACTIVE",
      privacyPhase,
      activeHolds: [],
      voidRequiresTwoApprovals: false,
    },
  },
});

afterEach(() => vi.restoreAllMocks());

it("shows the authoritative disposition, privacy phase, and active hold without exposing its ground", async () => {
  vi.spyOn(v3, "lifecycleReview").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      endpoint: "lifecycle.review",
      outcome: {
        tag: "AVAILABLE",
        data: {
          businessRevision: "3",
          lifecycleSequence: "4",
          lifecycleHash: "a".repeat(64),
          disposition: "VOIDED_DATA_ENTRY_ERROR",
          privacyPhase: "ERASURE_PENDING",
          activeHolds: [{ holdId: "00000000-0000-4000-8000-000000000001", reviewOn: "2026-10-01" }],
          voidRequiresTwoApprovals: true,
        },
      },
    },
  });
  render(<LifecycleStatus caseReference="CASE-1" token="synthetic" reloadSignal={0} />);
  expect(v3.lifecycleReview).not.toHaveBeenCalled();
  await userEvent.click(screen.getByText("Disposition and privacy status"));
  expect(await screen.findByText("Disposition: Voided as a data-entry error")).toBeVisible();
  expect(screen.getByText("Privacy phase: Erasure pending")).toBeVisible();
  expect(screen.getByText("HELD: active holds block payload erasure.")).toBeVisible();
  expect(screen.getByText(/Voiding this case requires two distinct approvals/u)).toBeVisible();
  expect(screen.queryByText(/hold ground/u)).toBeNull();
});

it("uses one unavailable presentation for inaccessible and absent lifecycle subjects", async () => {
  vi.spyOn(v3, "lifecycleReview").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { endpoint: "lifecycle.review", outcome: { tag: "RESOURCE_UNAVAILABLE", data: null } },
  });
  render(<LifecycleStatus caseReference="MISSING" token="synthetic" reloadSignal={0} />);
  expect(v3.lifecycleReview).not.toHaveBeenCalled();
  await userEvent.click(screen.getByText("Disposition and privacy status"));
  expect(await screen.findByText("Lifecycle status is unavailable for this case.")).toBeVisible();
  expect(screen.queryByText(/Disposition:/u)).toBeNull();
});

it.each([
  ["ACTIVE", "Active"],
  ["ERASURE_REQUESTED", "Erasure requested"],
  ["PAYLOAD_ERASED_SUPPRESSION_RETAINED", "Live payload erased; suppression evidence retained"],
  ["ERASURE_FINAL", "Managed payload erasure complete; suppression evidence retained"],
] as const)("renders %s separately from held state", async (phase, label) => {
  vi.spyOn(v3, "lifecycleReview").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: review(phase),
  });
  render(<LifecycleStatus caseReference="CASE-1" token="synthetic" reloadSignal={0} />);
  expect(v3.lifecycleReview).not.toHaveBeenCalled();
  await userEvent.click(screen.getByText("Disposition and privacy status"));
  expect(await screen.findByText(`Privacy phase: ${label}`)).toBeVisible();
  expect(screen.getByText("No active hold is recorded.")).toBeVisible();
  expect(screen.queryByText(/two distinct approvals/u)).toBeNull();
});

it("shows a safe diagnostic after a failed lifecycle review", async () => {
  vi.spyOn(v3, "lifecycleReview").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: {
      endpoint: "lifecycle.review",
      outcome: {
        tag: "FAILED",
        data: {
          code: "STORE_UNAVAILABLE",
          diagnostic: { id: "CORE_STORE_UNAVAILABLE", parameters: {} },
          message: "PRIVATE_PROVIDER_CANARY",
          recommendedAction: "RETRY_SAFE",
        },
      },
    },
  });
  render(<LifecycleStatus caseReference="CASE-1" token="synthetic" reloadSignal={0} />);
  expect(v3.lifecycleReview).not.toHaveBeenCalled();
  await userEvent.click(screen.getByText("Disposition and privacy status"));
  expect(await screen.findByRole("alert")).not.toHaveTextContent("PRIVATE_PROVIDER_CANARY");
});
