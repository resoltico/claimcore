import { deliveryOptions } from "./deliveryOptions";
import { responseBytes, strictJsonBytes } from "./responseBytes";
import type { ApiResult, CommandDraft, ServiceResponse } from "./types";
import { withinResponseDeadline } from "./responseDeadline";
import { localNotice } from "./notices";
import { type EndpointId, endpoints, wireContractFingerprint } from "@contracts/endpoint-catalog";
import { isHostFailure, isResponse } from "./responseGuards";
export type * from "./types";
export { isCurrentCase } from "./types";
export { isMutationUncertain, resultNotice } from "./outcomes";
const responseLimit = 16 * 1024 * 1024;
const readJson = async (response: Response, signal: AbortSignal): Promise<unknown> => {
  if (
    response.headers.get("content-type")?.split(";", 1)[0]?.trim().toLowerCase() !==
    "application/json"
  ) {
    void response.body?.cancel().catch(() => undefined);
    return null;
  }
  try {
    return strictJsonBytes(await responseBytes(response, responseLimit, signal));
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
    return { kind: "hostFailure", failure: value, status: response.status };
  }
  return { kind: "deliveryFailure", notice: { kind: "invalidHttp", status: response.status } };
};
const send = async <K extends EndpointId>(
  id: K,
  token: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
): Promise<ApiResult<ServiceResponse<K>>> => {
  const endpoint = endpoints.find((candidate) => candidate.id === id)!;
  const headers: Record<string, string> = {
    Accept: "application/json",
    "ClaimCore-Contract": wireContractFingerprint,
  };
  if (token !== undefined) {
    headers["X-ClaimCore-Antiforgery"] = token;
  }
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  try {
    const response = await fetch(endpoint.path, {
      method: endpoint.method,
      headers,
      body: body ?? null,
      ...deliveryOptions(signal),
    });
    return await result(id, response, signal);
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
  preview: (draft: CommandDraft, token: string) => request("case.preview", token, draft),
  execute: (bytes: string, token: string) =>
    withinResponseDeadline((owned) => send("case.execute", token, bytes, owned)),
};
