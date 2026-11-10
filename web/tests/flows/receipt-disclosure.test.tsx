import { actorRefInput } from "../support/inputs";
import { expect, it, vi } from "vitest";
import { render, screen } from "../support/presentation-test-support";
import { OperationLookup } from "../../src/views/OperationLookup";
import { authorityReceipt } from "../support/reply.fixtures";
import { definition } from "../support/service-ui.fixtures";
import { endpointResponse } from "../support/transport-response.fixtures";

it("renders the supplied receipt view without assuming a case snapshot", async () => {
  const receipt = authorityReceipt();
  vi.stubGlobal(
    "fetch",
    vi.fn().mockImplementation(() =>
      endpointResponse("operation.get", {
        representation: "HUMAN",
        outcome: {
          kind: "ACCEPTED",
          byRequester: true,
          document: {
            ...receipt,
            screen: {
              regions: (
                [
                  {
                    titleId: "label.recordDetails",
                    entries: [
                      {
                        key: "future-actor",
                        labelId: "label.recordedBy",
                        prominence: "PRIMARY",
                        value: {
                          kind: "ACTOR",
                          value: actorRefInput({
                            displayName: { kind: "NAMED", value: "Synthetic recorder" },
                          }),
                        },
                      },
                    ],
                  },
                ] as const
              ).map((section) => ({
                role: "FACTS" as const,
                titleId: section.titleId,
                content: { kind: "ENTRIES" as const, entries: section.entries },
              })),
            },
          },
        },
      }),
    ),
  );
  render(
    <OperationLookup
      definition={definition}
      initialOperationId="10000000-0000-7000-8000-000000000001"
    />,
  );
  expect(await screen.findByText("Synthetic recorder")).toBeVisible();
  expect(document.querySelector('.record-view [data-entry="claimantName"]')).toBeNull();
});
