import { type RequestByEndpoint } from "@contracts/client";
import { caseDocumentInput, closeCaseScreen } from "../support/case-document.fixtures";
import { requestText, endpointResponse } from "../support/transport-response.fixtures";
import { humanReadHttpReply as response } from "../support/human-read-reply.fixtures";
import { preview as reviewed } from "../support/operation-hook.fixtures";
import { prepareRequest } from "../../src/protocol/preparedRequest";
import { reviewConfirm, waitForReviewConfirm } from "../support/review-controls";
import { timelineDocument } from "../support/timeline-document.fixtures";
import { stubWorklistSummary } from "../support/worklist-summary.fixtures";
import { changeField } from "../support/field-entry";
import { workBasis } from "../support/inputs";
import { stubDashboardContext } from "../support/dashboard-context.fixtures";
import {
  findButton,
  getButton,
  render,
  screen,
  waitFor,
} from "../support/presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, expect, it, vi } from "vitest";
import type { CurrentCase } from "../../src/api/service";
import { Dashboard } from "../../src/views/Dashboard";
import * as operationIds from "../../src/operation/operationId";
const current: CurrentCase = caseDocumentInput(
  {
    tasks: ["case.close"],
    caseId: "20000000-0000-4000-8000-000000000001",
    revision: 1,
    history: "FULL",
  },
  closeCaseScreen,
);
import { wireReply } from "../support/reply.fixtures";
const accepted = wireReply("operation.execute");
if (accepted.outcome.kind !== "ACCEPTED") {
  throw new Error("CaseOperationReceipt fixture absent.");
}
const receipt = accepted.outcome.document;
const renderDashboard = () => render(<Dashboard />);
beforeEach(() => {
  vi.stubGlobal("fetch", vi.fn());
  stubDashboardContext();
});
const queueDefinitionAndList = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    response("case.list", {
      screen: {
        regions: [
          {
            role: "RESULTS",
            content: {
              kind: "TABLE",
              columns: [
                {
                  key: "caseReference",
                  labelId: "field.caseReference.label",
                  prominence: "PRIMARY",
                  isMoney: false,
                },
              ],
              rows: [
                {
                  key: "20000000-0000-4000-8000-000000000001",
                  values: [
                    {
                      kind: "REFERENCE",
                      value: "CASE-1",
                    },
                  ],
                },
              ],
            },
          },
        ],
      },
      context: {
        kind: "CASES",
        cases: [
          {
            caseId: "20000000-0000-4000-8000-000000000001",
            assignment: null,
          },
        ],
        tasks: ["case.open"],
        exactCaseId: null,
        nextCursor: null,
      },
      basis: {
        kind: "SNAPSHOT",
        observation: workBasis,
      },
      disclosure: "CURRENT",
    }),
  );
};
const queueDetail = () => {
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(
    endpointResponse("case.get", {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: current,
      },
    }),
  );
  fetch.mockResolvedValueOnce(response("case.timeline", timelineDocument()));
};
it("returns from a selected case through Dashboard's detail-back transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(
    await screen.findByRole(
      "link",
      {
        name: "CASE-1",
      },
      {
        timeout: 5_000,
      },
    ),
  );
  expect(
    await screen.findByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
    }),
  ).toBeVisible();
  await user.click(getButton("Back to cases"));
  expect(
    await screen.findByRole("heading", {
      name: "Cases",
    }),
  ).toBeVisible();
});
it("opens a metadata-derived act from Dashboard's current-case transition", async () => {
  const user = userEvent.setup();
  queueDefinitionAndList();
  queueDetail();
  renderDashboard();
  await user.click(
    await screen.findByRole("link", {
      name: "CASE-1",
    }),
  );
  vi.mocked(globalThis.fetch).mockResolvedValueOnce(
    endpointResponse("case.get", {
      representation: "HUMAN",
      outcome: {
        kind: "SUCCEEDED",
        document: current,
      },
    }),
  );
  await user.click(await findButton(/Close the case/u));
  expect(
    await screen.findByRole("heading", {
      name: "Close the case",
    }),
  ).toBeVisible();
  expect(document.querySelector('.workspace-sidebar a[href="/people"]')).not.toHaveAttribute(
    "aria-disabled",
    "true",
  );
  expect(
    screen.queryByRole("button", {
      name: "Sign out",
    }),
  ).toBeNull();
  await user.click(
    screen.getAllByRole("button", {
      name: "Close",
    })[0]!,
  );
  await user.click(document.querySelector(".account-menu > summary")!);
  expect(getButton("Sign out")).not.toBeDisabled();
  expect(
    await screen.findByRole("heading", {
      name: (_name, element) => element.id === "case-detail-title",
    }),
  ).toBeVisible();
});
it("returns an accepted open operation to Dashboard through its committed transition", async () => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const creation = operationIds.operationId();
  vi.spyOn(operationIds, "operationId").mockReturnValue(creation);
  fetch.mockImplementation((path, init) => {
    if (typeof path !== "string") {
      throw new Error("Expected an API path");
    }
    switch (path) {
      case "/api/case.list":
        return Promise.resolve(
          response("case.list", {
            screen: {
              regions: [
                {
                  role: "RESULTS",
                  content: {
                    kind: "TABLE",
                    columns: [
                      {
                        key: "caseReference",
                        labelId: "field.caseReference.label",
                        prominence: "PRIMARY",
                        isMoney: false,
                      },
                    ],
                    rows: [],
                  },
                },
              ],
            },
            context: {
              kind: "CASES",
              cases: [],
              tasks: ["case.open"],
              exactCaseId: null,
              nextCursor: null,
            },
            basis: {
              kind: "SNAPSHOT",
              observation: workBasis,
            },
            disclosure: "CURRENT",
          }),
        );
      case "/api/operation.preview": {
        const operationPreviewRequest = JSON.parse(
          requestText(init?.body),
        ) as RequestByEndpoint["operation.preview"];
        const reply = reviewed(prepareRequest("operation.execute", operationPreviewRequest));
        if (reply.kind !== "outcome" || reply.value.outcome.kind !== "REVIEWED") {
          throw new Error("Current review fixture required");
        }
        return Promise.resolve(
          endpointResponse("operation.preview", {
            ...reply.value,
            outcome: {
              kind: "REVIEWED",
              review: {
                ...reply.value.outcome.review,
                document: {
                  ...reply.value.outcome.review.document,
                  basis: {
                    kind: "SNAPSHOT",
                    observation: workBasis,
                  },
                },
              },
            },
          }),
        );
      }
      case "/api/operation.execute":
        return Promise.resolve(
          endpointResponse("operation.execute", {
            representation: "HUMAN",
            outcome: {
              kind: "ACCEPTED",
              document: {
                ...receipt,
                context: {
                  kind: "RECEIPT",
                  recordedFields: [],
                  target: {
                    kind: "CASE",
                    caseId: creation,
                    revision: 1,
                  },
                  caseLink: creation,
                  refreshCase: creation,
                },
              },
            },
          }),
        );
      case "/api/operation.get":
        return Promise.resolve(
          endpointResponse("operation.get", {
            representation: "HUMAN",
            outcome: {
              kind: "ACCEPTED",
              byRequester: true,
              document: {
                ...receipt,
                context: {
                  kind: "RECEIPT",
                  recordedFields: [],
                  target: {
                    kind: "CASE",
                    caseId: creation,
                    revision: 1,
                  },
                  caseLink: creation,
                  refreshCase: creation,
                },
              },
            },
          }),
        );
      case "/api/case.get":
        return Promise.resolve(
          endpointResponse("case.get", {
            representation: "HUMAN",
            outcome: {
              kind: "SUCCEEDED",
              document: {
                ...current,
                context: {
                  ...current.context,
                  caseId: creation,
                },
              },
            },
          }),
        );
      case "/api/case.timeline":
        return Promise.resolve(response("case.timeline", timelineDocument()));
      default:
        throw new Error(`Unexpected transition request: ${path}`);
    }
  });
  renderDashboard();
  await user.click(
    await screen.findByRole("link", {
      name: "Open a case",
    }),
  );
  await screen.findByLabelText("Case reference");
  const fill = (label: string, value: string): void => {
    changeField(label, value);
  };
  fill("Case reference", "CASE-1");
  fill("Incident date", "2026-09-01");
  fill("Notification date", "2026-09-02");
  fill("Country", "LV");
  fill("Claimant name", "Synthetic claimant");
  fill("Insurer", "Synthetic insurer");
  fill("Claimed amount", "12.34");
  fill("Claimed currency", "EUR");
  await user.click(getButton("Review changes"));
  await waitForReviewConfirm();
  await user.click(reviewConfirm());
  const heading = await screen.findByRole("heading", {
    name: (_name, element) => element.id === "case-detail-title",
  });
  expect(heading).toBeVisible();
  await waitFor(() => {
    expect(heading).toHaveFocus();
  });
  const currentRead = fetch.mock.calls.findLast(([path]) => path === "/api/case.get");
  expect(JSON.parse(typeof currentRead?.[1]?.body === "string" ? currentRead[1].body : "")).toEqual(
    {
      target: {
        kind: "ID",
        caseId: creation,
      },
    },
  );
});
beforeEach(() => {
  stubWorklistSummary();
});
