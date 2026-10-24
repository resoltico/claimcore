import type { PreparedRequest } from "../protocol/preparedRequest";
import { sendHttp } from "./transport";
import { responseBytes } from "./responseBytes";
import type { ApiResult, ServiceResponse } from "./types";
import { withinResponseDeadline } from "./responseDeadline";
import { localNotice } from "./notices";
import {
  type EndpointId,
  endpoints,
  strictByteProfile,
  consumerContractIdentity,
} from "@contracts/client";
import { admitBytes } from "../protocol/schema";
import { correlateReply, isHostFailure, isResponse } from "../protocol/client";
export type * from "./types";
export { isCurrentCase } from "./types";
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
  requestInput: unknown,
): Promise<ApiResult<ServiceResponse<K>>> => {
  const value = await readJson(response, signal);
  if (response.status === 200 && isResponse(id, value) && correlateReply(id, requestInput, value)) {
    return { kind: "outcome", value, status: response.status };
  }
  if (isHostFailure(value, response.status)) {
    return { kind: "hostFailure", failure: value.hostFailure, status: response.status };
  }
  return { kind: "deliveryFailure", notice: { kind: "invalidHttp", status: response.status } };
};
const send = async <K extends EndpointId>(
  id: K,
  token: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
): Promise<ApiResult<ServiceResponse<K>>> => {
  if (signal.aborted) {
    return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
  }
  let requestInput: unknown;
  try {
    requestInput = body === undefined ? {} : admitBytes(new TextEncoder().encode(body));
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("incomplete"), dispatched: false };
  }
  const endpoint = endpoints.find((candidate) => candidate.id === id)!;
  const headers: Record<string, string> = {
    Accept: "application/json",
    "ClaimCore-Contract": consumerContractIdentity,
  };
  if (token !== undefined) {
    headers["X-ClaimCore-Antiforgery"] = token;
  }
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  try {
    const response = await sendHttp(endpoint, headers, body, signal);
    return await result(id, response, signal, requestInput);
  } catch {
    return { kind: "deliveryFailure", notice: localNotice("unreachable") };
  }
};
const request = <K extends EndpointId>(
  id: K,
  token: string | undefined,
  body: object | undefined,
  signal?: AbortSignal,
) =>
  withinResponseDeadline(
    (owned) => send(id, token, body === undefined ? undefined : JSON.stringify(body), owned),
    signal,
  );
export const service = {
  session: () => request("session", undefined, undefined),
  logout: (token: string) => request("session.logout", token, {}),
  context: (token: string, signal?: AbortSignal) => request("context", token, {}, signal),
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
  authorityList: (cursor: string | null, token: string, signal?: AbortSignal) =>
    request("authority.list", token, { ...(cursor === null ? {} : { cursor }), limit: 50 }, signal),
  authorityGrants: (actorId: string, cursor: string | null, token: string, signal?: AbortSignal) =>
    request(
      "authority.grants",
      token,
      { actorId, ...(cursor === null ? {} : { cursor }), limit: 50 },
      signal,
    ),
  preview: (prepared: PreparedRequest<"operation.execute">, token: string) =>
    withinResponseDeadline((owned) => send("operation.preview", token, prepared.bytes, owned)),
  execute: (prepared: PreparedRequest<"operation.execute">, token: string) =>
    withinResponseDeadline((owned) => send(prepared.endpoint, token, prepared.bytes, owned)),
};
