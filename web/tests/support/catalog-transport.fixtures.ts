import { changeField } from "./field-entry";
import { isResponse } from "../../src/protocol/client";
import type { EndpointId, ResponseByEndpoint } from "@contracts/client";
export type Dispatch<T> = {
  path: string;
  bytes: string;
  input: T;
};
export const runtime = {
  productVersion: "0.10.0",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
export const input = (name: string, value: string) => {
  changeField(name, value);
};
export const response = <K extends EndpointId>(
  endpoint: K,
  value: ResponseByEndpoint[K],
): Response => {
  if (!isResponse(endpoint, value)) {
    throw new Error("Synthetic response violates the actual OpenAPI contract.");
  }
  return new Response(JSON.stringify(value), {
    status: 200,
    headers: {
      "content-type": "application/json",
    },
  });
};
