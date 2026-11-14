import { changeField } from "./field-entry";
import type { EndpointId, ResponseByEndpoint } from "@contracts/client";
export type Dispatch<T> = {
  path: string;
  bytes: string;
  input: T;
};
export const runtime = {
  productVersion: "0.10.0",
  observedAt: "2026-09-09T12:00:00.000000Z",
  businessDate: "2026-09-09",
  timeZone: "Etc/UTC",
};
export const input = (name: string, value: string) => {
  changeField(name, value);
};
export const response = <K extends EndpointId>(
  _endpoint: K,
  value: ResponseByEndpoint[K],
): Response =>
  new Response(JSON.stringify(value), {
    status: 200,
    headers: {
      "content-type": "application/json",
    },
  });
