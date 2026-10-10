import { responseBytes, strictJsonBytes } from "./responseBytes";
import type { ApiResult } from "./types";
import { withinResponseDeadline } from "./responseDeadline";
import { exactDownloadDisposition } from "./downloadDisposition";
import { localNotice } from "./notices";
import {
  type WebV3EndpointId,
  webV3Endpoints,
  webV3HostFailureStatuses,
  webV3TransportLimits,
} from "../generated/contracts/web-v3.endpoint-catalog";
import {
  isHostFailure,
  isRecoveryArtifact,
  isWebV3Response,
} from "../generated/contracts/web-v3.validation";
import type { CommandDraft, HostFailure, WebV3Response } from "../generated/contracts/web-v3.types";

/** The generated endpoint catalogue owns route, method, media type, and byte limits. */
export type * from "./types";
export { isMutationUncertain, resultNotice } from "./outcomes";

type Download = { blob: Blob; filename: string };

type JsonBody = object;
type RawBody = { bytes: Uint8Array; mediaType: string; headers?: Record<string, string> };

const isRawBody = (value: JsonBody | undefined): value is RawBody =>
  value !== undefined &&
  "bytes" in value &&
  value["bytes"] instanceof Uint8Array &&
  "mediaType" in value &&
  typeof value["mediaType"] === "string";

const endpoint = (id: WebV3EndpointId) => {
  const found = webV3Endpoints.find((candidate) => candidate.id === id);
  if (found === undefined) {
    throw new Error(`Generated Web endpoint ${id} is unavailable.`);
  }
  return found;
};

const jsonHeaders = (token: string | undefined): Record<string, string> => {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (token !== undefined) {
    headers["X-ClaimCore-Antiforgery"] = token;
  }
  return headers;
};

const responseMediaType = (response: Response): string | null =>
  response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() ?? null;

const cancelUnreadBody = (response: Response) => {
  void response.body?.cancel().catch(() => {
    // Cancellation refusal cannot replace the original protocol refusal.
  });
};

const readJson = async (response: Response, signal: AbortSignal): Promise<unknown> => {
  if (responseMediaType(response) !== "application/json") {
    cancelUnreadBody(response);
    return null;
  }
  try {
    return strictJsonBytes(
      await responseBytes(response, webV3TransportLimits.jsonResponseBytes, signal),
    );
  } catch {
    return null;
  }
};

const decodeJsonResponse = async <K extends WebV3EndpointId>(
  id: K,
  status: number,
  value: unknown,
): Promise<ApiResult<WebV3Response<K>>> => {
  if (status === 200 && (await isWebV3Response(id, value))) {
    return { kind: "outcome", value: value as WebV3Response<K>, status };
  }
  if (
    webV3HostFailureStatuses.some((candidate) => candidate === status) &&
    (await isHostFailure(value, status))
  ) {
    return { kind: "hostFailure", failure: value as HostFailure, status };
  }
  return {
    kind: "deliveryFailure",
    notice: { kind: "invalidHttp", status },
  };
};

const requestResult = async <K extends WebV3EndpointId>(
  id: K,
  token: string | undefined,
  body: JsonBody | RawBody | undefined,
  signal: AbortSignal,
): Promise<ApiResult<WebV3Response<K>>> => {
  const descriptor = endpoint(id);
  const headers = jsonHeaders(token);
  let requestBody: BodyInit | null = null;
  if (isRawBody(body)) {
    headers["Content-Type"] = body.mediaType;
    if (body.headers !== undefined) {
      Object.assign(headers, body.headers);
    }
    requestBody = new Uint8Array(body.bytes).buffer;
  } else if (body !== undefined) {
    headers["Content-Type"] = "application/json";
    requestBody = JSON.stringify(body);
  }
  try {
    const response = await fetch(descriptor.path, {
      method: descriptor.method,
      headers,
      body: requestBody,
      credentials: "same-origin",
      ...(signal === undefined ? {} : { signal }),
    });
    const payload = await readJson(response, signal);
    return await decodeJsonResponse(id, response.status, payload);
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("unreachable") };
  }
};

const request = <K extends WebV3EndpointId>(
  id: K,
  token: string | undefined,
  body: JsonBody | RawBody | undefined,
  signal?: AbortSignal,
) => withinResponseDeadline((ownedSignal) => requestResult(id, token, body, ownedSignal), signal);

type RawEndpointId = "recovery.importEnvelopePreview" | "recovery.importEnvelopeRetain";

const rawMaximum = (id: RawEndpointId) => {
  const { body } = endpoint(id);
  if (body === null || body.kind !== "RAW") {
    throw new Error(`Generated endpoint ${id} has no raw body.`);
  }
  return body;
};

const importRaw = async <K extends RawEndpointId>(
  id: K,
  source: File,
  token: string,
  sourceSha256?: string,
  signal?: AbortSignal,
): Promise<ApiResult<WebV3Response<K>>> => {
  const rule = rawMaximum(id);
  if (source.size > rule.maximumBytes) {
    return {
      kind: "deliveryFailure",
      notice: { kind: "fileTooLarge", maximumBytes: rule.maximumBytes },
    };
  }
  try {
    const bytes = new Uint8Array(await source.arrayBuffer());
    const headers =
      sourceSha256 === undefined ? undefined : { "X-ClaimCore-Source-Sha256": sourceSha256 };
    return await request(id, token, { bytes, mediaType: rule.mediaType, headers }, signal);
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("fileUnreadable") };
  }
};

const exportRecoveryResult = async (
  operationId: string,
  requestSha256: string,
  token: string,
  signal: AbortSignal,
): Promise<ApiResult<Download | WebV3Response<"recovery.export">>> => {
  const descriptor = endpoint("recovery.export");
  try {
    const response = await fetch(descriptor.path, {
      method: descriptor.method,
      headers: { ...jsonHeaders(token), "Content-Type": "application/json" },
      body: JSON.stringify({ operationId, requestSha256 }),
      credentials: "same-origin",
      signal,
    });
    const type = responseMediaType(response);
    if (type === "application/json") {
      return await decodeJsonResponse(
        "recovery.export",
        response.status,
        await readJson(response, signal),
      );
    }
    const expected = `claimcore-recovery-${operationId}.json`;
    const disposition = response.headers.get("content-disposition");
    if (
      response.status !== 200 ||
      descriptor.successMediaType === null ||
      type !== descriptor.successMediaType ||
      !exactDownloadDisposition(disposition, expected)
    ) {
      cancelUnreadBody(response);
      return {
        kind: "deliveryFailure",
        notice: localNotice("exportInvalid"),
      };
    }
    const bytes = await responseBytes(response, webV3TransportLimits.recoveryArtifactBytes, signal);
    if (!(await isRecoveryArtifact(strictJsonBytes(bytes), operationId))) {
      return { kind: "deliveryFailure", notice: localNotice("exportInvalid") };
    }
    return {
      kind: "outcome",
      value: { blob: new Blob([bytes], { type: descriptor.successMediaType }), filename: expected },
      status: response.status,
    };
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("unreachable") };
  }
};

const exportRecovery = (operationId: string, requestSha256: string, token: string) =>
  withinResponseDeadline((signal) =>
    exportRecoveryResult(operationId, requestSha256, token, signal),
  );

export const v3 = {
  session: () => request("session", undefined, undefined),
  logout: (token: string) => request("session.logout", token, {}),
  definition: (signal?: AbortSignal) => request("definition", undefined, undefined, signal),
  get: (caseReference: string, token: string, signal?: AbortSignal) =>
    request("case.get", token, { caseReference }, signal),
  list: (cursor: string | null, limit: number, token: string, signal?: AbortSignal) =>
    request("case.list", token, { ...(cursor === null ? {} : { cursor }), limit }, signal),
  history: (
    input: {
      caseReference: string;
      cursor: string | null;
      limit: number;
      detail?: "SUMMARY" | "FULL";
    },
    token: string,
    signal?: AbortSignal,
  ) =>
    request(
      "case.history",
      token,
      {
        caseReference: input.caseReference,
        ...(input.cursor === null ? {} : { cursor: input.cursor }),
        limit: input.limit,
        detail: input.detail ?? "SUMMARY",
      },
      signal,
    ),
  observe: (operationId: string, token: string, signal?: AbortSignal) =>
    request("operation.observe", token, { operationId }, signal),
  lifecycleReview: (caseReference: string, token: string, signal?: AbortSignal) =>
    request("lifecycle.review", token, { caseReference }, signal),
  prepare: (draft: CommandDraft, token: string) => request("command.prepare", token, draft),
  submit: (draft: CommandDraft, token: string) => request("command.execute", token, draft),
  recoveryList: (
    view: "PENDING" | "TERMINAL",
    cursor: string | null,
    limit: number,
    token: string,
    signal?: AbortSignal,
  ) =>
    request(
      "recovery.list",
      token,
      { view, ...(cursor === null ? {} : { cursor }), limit },
      signal,
    ),
  recoveryInspect: (
    operationId: string,
    attemptCursor: string | null,
    attemptLimit: number,
    token: string,
    signal?: AbortSignal,
  ) =>
    request(
      "recovery.inspect",
      token,
      { operationId, ...(attemptCursor === null ? {} : { attemptCursor }), attemptLimit },
      signal,
    ),
  recoveryResolve: (operationId: string, requestSha256: string, token: string) =>
    request("recovery.resolve", token, { operationId, requestSha256 }),
  recoveryDismiss: (operationId: string, requestSha256: string, token: string) =>
    request("recovery.dismiss", token, {
      operationId,
      requestSha256,
      confirmed: true,
    }),
  recoveryExport: exportRecovery,
  importEnvelopePreview: (source: File, token: string, signal?: AbortSignal) =>
    importRaw("recovery.importEnvelopePreview", source, token, undefined, signal),
  importEnvelopeRetain: (source: File, sourceSha256: string, token: string) =>
    importRaw("recovery.importEnvelopeRetain", source, token, sourceSha256),
};
