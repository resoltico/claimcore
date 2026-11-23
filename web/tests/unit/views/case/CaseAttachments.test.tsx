import { act, fireEvent, within } from "@testing-library/react";
import { service } from "../../../../src/api/service";
import { operationAffordance } from "../../../support/affordances.fixtures";
import { closeReview, closeReceipt } from "../../../support/operation-documents.fixtures";
import { openedTimelineEntry, timelineDocument } from "../../../support/timeline-document.fixtures";
import { waitForReviewConfirm } from "../../../support/review-controls";
import userEvent from "@testing-library/user-event";
import type { HumanDocument, RegionRow, ResponseFor } from "@contracts/client";
import { afterEach, expect, test, vi } from "vitest";
import { useEndpointCache } from "../../../../src/hooks/EndpointProvider";
import { RequesterContext } from "../../../../src/hooks/useRequesterContext";
import { JournalContext } from "../../../../src/recovery/JournalContext";
import { RecoveryProvider } from "../../../../src/recovery/RecoveryContext";
import { ObservedReceipt } from "../../../../src/views/operation/ObservedReceipt";
import { recoveryRecord } from "../../../support/recovery.fixtures";
import type { EndpointCache } from "../../../../src/hooks/endpointCache";
import { CaseDetail } from "../../../../src/views/CaseDetail";
import { CaseAttachments } from "../../../../src/views/case/CaseAttachments";
import { mockEndpoint } from "../../../support/endpoint-mocks";
import { humanReadReply } from "../../../support/human-read-reply.fixtures";
import { current } from "../../../support/operation-form.fixtures";
import { activeRequester } from "../../../support/human-requester.fixtures";
import type { CurrentCase } from "../../../../src/protocol/types";
import { definition } from "../../../support/service-ui.fixtures";
import {
  render,
  screen,
  testWorkspaceIdentity,
  waitFor,
} from "../../../support/presentation-test-support";

const selected = { kind: "ID" as const, caseId: current.subject.caseId };
const filename = "private_disclosure_canary.png";
const attachmentId = "a0000000-0000-4000-8000-000000000001";
const caseWithAttachments = {
  ...current,
  links: [
    {
      rel: "ATTACHMENTS",
      labelId: "section.attachments.label",
      link: { endpoint: "case.attachments", input: { target: selected } },
    },
  ],
} as const;
const attachmentList = (actions: NonNullable<RegionRow["actions"]> = []): HumanDocument => ({
  ...current,
  subject: { kind: "LIST" },
  attachmentObservation: { caseId: selected.caseId, sequence: 3 },
  actions: [],
  screen: {
    regions: [
      {
        role: "ATTACHMENTS",
        titleId: "section.attachments.label",
        content: {
          kind: "TABLE",
          columns: [
            {
              key: "attachmentName",
              labelId: "field.attachmentName.label",
              prominence: "PRIMARY",
              isMoney: false,
            },
          ],
          rows: [
            {
              key: attachmentId,
              actions,
              values: [{ kind: "NARRATIVE", value: filename }],
              transfers: [
                {
                  kind: "DOWNLOAD",
                  labelId: "endpoint.attachment.download.label",
                  request: { target: selected, attachmentId },
                  name: filename,
                },
                {
                  kind: "PREVIEW",
                  labelId: "endpoint.attachment.preview.label",
                  request: { target: selected, attachmentId },
                  name: filename,
                },
              ],
            },
          ],
        },
      },
    ],
  },
});
const admitted = attachmentList();

const CacheDriver = ({ capture }: { capture: (value: EndpointCache) => void }) => {
  const cache = useEndpointCache()!;
  capture(cache);
  return null;
};
const setup = async () => {
  const scope = new Array<EndpointCache>();
  const changed = vi.fn();
  const list = mockEndpoint("case.attachments");
  list.mockResolvedValueOnce({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.attachments", admitted),
  });
  const pendingList = Promise.withResolvers<Awaited<ReturnType<typeof list>>>();
  list.mockReturnValue(pendingList.promise);
  const workspace = (
    available: boolean,
    selectedCase: CurrentCase = caseWithAttachments,
    requester = activeRequester,
  ) => (
    <RequesterContext value={requester}>
      <CacheDriver
        capture={(value) => {
          scope[0] = value;
        }}
      />
      <CaseAttachments
        current={selectedCase}
        editing={false}
        disclosureAvailable={available}
        authoringAvailable
        onAdd={vi.fn()}
        onWithdraw={vi.fn()}
        onDisclosureChanged={changed}
      />
    </RequesterContext>
  );
  const { rerender } = render(workspace(true));
  expect(await screen.findByText(filename)).toBeVisible();
  return {
    changed,
    list,
    cache: scope[0]!,
    setAvailable: (available: boolean) => {
      rerender(workspace(available));
    },
    changeSelection: () => {
      rerender(
        workspace(true, {
          ...caseWithAttachments,
          subject: { ...current.subject, caseId: "b0000000-0000-4000-8000-000000000042" },
        }),
      );
    },
    changeOwner: () => {
      rerender(
        workspace(true, caseWithAttachments, {
          ...activeRequester,
          subject: {
            ...activeRequester.subject,
            actorId: "a0000000-0000-4000-8000-000000000042",
          },
        }),
      );
    },
  };
};

const headers = {
  "content-type": "application/json",
  "ClaimCore-Installation": testWorkspaceIdentity.installationId,
};
const download = () => userEvent.setup().click(screen.getByRole("button", { name: "Download" }));
const resourceUnavailable = {
  outcome: {
    kind: "REJECTED",
    rejection: {
      kind: "REFUSED",
      advice: {
        message: { messageId: "diagnostic.RESOURCE_UNAVAILABLE", arguments: [] },
        action: "ASK_FOR_ACCESS",
      },
    },
  },
} as const;
const sessionRejected = { hostFailure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" } };

afterEach(() => vi.unstubAllGlobals());

test.each([
  [200, resourceUnavailable],
  [401, sessionRejected],
])(
  "masks stale attachment rows and preserves feedback after a disclosure refusal with status %i",
  async (status, body) => {
    vi.stubGlobal("fetch", () =>
      Promise.resolve(new Response(JSON.stringify(body), { status, headers })),
    );
    const { changed, list } = await setup();
    await download();
    await waitFor(() => {
      expect(changed).toHaveBeenCalledTimes(1);
    });
    expect(list).toHaveBeenCalledTimes(2);
    expect(screen.queryByText(filename)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();
    expect(await screen.findByRole("alert")).toBeVisible();
  },
);

test("keeps download focused and retries by keyboard after a typed store fault", async () => {
  const failure = () =>
    new Response(
      JSON.stringify({
        outcome: {
          kind: "FAILED",
          fault: {
            code: "CORE_STORE_UNAVAILABLE",
            action: "RETRY_SAFE",
            messageId: "fault.CORE_STORE_UNAVAILABLE",
          },
        },
      }),
      { headers },
    );
  const held = Promise.withResolvers<Response>();
  const fetch = vi
    .fn()
    .mockReturnValueOnce(held.promise)
    .mockImplementation(() => Promise.resolve(failure()));
  vi.stubGlobal("fetch", fetch);
  const { changed, list } = await setup();
  const button = screen.getByRole("button", { name: "Download" });
  const user = userEvent.setup();
  act(() => {
    button.focus();
  });
  await user.keyboard("{Enter}");
  expect(button).toHaveAttribute("aria-disabled", "true");
  expect(button).toHaveFocus();
  expect(fetch).toHaveBeenCalledTimes(1);
  await user.keyboard("{Enter}");
  expect(fetch).toHaveBeenCalledTimes(1);

  await act(async () => {
    held.resolve(failure());
    await held.promise;
  });
  expect(await screen.findByRole("alert")).toHaveTextContent("ClaimCore storage is unavailable.");
  expect(changed).not.toHaveBeenCalled();
  expect(list).toHaveBeenCalledTimes(1);
  expect(screen.getByText(filename)).toBeVisible();
  expect(button).toBeEnabled();
  expect(button).toHaveFocus();
  await user.keyboard("{Enter}");
  expect(fetch).toHaveBeenCalledTimes(2);
});

const mockPreview = (digest: () => Promise<ArrayBuffer>, url: string, mediaType = "image/png") => {
  vi.stubGlobal("crypto", { subtle: { digest } });
  vi.stubGlobal("fetch", () =>
    Promise.resolve(
      new Response("abc", {
        headers: {
          "content-type": mediaType,
          "content-length": "3",
          "content-encoding": "identity",
          "ClaimCore-Installation": testWorkspaceIdentity.installationId,
          "cache-control": "no-store, no-transform",
          "Content-Digest": "sha-256=:ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=:",
        },
      }),
    ),
  );
  const create = vi.fn(() => url);
  const revoke = vi.fn();
  const OriginalUrl = URL;
  vi.stubGlobal(
    "URL",
    class extends OriginalUrl {
      static override createObjectURL = create;
      static override revokeObjectURL = revoke;
    },
  );
  return { create, revoke };
};

test("opening the same PDF again always replaces the viewer frame", async () => {
  const hash = Uint8Array.from(atob("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0="), (byte) =>
    byte.charCodeAt(0),
  ).buffer;
  const digest = vi.fn(() => Promise.resolve(hash));
  mockPreview(digest, "blob:unexpected", "application/pdf");
  await setup();
  const preview = screen.getByRole("button", { name: "Preview" });
  await userEvent.click(preview);
  const previous = await screen.findByTitle("Preview");
  await act(async () => {
    fireEvent.click(preview);
    await vi.waitFor(() => {
      expect(digest).toHaveBeenCalledTimes(2);
    });
  });
  expect(screen.getByTitle("Preview")).not.toBe(previous);
  expect(previous).not.toBeInTheDocument();
});

test.each(["selection", "owner", "withdrawal", "erasure", "access refusal"] as const)(
  "removes a PDF frame with a pending page after %s and ignores its late reply",
  async (cause) => {
    const hash = Uint8Array.from(atob("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0="), (byte) =>
      byte.charCodeAt(0),
    ).buffer;
    const { create } = mockPreview(
      () => Promise.resolve(hash),
      "blob:unexpected",
      "application/pdf",
    );
    const port: {
      onmessage: ((event: MessageEvent<unknown>) => void) | null;
      close: () => void;
      postMessage: (message: unknown) => void;
    } = { onmessage: null, close: vi.fn(), postMessage: vi.fn() };
    function channel() {
      return { port1: port, port2: {} };
    }
    vi.stubGlobal("MessageChannel", channel);
    const { cache, list, changed, changeOwner, changeSelection } = await setup();
    const user = userEvent.setup();
    await user.click(screen.getByRole("button", { name: "Preview" }));
    const frame = await screen.findByTitle("Preview");
    if (!(frame instanceof HTMLIFrameElement) || frame.contentWindow === null) {
      throw new Error("Expected an admitted PDF frame.");
    }
    vi.spyOn(frame.contentWindow, "postMessage").mockReturnValue(undefined);
    fireEvent.load(frame);
    const reply = port.onmessage;
    if (reply === null) {
      throw new Error("Expected a connection to the PDF frame.");
    }
    const rendered = (requestId: number, page: number) => {
      act(() => {
        reply(new MessageEvent("message", { data: { requestId, page, pageCount: 2 } }));
      });
    };
    rendered(1, 1);
    expect(screen.getByText("Page 1 of 2")).toBeVisible();
    await user.click(screen.getByRole("button", { name: "Next page" }));
    expect(port.postMessage).toHaveBeenCalledWith({ requestId: 2, page: 2 });

    switch (cause) {
      case "selection":
        changeSelection();
        break;
      case "owner":
        changeOwner();
        break;
      case "withdrawal":
        act(() => {
          cache.confirmAttachmentChange(selected.caseId, 4);
        });
        break;
      case "erasure":
        act(() => {
          cache.observeAttachmentErasure(selected.caseId);
        });
        break;
      case "access refusal":
        list.mockResolvedValue({
          kind: "outcome",
          status: 200,
          value: { representation: "HUMAN", ...resourceUnavailable },
        });
        act(() => {
          cache.invalidate();
        });
        await waitFor(() => {
          expect(changed).toHaveBeenCalledTimes(1);
        });
        break;
    }
    expect(port.close).toHaveBeenCalledTimes(1);
    expect(frame).not.toBeInTheDocument();
    rendered(2, 2);
    expect(screen.queryByText("Page 2 of 2")).not.toBeInTheDocument();
    expect(create).not.toHaveBeenCalled();
  },
);

test("discards a valid preview whose digest completes after a newer attachment disclosure", async () => {
  const pendingHash = Promise.withResolvers<ArrayBuffer>();
  const hash = vi.fn(() => pendingHash.promise);
  const { create } = mockPreview(hash, "blob:late-preview");
  const { cache } = await setup();
  await userEvent.setup().click(screen.getByRole("button", { name: "Preview" }));
  await waitFor(() => {
    expect(hash).toHaveBeenCalledTimes(1);
  });
  act(() => {
    cache.observeAttachmentDisclosure(selected.caseId, 4);
  });
  expect(screen.queryByText(filename)).not.toBeInTheDocument();
  await act(async () => {
    pendingHash.resolve(
      Uint8Array.from(atob("ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0="), (byte) =>
        byte.charCodeAt(0),
      ).buffer,
    );
    await pendingHash.promise;
  });
  expect(create).not.toHaveBeenCalled();
  expect(screen.queryByRole("img", { name: filename })).not.toBeInTheDocument();
});

test("revalidates the case once and masks private facts after the attachment list rejects the session", async () => {
  const fact = "PRIVATE-POLICY-CANARY";
  const caseReply = mockEndpoint("case.get");
  caseReply
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: humanReadReply("case.get", {
        ...caseWithAttachments,
        history: "NONE",
        screen: {
          regions: [
            {
              role: "FACTS",
              content: {
                kind: "ENTRIES",
                entries: [
                  {
                    key: "policyNumber",
                    labelId: "field.policyNumber.label",
                    prominence: "PRIMARY",
                    value: { kind: "TEXT", value: fact },
                  },
                ],
              },
            },
          ],
        },
      }),
    })
    .mockResolvedValue({
      kind: "hostFailure",
      status: 401,
      failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
    });
  const list = mockEndpoint("case.attachments");
  const observed = Promise.withResolvers<Awaited<ReturnType<typeof list>>>();
  list.mockReturnValue(observed.promise);

  render(
    <CaseDetail
      target={selected}
      definition={definition.catalog}
      reloadSignal={0}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );
  expect(await screen.findByText(fact)).toBeVisible();
  await act(async () => {
    observed.resolve({
      kind: "hostFailure",
      status: 401,
      failure: { code: "SESSION_REJECTED", phase: "NOT_STARTED" },
    });
    await observed.promise;
  });

  expect(caseReply).toHaveBeenCalledTimes(2);
  expect(list).toHaveBeenCalledTimes(1);
  expect(screen.queryByText(fact)).not.toBeInTheDocument();
  expect(screen.getAllByText(/You are signed out/u)[0]).toBeVisible();
});

test("a store fault retains the last admitted list only while its disclosure sequence is current", async () => {
  const { cache, changed, list } = await setup();
  const fault = {
    representation: "HUMAN",
    outcome: {
      kind: "FAILED",
      fault: {
        code: "CORE_STORE_UNAVAILABLE",
        action: "RETRY_SAFE",
        messageId: "fault.CORE_STORE_UNAVAILABLE",
      },
    },
  } satisfies ResponseFor<"case.attachments", "HUMAN">;
  list.mockResolvedValue({ kind: "outcome", status: 200, value: fault });
  act(() => {
    cache.invalidate();
  });
  expect(await screen.findByRole("alert")).toBeVisible();
  expect(screen.getByText(filename)).toBeVisible();
  expect(screen.getByRole("button", { name: "Download" })).toBeEnabled();
  expect(changed).not.toHaveBeenCalled();

  act(() => {
    cache.observeAttachmentDisclosure(selected.caseId, 4);
  });
  expect(await screen.findByRole("alert")).toBeVisible();
  expect(screen.queryByText(filename)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();
  expect(changed).not.toHaveBeenCalled();
});

test("reports one list access refusal across a parent revalidation", async () => {
  const { cache, changed, list, setAvailable } = await setup();
  list.mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: { representation: "HUMAN", ...resourceUnavailable },
  });
  act(() => {
    cache.invalidate();
  });
  expect(await screen.findByRole("alert")).toBeVisible();
  expect(changed).toHaveBeenCalledTimes(1);
  expect(screen.queryByText(filename)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();

  setAvailable(false);
  setAvailable(true);
  expect(screen.getByRole("alert")).toBeVisible();
  expect(changed).toHaveBeenCalledTimes(1);
  expect(list).toHaveBeenCalledTimes(2);
});

const attachmentObservation = (sequence: number) => ({ caseId: selected.caseId, sequence });
const receiptCaseLink = {
  rel: "OPEN_CASE",
  labelId: "label.openCase",
  link: { endpoint: "case.get", input: { target: selected } },
} as const;
const inspectTimelineDetails = async (text: string) => {
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: "View details" }));
  expect(await screen.findByText(text)).toBeVisible();
  await user.keyboard("{Escape}");
};

const historyWithAttachment = () => {
  const historical = "private history filename";
  const old = timelineDocument([
    {
      ...openedTimelineEntry,
      highlights: [
        {
          titleId: "section.attachments.label",
          entries: [
            {
              key: "attachmentName",
              labelId: "field.attachmentName.label",
              prominence: "PRIMARY",
              value: { kind: "NARRATIVE", value: historical },
            },
          ],
        },
      ],
    },
  ]);
  return {
    historical,
    document: { ...old, attachmentObservation: attachmentObservation(3) },
  };
};
const withdrawalAction = operationAffordance(
  "attachment.withdraw",
  { kind: "ATTACHMENT", target: selected, attachmentId },
  {},
  [],
);
const withdrawable = attachmentList([withdrawalAction]);
const withdrawalReceipt = (operationId: string): HumanDocument => ({
  ...closeReceipt,
  attachmentObservation: attachmentObservation(5),
  subject: { kind: "RECEIPT", operationId },
  links: [],
});
const mockWithdrawalReview = () =>
  vi.spyOn(service, "preview").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: {
          kind: "REVIEWED",
          review: {
            ...closeReview,
            document: {
              ...closeReview.document,
              subject: {
                ...closeReview.document.subject,
                kind: "REVIEW",
                consequence: "STEWARDSHIP",
                confirmation: { kind: "CASE_REFERENCE", reference: "CASE-SERIALIZATION" },
              },
              actions: [withdrawalAction],
            },
          },
        },
      },
    }),
  );
const mountWithdrawableCase = () =>
  render(
    <CaseDetail
      target={selected}
      reloadSignal={0}
      definition={definition.catalog}
      onBack={vi.fn()}
      onAct={vi.fn()}
    />,
  );

test("confirmed withdrawal masks old attachments and history through a list fault and a delayed old timeline", async () => {
  mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.get", caseWithAttachments),
  });
  const list = mockEndpoint("case.attachments");
  list
    .mockResolvedValueOnce({
      kind: "outcome",
      status: 200,
      value: humanReadReply("case.attachments", withdrawable),
    })
    .mockResolvedValue({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        outcome: {
          kind: "FAILED",
          fault: {
            code: "CORE_STORE_UNAVAILABLE",
            action: "RETRY_SAFE",
            messageId: "fault.CORE_STORE_UNAVAILABLE",
          },
        },
      },
    });
  const old = historyWithAttachment();
  const timeline = mockEndpoint("case.timeline");
  const stale = Promise.withResolvers<Awaited<ReturnType<typeof timeline>>>();
  const oldPage = {
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.timeline", old.document),
  } as const;
  timeline.mockResolvedValueOnce(oldPage).mockReturnValue(stale.promise);
  const lookup = mockEndpoint("operation.get");
  lookup.mockReturnValue(Promise.withResolvers<Awaited<ReturnType<typeof lookup>>>().promise);
  mockWithdrawalReview();
  const execute = vi.spyOn(service, "execute").mockImplementation((prepared) =>
    Promise.resolve({
      kind: "outcome",
      status: 200,
      value: {
        representation: "HUMAN",
        operationId: prepared.input.operationId,
        outcome: { kind: "ACCEPTED", document: withdrawalReceipt(prepared.input.operationId) },
      },
    }),
  );
  mountWithdrawableCase();
  expect(await screen.findByText(filename)).toBeVisible();
  await inspectTimelineDetails(old.historical);
  const user = userEvent.setup();
  await user.click(await screen.findByRole("button", { name: /^Withdraw attachment$/u }));
  const confirm = await waitForReviewConfirm();
  expect(confirm).toBeEnabled();
  await user.click(confirm);

  const panel = within(screen.getByRole("region", { name: /^Attachments$/u }));
  expect(await panel.findByRole("alert")).toBeVisible();
  expect(execute).toHaveBeenCalledTimes(1);
  expect(list.mock.calls.length).toBeGreaterThan(1);
  expect(timeline.mock.calls.length).toBeGreaterThan(1);
  expect(panel.queryByText(filename)).not.toBeInTheDocument();
  expect(panel.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();
  expect(panel.queryByRole("button", { name: "Preview" })).not.toBeInTheDocument();
  expect(panel.queryByRole("button", { name: "Withdraw attachment" })).not.toBeInTheDocument();
  expect(screen.queryByText(old.historical)).not.toBeInTheDocument();

  await act(async () => {
    stale.resolve(oldPage);
    await stale.promise;
  });
  expect(panel.queryByText(filename)).not.toBeInTheDocument();
  expect(screen.queryByText(old.historical)).not.toBeInTheDocument();
});

const staleReceiptId = "10000000-0000-7000-8000-000000000095";
const receiptName = "private attachment receipt filename";
const staleReceipt: HumanDocument = {
  ...closeReceipt,
  subject: { kind: "RECEIPT", operationId: staleReceiptId },
  disclosure: "CURRENT",
  attachmentObservation: attachmentObservation(3),
  screen: {
    regions: [
      {
        role: "FACTS",
        content: {
          kind: "ENTRIES",
          entries: [
            {
              key: "attachmentName",
              labelId: "field.attachmentName.label",
              prominence: "PRIMARY",
              value: { kind: "NARRATIVE", value: receiptName },
            },
          ],
        },
      },
    ],
  },
};

const mockAcceptedReceipt = (document: HumanDocument, operationId: string) => ({
  kind: "outcome" as const,
  status: 200,
  value: {
    representation: "HUMAN" as const,
    operationId,
    outcome: { kind: "ACCEPTED" as const, byRequester: true, document },
  },
});

const mountCaseWithPendingRecovery = (actId: "attachment.withdraw" | "attachment.add") => {
  const { credential, sessionEpoch, ...scope } = testWorkspaceIdentity;
  const record = { ...recoveryRecord(), ...scope, subject: "ATTACHMENT" as const, act: actId };
  const pending = Promise.withResolvers<Awaited<ReturnType<typeof service.operationGet>>>();
  const lookup = vi.spyOn(service, "operationGet").mockReturnValue(pending.promise);
  const remove = vi.fn().mockResolvedValue(undefined);
  const journal = {
    list: vi
      .fn()
      .mockImplementation(() => Promise.resolve(remove.mock.calls.length === 0 ? [record] : [])),
    put: vi.fn().mockResolvedValue("inserted"),
    remove,
  };
  const caches: EndpointCache[] = [];
  render(
    <>
      <CacheDriver
        capture={(value) => {
          caches[0] = value;
        }}
      />
      <JournalContext value={journal}>
        <RecoveryProvider identity={{ ...scope, credential, sessionEpoch }}>
          <CaseDetail
            target={selected}
            reloadSignal={0}
            definition={definition.catalog}
            onBack={vi.fn()}
            onAct={vi.fn()}
          />
          <ObservedReceipt operationId={staleReceiptId} />
        </RecoveryProvider>
      </JournalContext>
    </>,
  );
  return { cache: caches[0]!, pending, lookup, record, remove };
};

const mockCaseWithPrivateAttachment = () => {
  const old = historyWithAttachment();
  const caseGet = mockEndpoint("case.get").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.get", caseWithAttachments),
  });
  const list = mockEndpoint("case.attachments");
  const oldList = {
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.attachments", admitted),
  } as const;
  list.mockResolvedValue(oldList);
  const timeline = mockEndpoint("case.timeline");
  const oldPage = {
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.timeline", old.document),
  } as const;
  timeline.mockResolvedValue(oldPage);
  const receipt = mockEndpoint("operation.get").mockResolvedValue(
    mockAcceptedReceipt(staleReceipt, staleReceiptId),
  );
  return { caseGet, list, oldList, timeline, oldPage, historical: old.historical, receipt };
};

const mockLivePreview = () => {
  const encoded = "ungWv48Bz+pBQUDeXa4iI7ADYaOWF3qctBD/YfIAFa0=";
  const hash = Uint8Array.from(atob(encoded), (byte) => byte.charCodeAt(0)).buffer;
  return mockPreview(() => Promise.resolve(hash), "blob:active-attachment").revoke;
};

test.each([
  ["attachment.withdraw", 2],
  ["attachment.add", undefined],
] as const)(
  "an erased %s receipt masks newer attachment views and rejects delayed live readbacks",
  async (actId, sequence) => {
    const { caseGet, list, oldList, timeline, oldPage, historical, receipt } =
      mockCaseWithPrivateAttachment();
    const { cache, pending, lookup, record, remove } = mountCaseWithPendingRecovery(actId);
    expect(await screen.findByText(filename)).toBeVisible();
    await waitFor(() => {
      expect(lookup).toHaveBeenCalledTimes(1);
    });
    act(() => {
      cache.confirmAttachmentChange(selected.caseId, 2);
    });
    expect(await screen.findByText(filename)).toBeVisible();
    await inspectTimelineDetails(historical);
    expect(await screen.findByText(receiptName)).toBeVisible();
    expect(cache.attachmentDisclosure(selected.caseId)).toBe(3);
    const revoke = mockLivePreview();
    await userEvent.click(screen.getByRole("button", { name: "Preview" }));
    expect(await screen.findByRole("img", { name: filename })).toBeVisible();

    caseGet.mockReturnValue(Promise.withResolvers<Awaited<ReturnType<typeof caseGet>>>().promise);
    const staleList = Promise.withResolvers<Awaited<ReturnType<typeof list>>>();
    const staleTimeline = Promise.withResolvers<Awaited<ReturnType<typeof timeline>>>();
    const staleLookup = Promise.withResolvers<Awaited<ReturnType<typeof receipt>>>();
    list.mockReturnValue(staleList.promise);
    timeline.mockReturnValue(staleTimeline.promise);
    receipt.mockReturnValue(staleLookup.promise);

    const before = cache.invalidation();
    const erased = {
      ...closeReceipt,
      disclosure: "ERASED",
      subject: { kind: "RECEIPT", operationId: record.operationId },
      links: [receiptCaseLink],
      ...(sequence === undefined ? {} : { attachmentObservation: attachmentObservation(sequence) }),
    } as const satisfies HumanDocument;
    await act(async () => {
      pending.resolve(mockAcceptedReceipt(erased, record.operationId));
      await pending.promise;
    });
    await waitFor(() => {
      expect(remove).toHaveBeenCalledWith(record);
    });
    expect(screen.queryByRole("img", { name: filename })).not.toBeInTheDocument();
    expect(revoke).toHaveBeenCalledWith("blob:active-attachment");
    expect(cache.invalidation()).toBe(before + 1);
    expect(screen.queryByText(filename)).not.toBeInTheDocument();
    expect(screen.queryByText(historical)).not.toBeInTheDocument();
    expect(screen.queryByText(receiptName)).not.toBeInTheDocument();

    await act(async () => {
      staleList.resolve(oldList);
      staleTimeline.resolve(oldPage);
      staleLookup.resolve(mockAcceptedReceipt(staleReceipt, staleReceiptId));
      await Promise.all([staleList.promise, staleTimeline.promise, staleLookup.promise]);
      cache.observeAttachmentReceipt(erased);
    });
    expect(cache.invalidation()).toBe(before + 1);
    expect(screen.queryByText(filename)).not.toBeInTheDocument();
    expect(screen.queryByText(historical)).not.toBeInTheDocument();
    expect(screen.queryByText(receiptName)).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Preview" })).not.toBeInTheDocument();
    expect(screen.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();
  },
);

const mountTimelineWorkspace = () => {
  const caches: EndpointCache[] = [];
  const view = (reloadSignal: number) => (
    <>
      <CacheDriver
        capture={(value) => {
          caches[0] = value;
        }}
      />
      <CaseDetail
        target={selected}
        reloadSignal={reloadSignal}
        definition={definition.catalog}
        onBack={vi.fn()}
        onAct={vi.fn()}
      />
      <ObservedReceipt operationId={staleReceiptId} />
    </>
  );
  const { rerender } = render(view(0));
  return {
    cache: caches[0]!,
    reload: () => {
      rerender(view(1));
    },
  };
};

const erasedHistory = () => {
  const structural = "Structural erasure event";
  const page = timelineDocument(
    [
      {
        ...openedTimelineEntry,
        highlights: [
          {
            titleId: "label.recordDetails",
            entries: [
              {
                key: "recordedChange",
                labelId: "label.recordedChange",
                prominence: "PRIMARY",
                value: { kind: "TEXT", value: structural },
              },
            ],
          },
        ],
      },
    ],
    null,
    "ERASED",
  );
  return {
    structural,
    reply: humanReadReply("case.timeline", {
      ...page,
      attachmentObservation: attachmentObservation(9),
    }),
  };
};

test("an authoritative first erased timeline page removes attachment disclosure and stays visible during readback", async () => {
  const { caseGet, list, oldList, timeline, receipt } = mockCaseWithPrivateAttachment();
  const first = Promise.withResolvers<Awaited<ReturnType<typeof timeline>>>();
  const readback = Promise.withResolvers<Awaited<ReturnType<typeof timeline>>>();
  timeline.mockReturnValueOnce(first.promise).mockReturnValue(readback.promise);
  const { cache } = mountTimelineWorkspace();
  expect(await screen.findByText(filename)).toBeVisible();
  expect(await screen.findByText(receiptName)).toBeVisible();
  const revoke = mockLivePreview();
  await userEvent.click(screen.getByRole("button", { name: "Preview" }));
  expect(await screen.findByRole("img", { name: filename })).toBeVisible();
  expect(timeline).toHaveBeenCalledTimes(1);

  caseGet.mockReturnValue(Promise.withResolvers<Awaited<ReturnType<typeof caseGet>>>().promise);
  const staleList = Promise.withResolvers<Awaited<ReturnType<typeof list>>>();
  const staleLookup = Promise.withResolvers<Awaited<ReturnType<typeof receipt>>>();
  list.mockReturnValue(staleList.promise);
  receipt.mockReturnValue(staleLookup.promise);

  const { structural, reply } = erasedHistory();
  const erased = { kind: "outcome", status: 200, value: reply } as const;
  const before = cache.invalidation();
  const caseReads = caseGet.mock.calls.length;
  await act(async () => {
    first.resolve(erased);
    await first.promise;
  });
  expect(screen.queryByText(filename)).not.toBeInTheDocument();
  expect(cache.invalidation()).toBe(before + 1);
  expect(caseGet).toHaveBeenCalledTimes(caseReads + 1);
  expect(timeline).toHaveBeenCalledTimes(2);
  await inspectTimelineDetails(structural);
  expect(screen.queryByText(receiptName)).not.toBeInTheDocument();
  expect(screen.queryByRole("img", { name: filename })).not.toBeInTheDocument();
  expect(revoke).toHaveBeenCalledWith("blob:active-attachment");

  await act(async () => {
    staleList.resolve(oldList);
    staleLookup.resolve(mockAcceptedReceipt(staleReceipt, staleReceiptId));
    readback.resolve(erased);
    await Promise.all([staleList.promise, staleLookup.promise, readback.promise]);
  });

  expect(cache.invalidation()).toBe(before + 1);
  expect(timeline).toHaveBeenCalledTimes(2);
  await inspectTimelineDetails(structural);
  expect(screen.queryByText(filename)).not.toBeInTheDocument();
  expect(screen.queryByText(receiptName)).not.toBeInTheDocument();
  expect(screen.queryByRole("button", { name: "Download" })).not.toBeInTheDocument();
});

test("an admitted erased case read masks private data before a delayed live timeline responds", async () => {
  const { caseGet, timeline, oldPage, historical } = mockCaseWithPrivateAttachment();
  const stale = Promise.withResolvers<Awaited<ReturnType<typeof timeline>>>();
  timeline.mockReturnValue(stale.promise);
  const { cache, reload } = mountTimelineWorkspace();
  expect(await screen.findByText(filename)).toBeVisible();
  const before = cache.invalidation();
  expect(timeline).toHaveBeenCalledTimes(1);

  caseGet.mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: humanReadReply("case.get", {
      ...caseWithAttachments,
      disclosure: "ERASED",
      history: "FULL",
      actions: [],
      links: [],
      screen: { regions: [] },
    }),
  });
  reload();
  await waitFor(() => {
    expect(cache.attachmentsErased(selected.caseId)).toBe(true);
  });
  expect(cache.invalidation()).toBe(before + 1);
  expect(screen.queryByText(filename)).not.toBeInTheDocument();

  await act(async () => {
    stale.resolve(oldPage);
    await stale.promise;
  });
  expect(screen.queryByText(historical)).not.toBeInTheDocument();
  expect(screen.queryByText(receiptName)).not.toBeInTheDocument();
  expect(cache.invalidation()).toBe(before + 1);
});
