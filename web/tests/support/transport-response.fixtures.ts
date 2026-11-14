import { type RequestByEndpoint } from "@contracts/client";
import { ReadableStream } from "node:stream/web";
import { vi } from "vitest";
import { isObject } from "../../src/protocol/client";
export const requestAddress = (value: RequestInfo | URL): string => {
  if (typeof value === "string") {
    return value;
  }
  if (value instanceof URL) {
    return value.href;
  }
  return value.url;
};
const operationEcho = (endpoint: string): object => {
  const body = vi
    .mocked(globalThis.fetch)
    .mock?.calls.findLast(([path]) => requestAddress(path).endsWith(`/api/${endpoint}`))?.[1]?.body;
  const request: unknown = typeof body === "string" ? JSON.parse(body) : null;
  let echo: object = {};
  if (
    isObject(request) &&
    "operationId" in request &&
    ["operation.preview", "operation.execute", "operation.get"].includes(endpoint)
  ) {
    echo = {
      operationId: request["operationId"],
    };
  }
  return echo;
};
/** Serialize a complete authored current envelope, correlating only the captured request identity. */
export const endpointResponse = (endpoint: string, reply: unknown, status = 200): Response =>
  new Response(
    new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          const value = isObject(reply)
            ? {
                ...reply,
                ...operationEcho(endpoint),
              }
            : reply;
          controller.enqueue(new TextEncoder().encode(JSON.stringify(value)));
          controller.close();
        },
      },
      {
        highWaterMark: 0,
      },
    ) as BodyInit,
    {
      status,
      headers: {
        "content-type": "application/json",
      },
    },
  );

/** Refuse a non-text request rather than silently stringifying an unsupported body. */
export const requestText = (body: BodyInit | null | undefined): string => {
  if (typeof body !== "string") {
    throw new Error("Expected a serialized JSON request body");
  }
  return body;
};
export const operationRequest = (body: BodyInit | null | undefined) => {
  const operationExecuteRequest = JSON.parse(
    requestText(body),
  ) as RequestByEndpoint["operation.execute"];
  return operationExecuteRequest;
};
