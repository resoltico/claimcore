import type { Dispatch, SetStateAction } from "react";
import type {
  PreparationSummary,
  Receipt,
  RecoveryDetails,
  RecoveryImportPreview,
  RecoveryInspection,
  RecoveryListItem,
  RecoveryPage,
  WebV2Response,
} from "../../api/v2";
import { isMutationUncertain, resultMessage, v2 } from "../../api/v2";

export type Inspection = RecoveryInspection;
export type RecoveryViewKind = RecoveryPage["view"];
export type ImportKind = "ENVELOPE" | "RECORD";
export type ImportState = { file: File; kind: ImportKind; preview: RecoveryImportPreview };
export type ConfirmState = { action: "RESOLVE" | "DISMISS" | "EXPORT"; item: PreparationSummary };

type Setter<T> = Dispatch<SetStateAction<T>>;
type Listing = { load: (cursor: string | null) => Promise<void> };

export type RecoveryUi = {
  selected: Inspection | null;
  setSelected: Setter<Inspection | null>;
  selectedSummary: PreparationSummary | null;
  setSelectedSummary: Setter<PreparationSummary | null>;
  confirm: ConfirmState | null;
  setConfirm: Setter<ConfirmState | null>;
  importing: ImportState | null;
  setImporting: Setter<ImportState | null>;
  message: string | null;
  setMessage: Setter<string | null>;
  busy: string | null;
  setBusy: Setter<string | null>;
};

export type RecoveryActions = {
  inspect: (item: RecoveryListItem) => void;
  loadAttempts: (operationId: string, cursor: string) => void;
  choose: (action: ConfirmState["action"], item: PreparationSummary) => void;
  act: () => Promise<void>;
  exportItem: (item: PreparationSummary) => void;
  preview: (kind: ImportKind, file: File) => void;
  retain: () => Promise<void>;
};

export const page = (response: WebV2Response<"recovery.list">): RecoveryPage | null =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

const inspection = (response: WebV2Response<"recovery.inspect">): Inspection | null =>
  response.outcome.tag === "SUCCEEDED" && response.outcome.data.tag === "FOUND"
    ? response.outcome.data.value
    : null;

const accepted = (response: WebV2Response<"recovery.resolve">): Receipt | null => {
  const outcome = response.outcome;
  if (outcome.tag === "OBSERVED_ACCEPTED") return outcome.data.receipt;
  if (outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED") {
    return outcome.data.execution.receipt;
  }
  return null;
};

const operationId = (item: RecoveryListItem): string =>
  item.tag === "RETAINED" ? item.summary.operationId : item.revocation.operationId;

const setInspection = (value: Inspection, ui: RecoveryUi): void => {
  const retained: RecoveryDetails | null = value.tag === "RETAINED" ? value.value : null;
  ui.setSelected(value);
  ui.setSelectedSummary(retained?.preparation.summary ?? null);
  ui.setMessage(null);
};

const inspectOperation = async (
  id: string,
  attemptCursor: string | null,
  token: string,
  ui: RecoveryUi,
): Promise<void> => {
  ui.setBusy(id);
  const result = await v2.recoveryInspect(id, attemptCursor, 50, token);
  const details = result.kind === "outcome" ? inspection(result.value) : null;
  ui.setBusy(null);
  if (details === null) ui.setMessage(resultMessage(result));
  else setInspection(details, ui);
};

const performAction = async (token: string, listing: Listing, ui: RecoveryUi): Promise<void> => {
  const choice = ui.confirm;
  if (choice === null) return;
  if (choice.action === "EXPORT") {
    ui.setConfirm(null);
    await download(choice.item, token, ui);
    return;
  }
  const digest = choice.item.requestSha256;
  if (typeof digest !== "string") return;
  ui.setBusy(choice.item.operationId);
  if (choice.action === "DISMISS") {
    const result = await v2.recoveryDismiss(choice.item.operationId, digest, token);
    ui.setBusy(null);
    ui.setConfirm(null);
    ui.setMessage(resultMessage(result));
    void listing.load(null);
    return;
  }
  const result = await v2.recoveryResolve(choice.item.operationId, digest, token);
  ui.setBusy(null);
  ui.setConfirm(null);
  const receipt = result.kind === "outcome" ? accepted(result.value) : null;
  ui.setMessage(
    receipt !== null
      ? `Accepted exact operation ${receipt.operationId}.`
      : isMutationUncertain(result)
        ? `${resultMessage(result)} Inspect Recovery before retrying this exact preparation.`
        : resultMessage(result),
  );
  void listing.load(null);
};

export const onceWhilePending = (
  pending: { current: boolean },
  action: () => Promise<void>,
): Promise<void> => {
  if (pending.current) return Promise.resolve();
  pending.current = true;
  return action().finally(() => {
    pending.current = false;
  });
};

const download = async (item: PreparationSummary, token: string, ui: RecoveryUi): Promise<void> => {
  if (item.requestSha256 === null) {
    ui.setMessage("The exact recovery digest is unavailable; inspect the preparation.");
    return;
  }
  ui.setBusy(item.operationId);
  const result = await v2.recoveryExport(item.operationId, item.requestSha256, token);
  ui.setBusy(null);
  if (result.kind !== "outcome" || !("blob" in result.value)) {
    ui.setMessage(resultMessage(result));
    return;
  }
  const url = URL.createObjectURL(result.value.blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = result.value.filename;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
  ui.setMessage("Recovery export started. The file contains claimant data; keep it private.");
};

const previewImport = async (
  kind: ImportKind,
  file: File,
  token: string,
  ui: RecoveryUi,
): Promise<void> => {
  ui.setBusy(`import-${kind}`);
  const result =
    kind === "ENVELOPE"
      ? await v2.importEnvelopePreview(file, token)
      : await v2.importRecordPreview(file, token);
  ui.setBusy(null);
  const data =
    result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED"
      ? result.value.outcome.data
      : null;
  if (data === null) ui.setMessage(resultMessage(result));
  else ui.setImporting({ file, kind, preview: data });
};

const retainImport = async (token: string, listing: Listing, ui: RecoveryUi): Promise<void> => {
  const value = ui.importing;
  if (value === null) return;
  ui.setBusy(`import-${value.kind}`);
  const result =
    value.kind === "ENVELOPE"
      ? await v2.importEnvelopeRetain(value.file, value.preview.sourceSha256, token)
      : await v2.importRecordRetain(value.file, value.preview.sourceSha256, token);
  ui.setBusy(null);
  ui.setImporting(null);
  ui.setMessage(resultMessage(result));
  void listing.load(null);
};

export const recoveryActions = (
  token: string,
  listing: Listing,
  ui: RecoveryUi,
): RecoveryActions => ({
  inspect: (item) => void inspectOperation(operationId(item), null, token, ui),
  loadAttempts: (id, cursor) => void inspectOperation(id, cursor, token, ui),
  choose: (action, item) => ui.setConfirm({ action, item }),
  act: () => performAction(token, listing, ui),
  exportItem: (item) => ui.setConfirm({ action: "EXPORT", item }),
  preview: (kind, file) => void previewImport(kind, file, token, ui),
  retain: () => retainImport(token, listing, ui),
});
