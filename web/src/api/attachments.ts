import {
  attachmentTransport,
  consumerContractIdentity,
  isAttachmentTransferFailure,
  isHostFailure,
  strictByteProfile,
  type AttachmentReadRequest,
  type AttachmentTransferFailureReply,
} from "@contracts/client";
import { sameOwnerScope, type OwnerScope } from "../protocol/ownerScope";
import { localNotice, type Notice } from "../protocol/notices";
import { admitBytes } from "../protocol/schema";
import type { ApiResult } from "../protocol/types";
import { isDisclosureRefused } from "./outcomes";
import { exactResponseBytes, responseBytes } from "./responseBytes";
import { withinResponseDeadline } from "./responseDeadline";
import { sendHttp } from "./transport";

type TransferKind = "download" | "preview";
export type AttachmentBinary = {
  readonly bytes: Uint8Array<ArrayBuffer>;
  readonly mediaType: string;
};
type TransferOutcome =
  | { readonly kind: "RECEIVED"; readonly file: AttachmentBinary }
  | { readonly kind: "FAILED"; readonly reply: AttachmentTransferFailureReply };
const invalid = (status: number): ApiResult<TransferOutcome> => ({
  kind: "deliveryFailure",
  notice: { kind: "invalidHttp", status },
});
const expired = {
  kind: "deliveryFailure",
  notice: localNotice("cancelled"),
} as const;
const admittedMedia = (kind: TransferKind, mediaType: string) =>
  kind === "download"
    ? mediaType === attachmentTransport.downloadMediaType
    : mediaType === attachmentTransport.pngMediaType ||
      mediaType === attachmentTransport.jpegMediaType;
const admittedLength = (header: string | null): number | null => {
  if (header === null || !/^(0|[1-9][0-9]{0,7})$/u.test(header)) {
    return null;
  }
  const length = Number(header);
  return length <= attachmentTransport.maximumFileBytes ? length : null;
};
const admittedName = (disposition: string | null, name: string): boolean => {
  const filename =
    /^attachment; filename="[A-Za-z0-9._-]{1,120}"; filename\*=UTF-8''([A-Za-z0-9_.~%-]+)$/u.exec(
      disposition ?? "",
    );
  if (filename === null) {
    return false;
  }
  try {
    return decodeURIComponent(filename[1]!) === name;
  } catch {
    return false;
  }
};
const uncached = (headers: Headers): boolean => {
  const directives = (headers.get("cache-control") ?? "")
    .toLowerCase()
    .split(",")
    .map((value) => value.trim());
  return directives.includes("no-store") && directives.includes("no-transform");
};
const admission = (response: Response, kind: TransferKind, name: string, owner: OwnerScope) => {
  const { headers } = response;
  const mediaType = headers.get("content-type");
  const length = admittedLength(headers.get("content-length"));
  const digest = headers.get(attachmentTransport.digestHeader);
  if (
    response.status !== 200 ||
    mediaType === null ||
    !admittedMedia(kind, mediaType) ||
    length === null ||
    digest === null ||
    !/^sha-256=:[A-Za-z0-9+/]{43}=:$/u.test(digest) ||
    headers.get("ClaimCore-Installation") !== owner.installationId ||
    headers.get("content-encoding") !== "identity" ||
    !uncached(headers) ||
    (kind === "download" && !admittedName(headers.get("content-disposition"), name))
  ) {
    return null;
  }
  return { mediaType, length, digest };
};
const jsonFailure = async (
  response: Response,
  owner: OwnerScope,
  signal: AbortSignal,
): Promise<ApiResult<TransferOutcome>> => {
  const value = admitBytes(
    await responseBytes(response, strictByteProfile.maximumResponseBytes, signal),
  );
  if (
    response.status === 200 &&
    response.headers.get("ClaimCore-Installation") === owner.installationId &&
    isAttachmentTransferFailure(value)
  ) {
    return { kind: "outcome", status: response.status, value: { kind: "FAILED", reply: value } };
  }
  if (isHostFailure(value, response.status)) {
    return { kind: "hostFailure", status: response.status, failure: value.hostFailure };
  }
  return invalid(response.status);
};
const digestBytes = (bytes: Uint8Array<ArrayBuffer>) => crypto.subtle.digest("SHA-256", bytes);
const binaryResult = async (
  response: Response,
  metadata: NonNullable<ReturnType<typeof admission>>,
  current: () => boolean,
  signal: AbortSignal,
): Promise<ApiResult<TransferOutcome>> => {
  const bytes = await exactResponseBytes(response, metadata.length, signal);
  const digest = btoa(String.fromCharCode(...new Uint8Array(await digestBytes(bytes))));
  if (signal.aborted || !current()) {
    return expired;
  }
  return metadata.digest === `sha-256=:${digest}:`
    ? {
        kind: "outcome",
        status: response.status,
        value: { kind: "RECEIVED", file: { bytes, mediaType: metadata.mediaType } },
      }
    : invalid(response.status);
};
const receive = async (
  response: Response,
  kind: TransferKind,
  name: string,
  owner: OwnerScope,
  current: () => boolean,
  signal: AbortSignal,
): Promise<ApiResult<TransferOutcome>> => {
  try {
    if (response.headers.get("content-type")?.split(";", 1)[0] === "application/json") {
      const result = await jsonFailure(response, owner, signal);
      return signal.aborted || !current() ? expired : result;
    }
    const metadata = admission(response, kind, name, owner);
    if (metadata === null) {
      void response.body?.cancel().catch(() => undefined);
      return invalid(response.status);
    }
    return await binaryResult(response, metadata, current, signal);
  } catch {
    return signal.aborted || !current() ? expired : invalid(response.status);
  }
};

export const requestAttachment = (
  kind: TransferKind,
  selection: AttachmentReadRequest,
  name: string,
  credential: string,
  owner: OwnerScope,
  currentOwner: () => OwnerScope | null,
  callerSignal: AbortSignal,
): Promise<ApiResult<TransferOutcome>> =>
  withinResponseDeadline(
    async (signal) => {
      const current = () =>
        sameOwnerScope(owner, currentOwner()) && owner.serviceOrigin === window.location.origin;
      if (!current()) {
        return { ...expired, dispatched: false };
      }
      const body = JSON.stringify(selection);
      if (new TextEncoder().encode(body).length > strictByteProfile.maximumRequestBytes) {
        return { kind: "deliveryFailure", notice: localNotice("invalidInput"), dispatched: false };
      }
      try {
        const path =
          kind === "download" ? attachmentTransport.downloadPath : attachmentTransport.previewPath;
        const media =
          kind === "download"
            ? attachmentTransport.downloadMediaType
            : `${attachmentTransport.pngMediaType}, ${attachmentTransport.jpegMediaType}`;
        const response = await sendHttp(
          { path, method: "POST" },
          {
            Accept: `${media}, application/json`,
            "Content-Type": "application/json",
            "ClaimCore-Contract": consumerContractIdentity,
            "ClaimCore-Installation": owner.installationId,
            "X-ClaimCore-Antiforgery": credential,
          },
          body,
          signal,
        );
        return await receive(response, kind, name, owner, current, signal);
      } catch {
        return signal.aborted || !current()
          ? expired
          : { kind: "deliveryFailure", notice: localNotice("unreachable") };
      }
    },
    callerSignal,
    attachmentTransport.deadlineMilliseconds,
  );

export const isAttachmentDisclosureRefused = (result: ApiResult<TransferOutcome>): boolean =>
  result.kind === "outcome"
    ? result.value.kind === "FAILED" &&
      isDisclosureRefused({ ...result, value: result.value.reply })
    : isDisclosureRefused(result);

export const attachmentNotice = (result: ApiResult<TransferOutcome>): Notice | null => {
  if (result.kind === "deliveryFailure") {
    return result.notice;
  }
  if (result.kind === "hostFailure") {
    return { kind: "diagnostic", diagnostic: result.failure };
  }
  if (result.value.kind === "RECEIVED") {
    return null;
  }
  const { outcome } = result.value.reply;
  if (outcome.kind === "REJECTED") {
    return outcome.rejection.kind === "INPUT"
      ? { kind: "humanIssues", issues: outcome.rejection.issues }
      : { kind: "human", advice: outcome.rejection.advice };
  }
  return {
    kind: "human",
    advice: {
      message: { messageId: outcome.fault.messageId, arguments: [] },
      action: outcome.fault.action,
    },
  };
};

export const captureAttachment = async (file: File) => {
  if (file.size > attachmentTransport.maximumFileBytes) {
    throw new Error("File exceeds the attachment size limit.");
  }
  const bytes = new Uint8Array(await file.arrayBuffer());
  if (bytes.length !== file.size || bytes.length > attachmentTransport.maximumFileBytes) {
    throw new Error("The selected file changed.");
  }
  const hash = new Uint8Array(await digestBytes(bytes));
  const digest = Array.from(hash, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return { file: new File([bytes], file.name), digest };
};
