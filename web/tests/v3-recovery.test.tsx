import { render, screen, waitFor } from "./presentation-test-support";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { RecoveryView } from "../src/views/RecoveryView";
import { operationId, preparation, response } from "./v3-ui.fixtures";
import {
  accepted,
  importFile,
  inspection,
  list,
  preview,
  receipt,
  rejected,
  selectFile,
} from "./v3-recovery.fixtures";

const inspectsAndDismisses = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(inspection());
  fetch.mockResolvedValueOnce(response("recovery.dismiss", "DISMISSED", preparation));
  fetch.mockResolvedValueOnce(list([]));
  render(<RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />);
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  expect(await screen.findByRole("dialog", { name: "Recovery details" })).toHaveTextContent(
    preparation.summary.requestSha256!,
  );
  expect(screen.getByRole("dialog", { name: "Recovery details" })).toHaveTextContent(operationId);
  await user.click(screen.getByRole("button", { name: "Dismiss preparation" }));
  await user.click(await screen.findByRole("button", { name: "Confirm dismiss" }));
  expect(await screen.findByText("Preparation dismissed.")).toBeVisible();
};

describe("v3 Recovery view", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it(
    "inspects and dismisses only a server-advertised unsubmitted preparation",
    inspectsAndDismisses,
  );

  it("renders a definite resolve receipt instead of treating it as transport uncertainty", async () => {
    const user = userEvent.setup();
    const fetch = vi.mocked(globalThis.fetch);
    fetch.mockResolvedValueOnce(list());
    fetch.mockResolvedValueOnce(inspection());
    fetch.mockResolvedValueOnce(accepted());
    fetch.mockResolvedValueOnce(list([]));
    render(<RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />);
    await user.click(await screen.findByRole("button", { name: "Inspect" }));
    await user.click(screen.getByRole("button", { name: "Resolve exact preparation" }));
    await screen.findByRole("button", { name: "Confirm resolve" });
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
    const language = screen.getByRole("combobox", { name: "Interface language" });
    await user.selectOptions(language, "ar");
    expect(screen.getAllByRole("dialog")).toHaveLength(1);
    await user.selectOptions(language, "en");
    expect(fetch).toHaveBeenCalledTimes(2);
    await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
    expect(await screen.findByText(`Accepted exact operation ${operationId}.`)).toBeVisible();
  });

  it("keeps a completed non-acceptance out of the accepted receipt path", async () => {
    const user = userEvent.setup();
    const fetch = vi.mocked(globalThis.fetch);
    fetch.mockResolvedValueOnce(list());
    fetch.mockResolvedValueOnce(inspection());
    fetch.mockResolvedValueOnce(rejected());
    fetch.mockResolvedValueOnce(list([]));
    render(<RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />);
    await user.click(await screen.findByRole("button", { name: "Inspect" }));
    await user.click(screen.getByRole("button", { name: "Resolve exact preparation" }));
    await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
    expect(await screen.findByRole("status", { hidden: true })).toHaveTextContent(
      "Read the current case",
    );
    expect(screen.queryByText(/Accepted exact operation/u)).toBeNull();
  });
});

const exportsAndRetainsEnvelope = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  const createObjectURL = vi.fn(() => "blob:synthetic");
  const revokeObjectURL = vi.fn();
  Object.assign(URL, { createObjectURL, revokeObjectURL });
  vi.spyOn(HTMLAnchorElement.prototype, "click").mockImplementation(() => undefined);
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(inspection("FOUND"));
  fetch.mockResolvedValueOnce(
    new Response("{}", {
      headers: {
        "content-type": "application/vnd.claimcore.recovery+json",
        "content-disposition": `attachment; filename=claimcore-recovery-${operationId}.json; filename*=UTF-8''claimcore-recovery-${operationId}.json`,
      },
    }),
  );
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopePreview", "SUCCEEDED", preview));
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopeRetain", "RETAINED", preparation));
  fetch.mockResolvedValueOnce(list([]));
  const view = render(
    <RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />,
  );
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Export recovery envelope" }));
  expect(
    await screen.findByRole("dialog", { name: "Export recovery envelope?" }),
  ).toHaveTextContent("claimant data and recovery bytes");
  await user.click(screen.getByRole("button", { name: "Confirm export" }));
  await waitFor(() => {
    expect(createObjectURL).toHaveBeenCalledOnce();
  });
  expect(fetch.mock.calls[2]?.[1]?.body).toBe(
    JSON.stringify({ operationId, requestSha256: preparation.summary.requestSha256 }),
  );
  selectFile(
    view.container.querySelectorAll('input[type="file"]')[0]!,
    importFile(view.container, 0, "application/vnd.claimcore.recovery+json"),
  );
  await user.click(await screen.findByRole("button", { name: "Retain for Recovery" }));
  expect(await screen.findByText("Recovery material retained.")).toBeVisible();
  await waitFor(() => {
    expect(revokeObjectURL).toHaveBeenCalledWith("blob:synthetic");
  });
};

const retainsEnvelopeAfterListFailure = async (): Promise<void> => {
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("recovery.list", "REJECTED", {}));
  const view = render(
    <RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />,
  );
  expect(await screen.findByRole("alert")).toBeVisible();
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopePreview", "SUCCEEDED", preview));
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopeRetain", "RETAINED", preparation));
  fetch.mockResolvedValueOnce(list([]));
  selectFile(
    view.container.querySelectorAll('input[type="file"]')[0]!,
    importFile(view.container, 0, "application/vnd.claimcore.recovery+json"),
  );
  await user.click(await screen.findByRole("button", { name: "Retain for Recovery" }));
  await waitFor(() => {
    expect(document.querySelector('[role="status"]')).not.toBeNull();
  });
};

describe("v3 Recovery import and export", () => {
  beforeEach(() => vi.stubGlobal("fetch", vi.fn()));
  it(
    "exports the validated filename then retains previewed envelope material without submission",
    exportsAndRetainsEnvelope,
  );
  it(
    "retains encrypted-envelope previews and reports rejected recovery pages",
    retainsEnvelopeAfterListFailure,
  );
});

it("closes recovery detail, confirm, and import dialogs without dispatching mutations", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(inspection());
  fetch.mockResolvedValueOnce(inspection());
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopePreview", "SUCCEEDED", preview));
  const view = render(
    <RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />,
  );
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  await user.click(screen.getByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Dismiss preparation" }));
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  await user.click(screen.getByRole("button", { name: "Export recovery envelope" }));
  expect(await screen.findByRole("dialog", { name: "Export recovery envelope?" })).toBeVisible();
  expect(fetch).toHaveBeenCalledTimes(3);
  await user.click(screen.getByRole("button", { name: "Cancel" }));
  expect(fetch).toHaveBeenCalledTimes(3);
  selectFile(
    view.container.querySelectorAll('input[type="file"]')[0]!,
    importFile(view.container, 0, "application/vnd.claimcore.recovery+json"),
  );
  await user.click(await screen.findByRole("button", { name: "Cancel" }));
  expect(fetch).toHaveBeenCalledTimes(4);
});

it("reports malformed pages, malformed inspections, rejected imports, and invalid exports", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(response("recovery.list", "SUCCEEDED", { items: "invalid" }));
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(response("recovery.inspect", "SUCCEEDED", { tag: "NOT_FOUND" }));
  fetch.mockResolvedValueOnce(inspection());
  fetch.mockResolvedValueOnce(
    new Response("{}", { headers: { "content-type": "application/json" } }),
  );
  fetch.mockResolvedValueOnce(response("recovery.importEnvelopePreview", "REJECTED", {}));
  const view = render(
    <RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />,
  );
  expect(await screen.findByRole("alert")).toBeVisible();
  await user.click(screen.getByRole("button", { name: "Reload" }));
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await waitFor(() => {
    expect(document.querySelector('[role="status"]')).not.toBeNull();
  });
  await user.click(screen.getByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Export recovery envelope" }));
  await user.click(await screen.findByRole("button", { name: "Confirm export" }));
  selectFile(
    view.container.querySelectorAll('input[type="file"]')[0]!,
    importFile(view.container, 0, "application/vnd.claimcore.recovery+json"),
  );
  expect(await screen.findByRole("status", { hidden: true })).toBeVisible();
});

it("renders direct observed-accepted recovery receipts", async () => {
  vi.stubGlobal("fetch", vi.fn());
  const user = userEvent.setup();
  const fetch = vi.mocked(globalThis.fetch);
  fetch.mockResolvedValueOnce(list());
  fetch.mockResolvedValueOnce(inspection());
  fetch.mockResolvedValueOnce(
    response("recovery.resolve", "OBSERVED_ACCEPTED", {
      receipt: { ...receipt, replayed: true },
    }),
  );
  fetch.mockResolvedValueOnce(list([]));
  render(<RecoveryView onRecovery={vi.fn()} onMutationLockChange={vi.fn()} token="token" />);
  await user.click(await screen.findByRole("button", { name: "Inspect" }));
  await user.click(screen.getByRole("button", { name: "Resolve exact preparation" }));
  await user.click(await screen.findByRole("button", { name: "Confirm resolve" }));
  expect(await screen.findByText(`Accepted exact operation ${operationId}.`)).toBeVisible();
});
