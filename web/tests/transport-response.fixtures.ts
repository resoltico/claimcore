import { ReadableStream } from "node:stream/web";
import { vi } from "vitest";
import { isObject } from "../src/protocol/client";
const snapshot = (value: unknown): unknown => {
  if (!isObject(value)) {
    return value;
  }
  const { state } = value;
  return {
    caseReference: value["caseReference"],
    revision: value["revision"],
    state: isObject(state)
      ? Object.fromEntries(Object.entries(state).filter(([key]) => key !== "availableActs"))
      : state,
  };
};
const responseData = (tag: string, data: unknown, request: Record<string, unknown>): unknown => {
  if (tag === "OUTCOME_UNKNOWN") {
    return null;
  }
  if (isObject(data) && ["ACCEPTED", "ALREADY_ACCEPTED"].includes(tag) && isObject(data["scope"])) {
    const { scope } = data;
    return {
      ...data,
      operationId: request["operationId"],
      scope: {
        ...scope,
        ...(isObject(request["act"]) ? { act: request["act"]["kind"] } : {}),
        ...(scope["kind"] === "CASE" && request["scope"] === "CASE"
          ? {
              caseReference: request["caseReference"],
              revision: Number(request["expectedRevision"]) + 1,
            }
          : {}),
      },
    };
  }
  if (tag === "REVIEWED" && isObject(data) && isObject(data["review"])) {
    const { review } = data;
    return {
      ...data,
      review: {
        ...review,
        scope: request["scope"],
        before: snapshot(review["before"]),
        after: snapshot(review["after"]),
      },
    };
  }
  return data;
};
const envelope = (endpoint: string, tag: string, original: unknown) => {
  const body = vi.mocked(globalThis.fetch).mock?.calls.at(-1)?.[1]?.body;
  const request: unknown = typeof body === "string" ? JSON.parse(body) : null;
  let data = original;
  let echo: object = {};
  if (
    isObject(request) &&
    "operationId" in request &&
    ["operation.preview", "operation.execute"].includes(endpoint)
  ) {
    echo = { operationId: request["operationId"] };
    data = responseData(tag, data, request);
  }
  return { ...echo, outcome: { tag, data } };
};
/** Synthetic replies are correlated with the bytes handed to the mocked platform transport. */
export const transportResponse = (
  endpoint: string,
  tag: string,
  original: unknown,
  status: number,
): Response =>
  new Response(
    new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          controller.enqueue(
            new TextEncoder().encode(JSON.stringify(envelope(endpoint, tag, original))),
          );
          controller.close();
        },
      },
      { highWaterMark: 0 },
    ) as unknown as BodyInit,
    { status, headers: { "content-type": "application/json" } },
  );
