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
const reviewData = (kind: string, data: unknown, request: Record<string, unknown>): unknown => {
  if (
    kind === "REVIEWED" &&
    isObject(data) &&
    isObject(data["review"]) &&
    isObject(data["review"]["subject"])
  ) {
    const { subject } = data["review"];
    return {
      ...data,
      review: {
        subject: {
          ...subject,
          kind: request["kind"],
          ...(subject["kind"] === "CASE"
            ? { before: snapshot(subject["before"]), after: snapshot(subject["after"]) }
            : {}),
        },
      },
    };
  }
  return data;
};
const responseData = (kind: string, data: unknown, request: Record<string, unknown>): unknown => {
  if (kind === "OUTCOME_UNKNOWN") {
    return null;
  }
  if (
    isObject(data) &&
    ["ACCEPTED", "ALREADY_ACCEPTED"].includes(kind) &&
    isObject(data["subject"])
  ) {
    const { subject } = data;
    return {
      ...data,
      operationId: request["operationId"],
      subject: {
        ...subject,
        ...(isObject(request["act"]) ? { act: request["act"]["kind"] } : {}),
        ...(subject["kind"] === "CASE" && request["kind"] === "CASE"
          ? {
              caseReference: request["caseReference"],
              revision: Number(request["expectedRevision"]) + 1,
            }
          : {}),
      },
    };
  }
  return reviewData(kind, data, request);
};
const envelope = (endpoint: string, kind: string, original: unknown) => {
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
    data = responseData(kind, data, request);
  }
  return {
    ...echo,
    outcome: { kind, data },
  };
};
/** Synthetic server replies are built from the bytes handed to the mocked platform transport. */
export const transportResponse = (
  endpoint: string,
  kind: string,
  original: unknown,
  status: number,
): Response =>
  new Response(
    new ReadableStream<Uint8Array>(
      {
        pull(controller) {
          controller.enqueue(
            new TextEncoder().encode(JSON.stringify(envelope(endpoint, kind, original))),
          );
          controller.close();
        },
      },
      { highWaterMark: 0 },
    ) as unknown as BodyInit,
    { status, headers: { "content-type": "application/json" } },
  );
