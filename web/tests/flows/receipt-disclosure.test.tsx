import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { OperationLookup } from "../../src/views/OperationLookup";
import { authorityReceipt } from "../support/reply.fixtures";
import { definition } from "../support/service-ui.fixtures";
import { transportResponse } from "../support/transport-response.fixtures";

it("renders the supplied receipt view without assuming a case snapshot", async () => {
  const receipt = authorityReceipt();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      transportResponse("operation.get", "ACCEPTED", { receipt, byRequester: true }, 200, {
        kind: "RECORD",
        sections: [
          {
            titleId: "label.recordDetails",
            entries: [
              {
                key: "future-actor",
                labelId: "label.recordedBy",
                prominence: "PRIMARY",
                value: { kind: "ACTOR", value: receipt.recordedBy },
              },
            ],
          },
        ],
      }),
    ),
  );
  render(<OperationLookup definition={definition} initialOperationId={receipt.operationId} />);
  expect(await screen.findByText("Synthetic recorder")).toBeVisible();
  expect(document.querySelector('.record-view [data-entry="claimantName"]')).toBeNull();
});
