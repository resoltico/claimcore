import { fireEvent } from "./presentation-test-support";
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
  const control = document.querySelector<HTMLInputElement>(
    `input[name="${name}"],input[data-field-name="${name}"]`,
  );
  if (control === null) {
    throw new Error(`Missing catalogue input: ${name}`);
  }
  fireEvent.change(control, {
    target: {
      value,
    },
  });
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
