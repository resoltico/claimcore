import { endpoints, isResponse, type EndpointId, type ResponseByEndpoint } from "@contracts/client";
import { admitBytes } from "./schema";
export {
  buildRequest,
  requestIssue,
  isRequest,
  isResponse,
  isObject,
  isHostFailure,
} from "@contracts/client";
export { correlateReply } from "./correlation";

export const decodeResponse = <K extends EndpointId>(
  endpoint: K,
  bytes: Uint8Array,
): ResponseByEndpoint[K] => {
  const value = admitBytes(bytes);
  if (!isResponse(endpoint, value)) {
    throw new Error("Invalid endpoint response.");
  }
  return value;
};
export const isEndpointId = (value: string): value is EndpointId =>
  endpoints.some((item) => item.id === value);
