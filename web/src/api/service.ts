import { captureBusinessDateObserver } from "./businessDateObservation";
import type { PreparedRequest } from "../protocol/preparedRequest";
import { sendHttp } from "./transport";
import { responseBytes } from "./responseBytes";
import type { ApiResult, ServiceResponse } from "../protocol/types";
import { withinResponseDeadline } from "./responseDeadline";
import { localNotice } from "../protocol/notices";
import {
  type EndpointId,
  type Representation,
  representationHeader,
  type RequestByEndpoint,
  endpoints,
  strictByteProfile,
  consumerContractIdentity,
  correlationOf,
} from "@contracts/client";
import { admitBytes } from "../protocol/schema";
import { isHostFailure, isResponseFor, isObject } from "../protocol/client";
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
const echoesOperationId = <K extends EndpointId>(
  id: K,
  input: RequestByEndpoint[K] | undefined,
  reply: unknown,
): boolean =>
  !correlationOf[id] ||
  (input !== undefined &&
    "operationId" in input &&
    isObject(reply) &&
    input.operationId === reply["operationId"]);

const result = async <K extends EndpointId, R extends Representation>(
  id: K,
  response: Response,
  signal: AbortSignal,
  input: RequestByEndpoint[K] | undefined,
  representation: R,
): Promise<ApiResult<ServiceResponse<K, R>>> => {
  const value = await readJson(response, signal);
  if (signal.aborted) {
    return { kind: "deliveryFailure", notice: localNotice("cancelled") };
  }
  if (
    response.status === 200 &&
    isResponseFor(id, representation, value) &&
    echoesOperationId(id, input, value)
  ) {
    return { kind: "outcome", value, status: response.status };
  }
  if (isHostFailure(value, response.status)) {
    return { kind: "hostFailure", failure: value.hostFailure, status: response.status };
  }
  return { kind: "deliveryFailure", notice: { kind: "invalidHttp", status: response.status } };
};
function send<K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
  input: RequestByEndpoint[K] | undefined,
  installationId?: string,
): Promise<ApiResult<ServiceResponse<K>>>;
function send<K extends EndpointId, R extends Representation>(
  id: K,
  credential: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
  input: RequestByEndpoint[K] | undefined,
  installationId: string | undefined,
  representation: R,
): Promise<ApiResult<ServiceResponse<K, R>>>;
function send<K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: string | undefined,
  signal: AbortSignal,
  input: RequestByEndpoint[K] | undefined,
  installationId?: string,
  representation: Representation = "HUMAN",
): Promise<ApiResult<ServiceResponse<K, Representation>>> {
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
  if (endpoint.requiresRepresentation) {
    headers[representationHeader] = representation;
  }
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
  const network = async (
    deliveredSignal: AbortSignal,
  ): Promise<ApiResult<ServiceResponse<K, Representation>>> => {
    if (deliveredSignal.aborted) {
      return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
    }
    try {
      const response = await sendHttp(endpoint, headers, body, deliveredSignal);
      const reply = await result(id, response, deliveredSignal, input, representation);
      if (!signal.aborted && !deliveredSignal.aborted && reply.kind === "outcome") {
        observeDate(reply.value);
      }
      return reply;
    } catch {
      return {
        kind: "deliveryFailure",
        notice: localNotice(deliveredSignal.aborted ? "cancelled" : "unreachable"),
      };
    }
  };
  return network(signal);
}
function request<K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: RequestByEndpoint[K] | undefined,
  signal?: AbortSignal,
  installationId?: string,
): Promise<ApiResult<ServiceResponse<K>>>;
function request<K extends EndpointId, R extends Representation>(
  id: K,
  credential: string | undefined,
  body: RequestByEndpoint[K] | undefined,
  signal: AbortSignal | undefined,
  installationId: string | undefined,
  representation: R,
): Promise<ApiResult<ServiceResponse<K, R>>>;
function request<K extends EndpointId>(
  id: K,
  credential: string | undefined,
  body: RequestByEndpoint[K] | undefined,
  signal?: AbortSignal,
  installationId?: string,
  representation: Representation = "HUMAN",
): Promise<ApiResult<ServiceResponse<K, Representation>>> {
  return withinResponseDeadline(
    (owned) =>
      send(
        id,
        credential,
        body === undefined ? undefined : JSON.stringify(body),
        owned,
        body,
        installationId,
        representation,
      ),
    signal,
  );
}
export function requestEndpoint<K extends EndpointId>(
  id: K,
  credential: string,
  input: RequestByEndpoint[K],
  signal?: AbortSignal,
  installationId?: string,
): Promise<ApiResult<ServiceResponse<K>>>;
export function requestEndpoint<K extends EndpointId, R extends Representation>(
  id: K,
  credential: string,
  input: RequestByEndpoint[K],
  signal: AbortSignal | undefined,
  installationId: string | undefined,
  representation: R,
): Promise<ApiResult<ServiceResponse<K, R>>>;
export function requestEndpoint<K extends EndpointId>(
  id: K,
  credential: string,
  input: RequestByEndpoint[K],
  signal?: AbortSignal,
  installationId?: string,
  representation: Representation = "HUMAN",
): Promise<ApiResult<ServiceResponse<K, Representation>>> {
  return request(id, credential, input, signal, installationId, representation);
}
export const service = {
  session: (signal?: AbortSignal) => request("session", undefined, undefined, signal),
  logout: (credential: string) => request("session.logout", credential, {}),
  context: (credential: string, signal?: AbortSignal) => request("context", credential, {}, signal),
  get: (caseReference: string, credential: string, signal?: AbortSignal) =>
    request("case.get", credential, { target: { kind: "REFERENCE", caseReference } }, signal),
  list: (cursor: string | null, limit: number, credential: string, signal?: AbortSignal) =>
    request("case.list", credential, { ...(cursor === null ? {} : { cursor }), limit }, signal),
  peopleList: (cursor: string | null, credential: string, signal?: AbortSignal) =>
    request(
      "people.list",
      credential,
      { ...(cursor === null ? {} : { cursor }), limit: 50 },
      signal,
    ),
  peopleGrants: (
    actorId: string,
    cursor: string | null,
    credential: string,
    signal?: AbortSignal,
  ) =>
    request(
      "people.grants",
      credential,
      { actorId, ...(cursor === null ? {} : { cursor }), limit: 50 },
      signal,
    ),
  operationGet: (
    operationId: string,
    credential: string,
    installationId: string | undefined,
    signal?: AbortSignal,
  ) => request("operation.get", credential, { operationId }, signal, installationId),
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
