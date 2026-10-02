import type { InspectionControl } from "../../hooks/recoveryControl";
import { localNotice, recoveryNotice } from "../../api/notices";
import type { Notice } from "../../api/notices";
import type { Dispatch, SetStateAction } from "react";
import type {
  ApiResult,
  PreparationSummary,
  Receipt,
  RecoveryImportPreview,
  RecoveryInspection,
  RecoveryListItem,
  RecoveryPage,
  WebV3Response,
} from "../../api/v3";
import { isMutationUncertain, resultNotice, v3 } from "../../api/v3";
import type { RecoveryTarget } from "../../domain/operationState";

export type Inspection = RecoveryInspection;
export type RecoveryViewKind = RecoveryPage["view"];
export type ImportState = { file: File; preview: RecoveryImportPreview };
export type ConfirmState = { action: "RESOLVE" | "DISMISS" | "EXPORT"; item: PreparationSummary };

type Setter<T> = Dispatch<SetStateAction<T>>;
type Listing = { load: (cursor: string | null) => Promise<void> };

export type RecoveryUi = {
  onRecovery: (target: RecoveryTarget) => void;
  inspection: InspectionControl;
  setSelected: Setter<Inspection | null>;
  confirm: ConfirmState | null;
  setConfirm: Setter<ConfirmState | null>;
  importing: ImportState | null;
  setImporting: Setter<ImportState | null>;
  setMessage: Setter<Notice | null>;
  setBusy: Setter<string | null>;
};

export type RecoveryActions = {
  inspectId: (operationId: string) => void;
  inspect: (item: RecoveryListItem) => void;
  loadAttempts: (operationId: string, cursor: string) => void;
  choose: (action: ConfirmState["action"], item: PreparationSummary) => void;
  act: () => Promise<void>;
  exportItem: (item: PreparationSummary) => void;
  preview: (file: File) => Promise<void>;
  retain: () => Promise<void>;
};

export const page = (response: WebV3Response<"recovery.list">): RecoveryPage | null =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;

const inspection = (response: WebV3Response<"recovery.inspect">): Inspection | null =>
  response.outcome.tag === "SUCCEEDED" && response.outcome.data.tag === "FOUND"
    ? response.outcome.data.value
    : null;

const accepted = (response: WebV3Response<"recovery.resolve">): Receipt | null => {
  const { outcome } = response;
  if (outcome.tag === "OBSERVED_ACCEPTED") {
    return outcome.data.receipt;
  }
  if (outcome.tag === "COMPLETED" && outcome.data.execution.tag === "ACCEPTED") {
    return outcome.data.execution.receipt;
  }
  return null;
};

const operationId = (item: RecoveryListItem): string =>
  item.tag === "RETAINED" ? item.summary.operationId : item.revocation.operationId;

type InspectionUi = Pick<RecoveryUi, "inspection" | "setSelected" | "setMessage" | "setBusy">;

const setInspection = (value: Inspection, ui: InspectionUi): void => {
  ui.setSelected(value);
  ui.setMessage(null);
};

export const inspectOperation = async (
  id: string,
  attemptCursor: string | null,
  token: string,
  ui: InspectionUi,
): Promise<void> => {
  ui.inspection.cancel();
  const signal = ui.inspection.begin();
  if (attemptCursor === null) {
    ui.setSelected(null);
  }
  ui.setBusy(id);
  const result = await v3.recoveryInspect(id, attemptCursor, 50, token, signal);
  if (signal.aborted) {
    return;
  }
  ui.inspection.complete();
  const details = result.kind === "outcome" ? inspection(result.value) : null;
  ui.setBusy(null);
  if (details === null) {
    ui.setSelected(null);
    ui.setMessage(resultNotice(result));
  } else {
    setInspection(details, ui);
  }
};

const reportOperation = (
  identity: Pick<RecoveryTarget, "operationId" | "requestSha256">,
  message: Notice,
  ui: RecoveryUi,
): void => {
  ui.setMessage(message);
  ui.onRecovery({
    operationId: identity.operationId,
    requestSha256: identity.requestSha256,
    message,
  });
};

const exportFailure = (
  result: ApiResult<WebV3Response<"recovery.export">>,
  item: PreparationSummary,
  ui: RecoveryUi,
): void => {
  ui.setSelected(null);
  reportOperation(
    item,
    isMutationUncertain(result)
      ? recoveryNotice(resultNotice(result), "inspectBeforeAction")
      : resultNotice(result),
    ui,
  );
};

const download = async (item: PreparationSummary, token: string, ui: RecoveryUi): Promise<void> => {
  if (item.requestSha256 === null) {
    ui.setMessage(localNotice("digestUnavailable"));
    return;
  }
  ui.setBusy(item.operationId);
  const result = await v3.recoveryExport(item.operationId, item.requestSha256, token);
  ui.setBusy(null);
  if (result.kind !== "outcome") {
    exportFailure(result, item, ui);
    return;
  }
  const { value, status } = result;
  if (!("blob" in value)) {
    exportFailure({ kind: "outcome", value, status }, item, ui);
    return;
  }
  const url = URL.createObjectURL(value.blob);
  const link = document.createElement("a");
  link.href = url;
  link.download = value.filename;
  document.body.append(link);
  link.click();
  link.remove();
  URL.revokeObjectURL(url);
  ui.setMessage(localNotice("exportStarted"));
};

const resolutionNotice = (
  result: Awaited<ReturnType<typeof v3.recoveryResolve>>,
  receipt: Receipt | null,
): Notice => {
  if (receipt !== null) {
    const message: Notice = { kind: "accepted", operationId: receipt.operationId };
    return isMutationUncertain(result) ? recoveryNotice(message, "inspectBeforeAction") : message;
  }
  return isMutationUncertain(result)
    ? recoveryNotice(resultNotice(result), "inspectBeforeRetry")
    : resultNotice(result);
};

const performAction = async (token: string, listing: Listing, ui: RecoveryUi): Promise<void> => {
  const choice = ui.confirm;
  if (choice === null) {
    return;
  }
  if (choice.action === "EXPORT") {
    ui.setConfirm(null);
    await download(choice.item, token, ui);
    return;
  }
  const digest = choice.item.requestSha256;
  if (typeof digest !== "string") {
    return;
  }
  ui.inspection.cancel();
  ui.setSelected(null);
  ui.setBusy(choice.item.operationId);
  if (choice.action === "DISMISS") {
    const result = await v3.recoveryDismiss(choice.item.operationId, digest, token);
    ui.setBusy(null);
    ui.setConfirm(null);
    reportOperation(
      choice.item,
      isMutationUncertain(result)
        ? recoveryNotice(resultNotice(result), "inspectBeforeRetry")
        : resultNotice(result),
      ui,
    );
    void listing.load(null);
    return;
  }
  const result = await v3.recoveryResolve(choice.item.operationId, digest, token);
  ui.setBusy(null);
  ui.setConfirm(null);
  const receipt = result.kind === "outcome" ? accepted(result.value) : null;
  reportOperation(choice.item, resolutionNotice(result, receipt), ui);
  void listing.load(null);
};

const previewImport = async (file: File, token: string, ui: RecoveryUi): Promise<void> => {
  ui.inspection.cancel();
  const signal = ui.inspection.begin();
  ui.setImporting(null);
  ui.setBusy("import-ENVELOPE");
  const result = await v3.importEnvelopePreview(file, token, signal);
  if (signal.aborted) {
    return;
  }
  ui.inspection.complete();
  ui.setBusy(null);
  const data =
    result.kind === "outcome" && result.value.outcome.tag === "SUCCEEDED"
      ? result.value.outcome.data
      : null;
  if (data === null) {
    ui.setMessage(resultNotice(result));
  } else {
    ui.setImporting({ file, preview: data });
  }
};

const retainImport = async (token: string, listing: Listing, ui: RecoveryUi): Promise<void> => {
  const value = ui.importing;
  if (value === null) {
    return;
  }
  ui.setBusy("import-ENVELOPE");
  const result = await v3.importEnvelopeRetain(value.file, value.preview.sourceSha256, token);
  ui.setBusy(null);
  ui.setImporting(null);
  reportOperation(
    value.preview.decodedEffect,
    isMutationUncertain(result)
      ? recoveryNotice(resultNotice(result), "inspectBeforeRetry")
      : resultNotice(result),
    ui,
  );
  void listing.load(null);
};

export const recoveryActions = (
  token: string,
  listing: Listing,
  ui: RecoveryUi,
): RecoveryActions => ({
  inspectId: (id) => void inspectOperation(id, null, token, ui),
  inspect: (item) => void inspectOperation(operationId(item), null, token, ui),
  loadAttempts: (id, cursor) => void inspectOperation(id, cursor, token, ui),
  choose: (action, item) => {
    ui.setConfirm({ action, item });
  },
  act: () => performAction(token, listing, ui),
  exportItem: (item) => {
    ui.setConfirm({ action: "EXPORT", item });
  },
  preview: (file) => previewImport(file, token, ui),
  retain: () => retainImport(token, listing, ui),
});
