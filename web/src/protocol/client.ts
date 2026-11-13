import { endpoints, type EndpointId } from "@contracts/client";
export { isHostFailure } from "@contracts/client";
export const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null && !Array.isArray(value);
export const isEndpointId = (value: string): value is EndpointId =>
  endpoints.some((item) => item.id === value);
