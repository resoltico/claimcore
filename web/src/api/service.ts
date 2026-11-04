import { captureBusinessDateObserver } from "./businessDateObservation";
import { ReadAdmission } from "./readAdmission";
import { replyCorrelates } from "../protocol/replyCorrelation";
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
const readAdmission = new ReadAdmission();
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
  input: unknown,
): Promise<ApiResult<ServiceResponse<K>>> => {
  const value = await readJson(response, signal);
  if (response.status === 200 && isResponse(id, value) && replyCorrelates(id, input, value)) {
    return { kind: "outcome", value, status: response.status };
  }
  if (isHostFailure(value, response.status)) {
    return { kind: "hostFailure", failure: value.hostFailure, status: response.status };
  }
  return { kind: "deliveryFailure", notice: { kind: "invalidHttp", status: response.status } };
};
const send = <K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
  input: unknown,
  installationId?: string,
): Promise<ApiResult<ServiceResponse<K>>> => {
  if (signal.aborted) {
    return Promise.resolve({
      kind: "deliveryFailure",
      notice: localNotice("cancelled"),
      dispatched: false,
    });
  }
  try {
    if (body !== undefined) {
      admitBytes(new TextEncoder().encode(body));
    }
  } catch {
    return Promise.resolve({
      kind: "deliveryFailure",
      notice: localNotice("incomplete"),
      dispatched: false,
    });
  }
  const endpoint = endpoints.find((candidate) => candidate.id === id)!;
  if (endpoint.requiresInstallation && installationId === undefined) {
    return Promise.resolve({
      kind: "deliveryFailure",
      notice: localNotice("invalidInput"),
      dispatched: false,
    });
  }
  const headers: Record<string, string> = {
    Accept: "application/json",
    "ClaimCore-Contract": consumerContractIdentity,
  };
  if (installationId !== undefined) {
    headers["ClaimCore-Installation"] = installationId;
  }
  if (credential !== undefined) {
    headers["X-ClaimCore-Antiforgery"] = credential;
  }
  if (body !== undefined) {
    headers["Content-Type"] = "application/json";
  }
  const observeDate = captureBusinessDateObserver(id, credential);
  const network = async (deliveredSignal: AbortSignal): Promise<ApiResult<ServiceResponse<K>>> => {
    if (deliveredSignal.aborted) {
      return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
    }
    try {
      const response = await sendHttp(endpoint, headers, body, deliveredSignal);
      const reply = await result(id, response, deliveredSignal, input);
      if (!signal.aborted && !deliveredSignal.aborted && reply.kind === "outcome") {
        observeDate(reply.value);
      }
      return reply;
    } catch {
      return { kind: "deliveryFailure", notice: localNotice("unreachable") };
    }
  };
  if (endpoint.effect !== "READ") {
    return network(signal);
  }
  return readAdmission.run(signal, async () => {
    // Obsolete observations still drain their bounded response after dispatch. Aborting the
    // fetch can finish before the server releases its request capacity.
    const reply = await withinResponseDeadline(network);
    return signal.aborted ? { kind: "deliveryFailure", notice: localNotice("cancelled") } : reply;
  });
};
const request = <K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: object | undefined,
  signal?: AbortSignal,
  installationId?: string,
) =>
  withinResponseDeadline(
    (owned) =>
      send(
        id,
        credential,
        body === undefined ? undefined : JSON.stringify(body),
        owned,
        body,
        installationId,
      ),
    signal,
  );
export const requestEndpoint = <K extends EndpointId>(
  id: K,
  credential: string,
  input: RequestByEndpoint[K],
  signal?: AbortSignal,
) => request(id, credential, input, signal);
export const service = {
  session: (signal?: AbortSignal) => request("session", undefined, undefined, signal),
  logout: (credential: string) => request("session.logout", credential, {}),
  context: (credential: string, signal?: AbortSignal) => request("context", credential, {}, signal),
  get: (caseReference: string, credential: string, signal?: AbortSignal) =>
    request("case.get", credential, { target: { kind: "REFERENCE", caseReference } }, signal),
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
        target: { kind: "REFERENCE", caseReference: input.caseReference },
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
  status: (
    operationId: string,
    credential: string,
    installationId: string | undefined,
    signal?: AbortSignal,
  ) => request("operation.status", credential, { operationId }, signal, installationId),
  resolve: (operationId: string, credential: string, installationId: string | undefined) =>
    request("operation.resolve", credential, { operationId }, undefined, installationId),
  preview: (
    prepared: PreparedRequest<"operation.execute">,
    credential: string,
    signal?: AbortSignal,
  ) =>
    withinResponseDeadline(
      (owned) => send("operation.preview", credential, prepared.bytes, owned, prepared.input),
      signal,
    ),
  execute: (
    prepared: PreparedRequest<"operation.execute">,
    credential: string,
    installationId: string | undefined,
  ) =>
    withinResponseDeadline((owned) =>
      send(prepared.endpoint, credential, prepared.bytes, owned, prepared.input, installationId),
    ),
};
