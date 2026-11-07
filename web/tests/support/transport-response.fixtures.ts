import { fixtureView } from "./read-view.fixtures";
import type { PresentedView } from "@contracts/client";
import { ReadableStream } from "node:stream/web";
import { vi } from "vitest";
import { isObject } from "../../src/protocol/client";
const requestAddress = (value: RequestInfo | URL): string => {
  if (typeof value === "string") {
    return value;
  }
  if (value instanceof URL) {
    return value.href;
  }
  return value.url;
};
const envelope = (endpoint: string, kind: string, original: unknown, view: PresentedView) => {
  const body = vi
    .mocked(globalThis.fetch)
    .mock?.calls.findLast(([path]) => requestAddress(path).endsWith(`/api/${endpoint}`))?.[1]?.body;
  const request: unknown = typeof body === "string" ? JSON.parse(body) : null;
  const data = original;
  let echo: object = {};
  if (
    isObject(request) &&
    "operationId" in request &&
    ["operation.preview", "operation.execute", "operation.get"].includes(endpoint)
  ) {
    echo = { operationId: request["operationId"] };
  }
  return {
    ...echo,
    outcome: {
      kind,
      data,
      ...(["SUCCEEDED", "ACCEPTED", "ALREADY_ACCEPTED"].includes(kind) ? { view } : {}),
    },
  };
};
/** Synthetic server replies are built from the bytes handed to the mocked platform transport. */
export const transportResponse = (
  endpoint: string,
  kind: string,
  original: unknown,
  status: number,
  view: PresentedView = fixtureView(endpoint, kind, original),
): Response =>
  new Response(
    new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          controller.enqueue(
            new TextEncoder().encode(JSON.stringify(envelope(endpoint, kind, original, view))),
          );
          controller.close();
        },
      },
      { highWaterMark: 0 },
    ) as unknown as BodyInit,
    { status, headers: { "content-type": "application/json" } },
  );
