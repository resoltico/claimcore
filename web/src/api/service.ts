import { captureBusinessDateObserver, type BusinessDateScope } from "./businessDateObservation";
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
  attachmentTransport,
  strictByteProfile,
  consumerContractIdentity,
  correlationOf,
  isEndpointResponse,
} from "@contracts/client";
import { operationTask } from "../protocol/taskCatalog";
import { admitBytes } from "../protocol/schema";
import { isHostFailure, isObject } from "../protocol/client";
export type * from "../protocol/types";
export { isCurrentCase } from "../protocol/types";
export { resultNotice } from "./outcomes";
/** Typed endpoint bindings belong to the transport; operation tasks are selected from Core controls. */
export const apiEndpoints = {
  session: "session",
  sessionLogout: "session.logout",
  operationPreview: "operation.preview",
  operationExecute: "operation.execute",
  caseGet: "case.get",
  caseAssignees: "case.assignees",
  caseList: "case.list",
  caseSummary: "case.summary",
  caseTimeline: "case.timeline",
  caseNotes: "case.notes",
  caseAttachments: "case.attachments",
  casePayments: "case.payments",
  reminderList: "reminder.list",
  reminderGet: "reminder.get",
  workViews: "work.views",
  operationGet: "operation.get",
  peopleList: "people.list",
  peopleGrants: "people.grants",
  peopleHistory: "people.history",
  peopleRequests: "people.requests",
  context: "context",
  peopleRequestsDismiss: "people.requests.dismiss",
  terminateOwnSessions: "session.terminateOwn",
  terminatePersonSessions: "people.terminateSessions",
} as const satisfies Readonly<Record<string, EndpointId>>;
const businessDateScope = (id: EndpointId): BusinessDateScope =>
  id === apiEndpoints.context ? "REQUESTER" : "RESOURCE";
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
    typeof input.operationId === "string" &&
    isObject(reply) &&
    input.operationId === reply["operationId"]);

const structurallyValid = (id: EndpointId, value: unknown): boolean =>
  isEndpointResponse(id, value);

const result = async <K extends EndpointId, R extends Representation>(
  id: K,
  response: Response,
  signal: AbortSignal,
  input: RequestByEndpoint[K] | undefined,
  representation: R,
): Promise<ApiResult<ServiceResponse<K, R>>> => {
  const value = await readJson(response, signal);
  const { requiresRepresentation } = endpoints.find((endpoint) => endpoint.id === id)!;
  if (signal.aborted) {
    return { kind: "deliveryFailure", notice: localNotice("cancelled") };
  }
  if (
    response.status === 200 &&
    isObject(value) &&
    (!requiresRepresentation || value["representation"] === representation) &&
    echoesOperationId(id, input, value) &&
    structurallyValid(id, value)
  ) {
    // oxlint-disable-next-line typescript/no-unsafe-type-assertion -- Generated structural admission plus representation and operation correlation establish this endpoint response.
    return { kind: "outcome", value: value as ServiceResponse<K, R>, status: response.status };
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
      if (new TextEncoder().encode(body).length > strictByteProfile.maximumRequestBytes) {
        throw new Error("Request exceeds its byte allowance.");
      }
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
  const observeDate = captureBusinessDateObserver(businessDateScope(id), credential);
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
const uploadAttachment = (
  prepared: PreparedRequest<"operation.execute">,
  file: File,
  credential: string,
  installationId: string,
  callerSignal: AbortSignal,
): Promise<ApiResult<ServiceResponse<"operation.execute">>> =>
  withinResponseDeadline(
    async (signal) => {
      if (
        signal.aborted ||
        operationTask(prepared.input.task).execution !== "ATTACHMENT_UPLOAD" ||
        file.size > attachmentTransport.maximumFileBytes
      ) {
        return { kind: "deliveryFailure", notice: localNotice("cancelled"), dispatched: false };
      }
      const form = new FormData();
      form.append(
        attachmentTransport.manifestPartName,
        new Blob([prepared.bytes], { type: attachmentTransport.manifestMediaType }),
        attachmentTransport.manifestFileName,
      );
      form.append(
        attachmentTransport.filePartName,
        new Blob([file], { type: attachmentTransport.downloadMediaType }),
        attachmentTransport.fileName,
      );
      const observeDate = captureBusinessDateObserver("RESOURCE", credential);
      try {
        const response = await sendHttp(
          { path: attachmentTransport.uploadPath, method: "POST" },
          {
            Accept: "application/json",
            "ClaimCore-Contract": consumerContractIdentity,
            "ClaimCore-Installation": installationId,
            "X-ClaimCore-Antiforgery": credential,
            [representationHeader]: "HUMAN",
          },
          form,
          signal,
        );
        if (
          response.status === 200 &&
          response.headers.get("ClaimCore-Installation") !== installationId
        ) {
          void response.body?.cancel().catch(() => undefined);
          return {
            kind: "deliveryFailure",
            notice: { kind: "invalidHttp", status: response.status },
          };
        }
        const reply = await result(prepared.endpoint, response, signal, prepared.input, "HUMAN");
        if (!signal.aborted && reply.kind === "outcome") {
          observeDate(reply.value);
        }
        return reply;
      } catch {
        return {
          kind: "deliveryFailure",
          notice: localNotice(signal.aborted ? "cancelled" : "unreachable"),
        };
      }
    },
    callerSignal,
    attachmentTransport.deadlineMilliseconds,
  );

export const service = {
  uploadAttachment,
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
