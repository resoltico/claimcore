import { act } from "@testing-library/react";
import { afterEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../../src/protocol/types";
import { useOperation, type OperationModel } from "../../../src/operation/useOperation";
import { render, waitFor } from "../../support/presentation-test-support";
import {
  operationAffordance,
  requireOperationAffordance,
} from "../../support/affordances.fixtures";
import { humanReadHttpReply } from "../../support/human-read-reply.fixtures";
const caseId = "00000000-0000-4000-8000-000000000002";
const current = (family: number): CurrentCase => ({
  screen: { regions: [] },
  subject: { kind: "CASE", caseId, revision: 7, workRevision: 2 },
  actions: [
    operationAffordance(
      "case.erase",
      {
        kind: "CASE",
        target: { kind: "ID", caseId },
        expectedRevision: 7,
        expectedFamilySequence: family,
      },
      {},
      [],
    ),
  ],
  links: [],
  history: "NONE",
  nextCursor: null,
  disclosure: "CURRENT",
  basis: {
    kind: "SNAPSHOT",
    observation: {
      observedAt: "2026-11-09T12:00:00.000000Z",
      businessDate: "2026-11-09",
      timeZone: "Etc/UTC",
    },
  },
});
afterEach(() => vi.unstubAllGlobals());
it("explicit family review acquires the new supplied erasure basis without changing the root version or dispatching a mutation", async () => {
  const previous = current(12);
  const offered = requireOperationAffordance(
    previous.actions.find((action) => action.task === "case.erase"),
  );
  const fetch = vi.fn().mockResolvedValue(humanReadHttpReply("case.get", current(13)));
  vi.stubGlobal("fetch", fetch);
  let model: OperationModel | undefined;
  const Harness = () => {
    model = useOperation({
      subject: "CASE",
      kind: "case.erase",
      current: previous,
      credential: "token",
      binding: offered.binding,
      affordance: offered,
      onLockChange: vi.fn(),
    });
    return null;
  };
  render(<Harness />);
  await act(() =>
    Promise.try(() => {
      model!.refreshPrecondition();
    }),
  );
  await waitFor(() => {
    expect(model!.bindingSource).toMatchObject({ expectedRevision: 7, expectedFamilySequence: 13 });
  });
  expect(fetch).toHaveBeenCalledTimes(1);
  expect(model!.prepared).toBeNull();
  expect(model!.phase).toBe("EDITING");
});
