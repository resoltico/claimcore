import type { PreparedRequest } from "../protocol/preparedRequest";
import { sendHttp } from "./transport";
import { responseBytes } from "./responseBytes";
import type { ApiResult, ServiceResponse } from "../protocol/types";
import { withinResponseDeadline } from "./responseDeadline";
import { localNotice } from "../protocol/notices";
import {
  type EndpointId,
  type RequestByEndpoint,
  endpoints,
  strictByteProfile,
  consumerContractIdentity,
} from "@contracts/client";
import { admitBytes } from "../protocol/schema";
import { isHostFailure, isResponse } from "../protocol/client";
export type * from "../protocol/types";
export { isCurrentCase } from "../protocol/types";
export { isMutationUncertain, resultNotice } from "./outcomes";
const responseLimit = strictByteProfile.maximumResponseBytes;
const readJson = async (response: Response, signal: AbortSignal): Promise<unknown> => {
  if (
    response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() !==
    "application/json"
  ) {
    void response.body?.cancel().catch(() => undefined);
    return null;
  }
  try {
    return admitBytes(await responseBytes(response, responseLimit, signal));
  } catch {
    return null;
  }
};
const result = async <K extends EndpointId>(
  id: K,
  response: Response,
  signal: AbortSignal,
): Promise<ApiResult<ServiceResponse<K>>> => {
  const value = await readJson(response, signal);
  if (response.status === 200 && isResponse(id, value)) {
    return { kind: "outcome", value, status: response.status };
  }
  if (isHostFailure(value, response.status)) {
    return { kind: "hostFailure", failure: value.hostFailure, status: response.status };
  }
  return { kind: "deliveryFailure", notice: { kind: "invalidHttp", status: response.status } };
};
const send = async <K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
): Promise<ApiResult<ServiceResponse<K>>> => {
  if (signal.aborted) {
    return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
  }
  try {
    if (body !== undefined) {
      admitBytes(new TextEncoder().encode(body));
    }
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("incomplete"), dispatched: false };
  }
  const endpoint = endpoints.find((candidate) => candidate.id === id)!;
  const headers: Record<string, string> = {
    Accept: "application/json",
    "ClaimCore-Contract": consumerContractIdentity,
  };
  if (credential !== undefined) {
    headers["X-ClaimCore-Antiforgery"] = credential;
  }
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  try {
    const response = await sendHttp(endpoint, headers, body, signal);
    return await result(id, response, signal);
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("unreachable") };
  }
};
const request = <K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: object | undefined,
  signal?: AbortSignal,
) =>
  withinResponseDeadline(
    (owned) => send(id, credential, body === undefined ? undefined : JSON.stringify(body), owned),
    signal,
  );
export const requestEndpoint = <K extends EndpointId>(
  id: K,
  credential: string,
  input: RequestByEndpoint[K],
  signal?: AbortSignal,
) => request(id, credential, input, signal);
export const service = {
  session: () => request("session", undefined, undefined),
  logout: (credential: string) => request("session.logout", credential, {}),
  context: (credential: string, signal?: AbortSignal) => request("context", credential, {}, signal),
  get: (caseReference: string, credential: string, signal?: AbortSignal) =>
    request("case.get", credential, { caseReference }, signal),
  list: (cursor: string | null, limit: number, credential: string, signal?: AbortSignal) =>
    request("case.list", credential, { ...(cursor === null ? {} : { cursor }), limit }, signal),
  history: (
    input: {
      caseReference: string;
      cursor: string | null;
      limit: number;
      detail?: "SUMMARY" | "FULL";
    },
    credential: string,
    signal?: AbortSignal,
  ) =>
    request(
      "case.history",
      credential,
      {
        caseReference: input.caseReference,
        ...(input.cursor === null ? {} : { cursor: input.cursor }),
        limit: input.limit,
        detail: input.detail ?? "SUMMARY",
      },
      signal,
    ),
  observe: (operationId: string, credential: string, signal?: AbortSignal) =>
    request("operation.observe", credential, { operationId }, signal),
  authorityList: (cursor: string | null, credential: string, signal?: AbortSignal) =>
    request(
      "authority.list",
      credential,
      { ...(cursor === null ? {} : { cursor }), limit: 50 },
      signal,
    ),
  authorityGrants: (
    actorId: string,
    cursor: string | null,
    credential: string,
    signal?: AbortSignal,
  ) =>
    request(
      "authority.grants",
      credential,
      { actorId, ...(cursor === null ? {} : { cursor }), limit: 50 },
      signal,
    ),
  resolve: (operationId: string, credential: string) =>
    request("operation.resolve", credential, { operationId }),
  preview: (prepared: PreparedRequest<"operation.execute">, credential: string) =>
    withinResponseDeadline((owned) => send("operation.preview", credential, prepared.bytes, owned)),
  execute: (prepared: PreparedRequest<"operation.execute">, credential: string) =>
    withinResponseDeadline((owned) => send(prepared.endpoint, credential, prepared.bytes, owned)),
};
