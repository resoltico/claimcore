import {
  csvExportTransport,
  consumerContractIdentity,
  strictByteProfile,
  isCsvExportFailure,
  isHostFailure,
  type ExportRequest,
  type ExportFailureReply,
} from "@contracts/client";
import type { ApiResult } from "../protocol/types";
import { localNotice, type Notice } from "../protocol/notices";
import { sameOwnerScope, type OwnerScope } from "../protocol/ownerScope";
import { admitBytes } from "../protocol/schema";
import { responseBytes } from "./responseBytes";
import { withinResponseDeadline } from "./responseDeadline";
import { sendHttp } from "./transport";

export type CsvDownload = { readonly bytes: Uint8Array<ArrayBuffer>; readonly filename: string };
type CsvOutcome =
  | { readonly kind: "DOWNLOAD"; readonly document: CsvDownload }
  | { readonly kind: "FAILED"; readonly reply: ExportFailureReply };
const media = (response: Response) =>
  response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase();
const csvLength = (response: Response): number | null => {
  const value = response.headers.get("content-length");
  if (value === null || !/^(0|[1-9][0-9]*)$/u.test(value)) {
    return null;
  }
  const length = Number(value);
  return Number.isSafeInteger(length) &&
    length > 0 &&
    length <= csvExportTransport.maximumResponseBytes
    ? length
    : null;
};
const invalid = (status: number): ApiResult<CsvOutcome> => ({
  kind: "deliveryFailure",
  notice: { kind: "invalidHttp", status },
});
const jsonFailure = async (
  response: Response,
  signal: AbortSignal,
): Promise<ApiResult<CsvOutcome>> => {
  if (media(response) !== "application/json") {
    void response.body?.cancel().catch(() => undefined);
    return invalid(response.status);
  }
  const value = admitBytes(
    await responseBytes(response, strictByteProfile.maximumResponseBytes, signal),
  );
  if (response.status === 200 && isCsvExportFailure(value)) {
    return { kind: "outcome", status: response.status, value: { kind: "FAILED", reply: value } };
  }
  if (isHostFailure(value, response.status)) {
    return { kind: "hostFailure", status: response.status, failure: value.hostFailure };
  }
  return invalid(response.status);
};
const csvMetadata = (response: Response, selection: ExportRequest, owner: OwnerScope) => {
  const length = csvLength(response);
  const filename = csvExportTransport.filenames[selection.query];
  const disposition = response.headers.get("content-disposition");
  const safeDisposition =
    disposition === `attachment; filename="${filename}"` ||
    disposition === `attachment; filename=${filename}`;
  if (
    length === null ||
    response.headers.get("content-encoding")?.trim().toLowerCase() !== "identity" ||
    !safeDisposition ||
    response.headers.get("ClaimCore-Installation") !== owner.installationId
  ) {
    return null;
  }
  return { length, filename };
};
const receive = async (
  response: Response,
  selection: ExportRequest,
  owner: OwnerScope,
  currentOwner: () => OwnerScope | null,
  signal: AbortSignal,
): Promise<ApiResult<CsvOutcome>> => {
  try {
    if (response.status !== 200 || media(response) !== csvExportTransport.mediaType) {
      const result = await jsonFailure(response, signal);
      return signal.aborted || !sameOwnerScope(owner, currentOwner())
        ? { kind: "deliveryFailure", notice: localNotice("cancelled") }
        : result;
    }
    const metadata = csvMetadata(response, selection, owner);
    if (metadata === null) {
      void response.body?.cancel().catch(() => undefined);
      return invalid(response.status);
    }
    const bytes = await responseBytes(response, csvExportTransport.maximumResponseBytes, signal);
    if (
      bytes.length !== metadata.length ||
      signal.aborted ||
      !sameOwnerScope(owner, currentOwner())
    ) {
      return invalid(response.status);
    }
    new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    return {
      kind: "outcome",
      status: response.status,
      value: { kind: "DOWNLOAD", document: { bytes, filename: metadata.filename } },
    };
  } catch {
    return signal.aborted
      ? { kind: "deliveryFailure", notice: localNotice("cancelled") }
      : invalid(response.status);
  }
};
/** The complete body and current owner are admitted before a caller can construct a download. */
export const requestCsvExport = (
  selection: ExportRequest,
  credential: string,
  owner: OwnerScope,
  currentOwner: () => OwnerScope | null,
  callerSignal?: AbortSignal,
): Promise<ApiResult<CsvOutcome>> =>
  withinResponseDeadline(async (signal) => {
    if (!sameOwnerScope(owner, currentOwner()) || owner.serviceOrigin !== window.location.origin) {
      return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
    }
    const body = JSON.stringify(selection);
    if (new TextEncoder().encode(body).length > strictByteProfile.maximumRequestBytes) {
      return { kind: "deliveryFailure", notice: localNotice("invalidInput"), dispatched: false };
    }
    try {
      const response = await sendHttp(
        csvExportTransport,
        {
          Accept: "text/csv, application/json",
          "Content-Type": "application/json",
          "ClaimCore-Contract": consumerContractIdentity,
          "ClaimCore-Installation": owner.installationId,
          "X-ClaimCore-Antiforgery": credential,
        },
        body,
        signal,
      );
      return await receive(response, selection, owner, currentOwner, signal);
    } catch {
      return {
        kind: "deliveryFailure",
        notice: localNotice(signal.aborted ? "cancelled" : "unreachable"),
      };
    }
  }, callerSignal);

export const csvExportNotice = (result: ApiResult<CsvOutcome>): Notice | null => {
  if (result.kind === "deliveryFailure") {
    return result.notice;
  }
  if (result.kind === "hostFailure") {
    return result.failure.code === "CONTRACT_MISMATCH"
      ? localNotice("definitionMismatch")
      : { kind: "diagnostic", diagnostic: result.failure };
  }
  if (result.value.kind === "DOWNLOAD") {
    return null;
  }
  const { outcome } = result.value.reply;
  switch (outcome.kind) {
    case "REJECTED":
      return outcome.rejection.kind === "INPUT"
        ? { kind: "humanIssues", issues: outcome.rejection.issues }
        : { kind: "human", advice: outcome.rejection.advice };
    case "FAILED":
      return {
        kind: "human",
        advice: {
          message: { messageId: outcome.fault.messageId, arguments: [] },
          action: outcome.fault.action,
        },
      };
    case "LIMIT_EXCEEDED":
    case "PROCESSING_TIMEOUT":
    case "INVALID_CONTINUATION":
      return { kind: "human", advice: outcome.advice };
  }
};

/** Called only after complete response admission; releases its object URL on completion or owner cleanup. */
export const saveCsvDownload = (
  download: CsvDownload,
  owner: OwnerScope,
  currentOwner: () => OwnerScope | null,
): (() => void) => {
  if (!sameOwnerScope(owner, currentOwner())) {
    return () => undefined;
  }
  const url = URL.createObjectURL(new Blob([download.bytes], { type: "text/csv;charset=utf-8" }));
  let released = false;
  const release = () => {
    if (!released) {
      released = true;
      URL.revokeObjectURL(url);
    }
  };
  try {
    if (!sameOwnerScope(owner, currentOwner())) {
      release();
      return release;
    }
    const link = document.createElement("a");
    link.href = url;
    link.download = download.filename;
    link.hidden = true;
    document.body.append(link);
    try {
      link.click();
    } finally {
      link.remove();
    }
    setTimeout(release, 0);
    return release;
  } catch (error) {
    release();
    throw error;
  }
};
