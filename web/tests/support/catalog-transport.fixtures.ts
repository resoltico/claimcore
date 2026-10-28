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
  const control =
    document.querySelector<HTMLElement>(`[data-field-name="${name.split(".").at(-1)}"]`) ??
    document.querySelector<HTMLElement>(`input[name="${name}"]`);
  if (control === null) {
    throw new Error(`Missing catalogue input: ${name}`);
  }
  changeField(control, value);
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
