import {
  type WebV2EndpointId,
  webV2Endpoints,
  webV2HostFailureStatuses,
} from "../generated/convergence/web-v2.endpoint-catalog";
import { isHostFailure, isWebV2Response } from "../generated/convergence/web-v2.validation";

/** The generated endpoint catalogue owns route, method, media type, and byte limits. */
export type {
  AdvisoryReview,
  ApiResult,
  CaseFields,
  CaseSummary,
  CaseView,
  CommandDescriptor,
  CommandDraft,
  CommandInputDescriptor,
  CurrentCase,
  DefinitionPayload,
  EndpointOutcome,
  FieldDescriptor,
  PreparationDetails,
  PreparationSummary,
  Receipt,
  RecoveryDetails,
  RecoveryImportPreview,
  SemanticDefinition,
  SessionSnapshot,
  WebV2Response,
} from "../generated/convergence/web-v2.types";
export { isMutationUncertain, resultMessage } from "./outcomes";
import type { ApiResult, CommandDraft, WebV2Response } from "../generated/convergence/web-v2.types";

type Download = { blob: Blob; filename: string };

type JsonBody = object;
type RawBody = { bytes: Uint8Array; mediaType: string; headers?: Record<string, string> };

const isRawBody = (value: JsonBody | undefined): value is RawBody =>
  value !== undefined &&
  "bytes" in value &&
  value["bytes"] instanceof Uint8Array &&
  "mediaType" in value &&
  typeof value["mediaType"] === "string";

const endpoint = (id: WebV2EndpointId) => {
  const found = webV2Endpoints.find((candidate) => candidate.id === id);
  if (found === undefined) throw new Error(`Generated Web endpoint ${id} is unavailable.`);
  return found;
};

const jsonHeaders = (token: string | undefined): Record<string, string> => {
  const headers: Record<string, string> = { Accept: "application/json" };
  if (token !== undefined) headers["X-ClaimCore-Antiforgery"] = token;
  return headers;
};

const responseMediaType = (response: Response): string | null =>
  response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() ?? null;

const exactFilenameParameter = (name: string, value: string, expected: string): boolean => {
  if (name === "filename") return value === expected || value === `"${expected}"`;
  if (name === "filename*") return value === `UTF-8''${encodeURIComponent(expected)}`;
  return false;
};

/** ASP.NET FileResult emits both ASCII filename parameters; accept no competing suggestion. */
const exactDownloadDisposition = (header: string | null, expected: string): boolean => {
  if (header === null) return false;
  const [kind, ...parameters] = header.split(";").map((part) => part.trim());
  if (kind?.toLowerCase() !== "attachment" || parameters.length !== 2) return false;
  const seen = new Set<string>();
  for (const parameter of parameters) {
    const equals = parameter.indexOf("=");
    if (equals < 1) return false;
    const name = parameter.slice(0, equals).toLowerCase();
    const value = parameter.slice(equals + 1);
    if (seen.has(name)) return false;
    if (!exactFilenameParameter(name, value, expected)) return false;
    seen.add(name);
  }
  return seen.has("filename") && seen.has("filename*");
};

const readJson = async (response: Response): Promise<unknown> => {
  if (responseMediaType(response) !== "application/json") return null;
  try {
    return await response.json();
  } catch {
    return null;
  }
};

const hostFailureStatus = (status: number): boolean =>
  webV2HostFailureStatuses.some((candidate) => candidate === status);

const decodeJsonResponse = <K extends WebV2EndpointId>(
  id: K,
  status: number,
  value: unknown,
): ApiResult<WebV2Response<K>> => {
  if (isWebV2Response(id, value)) return { kind: "outcome", value, status };
  if (hostFailureStatus(status) && isHostFailure(value)) {
    return { kind: "hostFailure", failure: value, status };
  }
  return {
    kind: "deliveryFailure",
    message: `The local service returned an invalid HTTP ${status} response.`,
  };
};

const request = async <K extends WebV2EndpointId>(
  id: K,
  token: string | undefined,
  body: JsonBody | RawBody | undefined,
  signal?: AbortSignal,
): Promise<ApiResult<WebV2Response<K>>> => {
  const descriptor = endpoint(id);
  const headers = jsonHeaders(token);
  let requestBody: BodyInit | null = null;
  if (isRawBody(body)) {
    headers["Content-Type"] = body.mediaType;
    if (body.headers !== undefined) Object.assign(headers, body.headers);
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
    const payload = await readJson(response);
    return decodeJsonResponse(id, response.status, payload);
  } catch {
    return { kind: "deliveryFailure", message: "The local service could not be reached." };
  }
};

type RawEndpointId =
  | "recovery.importEnvelopePreview"
  | "recovery.importEnvelopeRetain"
  | "recovery.importRecordPreview"
  | "recovery.importRecordRetain";

const rawMaximum = (id: RawEndpointId) => {
  const body = endpoint(id).body;
  if (body === null || body.kind !== "RAW")
    throw new Error(`Generated endpoint ${id} has no raw body.`);
  return body;
};

const importRaw = async <K extends RawEndpointId>(
  id: K,
  source: File,
  token: string,
  sourceSha256?: string,
  signal?: AbortSignal,
): Promise<ApiResult<WebV2Response<K>>> => {
  const rule = rawMaximum(id);
  if (source.size > rule.maximumBytes)
    return {
      kind: "deliveryFailure",
      message: `The selected file exceeds ${rule.maximumBytes} bytes.`,
    };
  const bytes = new Uint8Array(await source.arrayBuffer());
  const headers =
    sourceSha256 === undefined ? undefined : { "X-ClaimCore-Source-Sha256": sourceSha256 };
  return request(id, token, { bytes, mediaType: rule.mediaType, headers }, signal);
};

const exportRecovery = async (
  operationId: string,
  requestSha256: string,
  token: string,
): Promise<ApiResult<Download | WebV2Response<"recovery.export">>> => {
  const descriptor = endpoint("recovery.export");
  try {
    const response = await fetch(descriptor.path, {
      method: descriptor.method,
      headers: { ...jsonHeaders(token), "Content-Type": "application/json" },
      body: JSON.stringify({ operationId, requestSha256 }),
      credentials: "same-origin",
    });
    const type = responseMediaType(response);
    if (type === "application/json") {
      return decodeJsonResponse("recovery.export", response.status, await readJson(response));
    }
    const expected = `claimcore-recovery-${operationId}.json`;
    const disposition = response.headers.get("content-disposition");
    if (
      response.status !== 200 ||
      descriptor.successMediaType === null ||
      type !== descriptor.successMediaType ||
      !exactDownloadDisposition(disposition, expected)
    )
      return {
        kind: "deliveryFailure",
        message: "The recovery export response failed its protocol checks.",
      };
    return {
      kind: "outcome",
      value: { blob: await response.blob(), filename: expected },
      status: response.status,
    };
  } catch {
    return { kind: "deliveryFailure", message: "The local service could not be reached." };
  }
};

export const v2 = {
  session: () => request("session", undefined, undefined),
  login: (credential: string, antiforgeryToken: string) =>
    request("session.login", antiforgeryToken, { credential, antiforgeryToken }),
  logout: (token: string) => request("session.logout", token, {}),
  definition: (signal?: AbortSignal) => request("definition", undefined, undefined, signal),
  get: (caseReference: string, token: string, signal?: AbortSignal) =>
    request("case.get", token, { caseReference }, signal),
  list: (cursor: string | null, limit: number, token: string, signal?: AbortSignal) =>
    request("case.list", token, { ...(cursor === null ? {} : { cursor }), limit }, signal),
  history: (
    caseReference: string,
    cursor: string | null,
    limit: number,
    token: string,
    signal?: AbortSignal,
  ) =>
    request(
      "case.history",
      token,
      {
        caseReference,
        ...(cursor === null ? {} : { cursor }),
        limit,
        detail: "FULL",
      },
      signal,
    ),
  observe: (operationId: string, token: string, signal?: AbortSignal) =>
    request("operation.observe", token, { operationId }, signal),
  prepare: (draft: CommandDraft, token: string) => request("command.prepare", token, draft),
  submit: (operationId: string, requestSha256: string, token: string) =>
    request("command.execute", token, { operationId, requestSha256 }),
  recoveryList: (cursor: string | null, limit: number, token: string, signal?: AbortSignal) =>
    request("recovery.list", token, { ...(cursor === null ? {} : { cursor }), limit }, signal),
  recoveryInspect: (operationId: string, token: string, signal?: AbortSignal) =>
    request("recovery.inspect", token, { operationId }, signal),
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
  importRecordPreview: (source: File, token: string, signal?: AbortSignal) =>
    importRaw("recovery.importRecordPreview", source, token, undefined, signal),
  importRecordRetain: (source: File, sourceSha256: string, token: string) =>
    importRaw("recovery.importRecordRetain", source, token, sourceSha256),
};
