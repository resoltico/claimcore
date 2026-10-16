import { ReadableStream } from "node:stream/web";
import { vi } from "vitest";
import { isObject } from "@contracts/browser-contract";
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
const responseData = (
  endpoint: string,
  tag: string,
  data: unknown,
  request: Record<string, unknown>,
): unknown => {
  if (isObject(data) && ["ACCEPTED", "ALREADY_ACCEPTED", "OUTCOME_UNKNOWN"].includes(tag)) {
    const receipt = { ...data, operationId: request["operationId"] };
    if (tag === "OUTCOME_UNKNOWN" || !isObject(request["act"])) {
      return receipt;
    }
    return {
      ...receipt,
      act: request["act"]["kind"],
      ...(endpoint.startsWith("case.")
        ? {
            caseReference: request["caseReference"],
            revision: Number(request["expectedRevision"]) + 1,
          }
        : {}),
    };
  }
  if (tag === "REVIEWED" && isObject(data) && isObject(data["review"])) {
    const { review } = data;
    return {
      ...data,
      review: { ...review, before: snapshot(review["before"]), after: snapshot(review["after"]) },
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
    ["case.preview", "case.execute", "authority.execute"].includes(endpoint)
  ) {
    echo = { operationId: request["operationId"] };
    data = responseData(endpoint, tag, data, request);
  }
  if (endpoint === "case.get" && tag === "SUCCEEDED" && isObject(data) && !("offers" in data)) {
    data = { ...data, offers: [], historyDetails: ["SUMMARY", "FULL"] };
  }
  return { endpoint, ...echo, outcome: { tag, data } };
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
