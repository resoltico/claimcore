import { afterEach, vi, type Mock } from "vitest";
import type { EndpointId, RequestByEndpoint } from "@contracts/client";
import type { ApiResult, EndpointOutcome, ServiceResponse } from "../../src/api/service";
import * as api from "../../src/api/service";
import { isRequest } from "../../src/protocol/client";
type Handler = (
  credential: string,
  input: RequestByEndpoint[EndpointId],
  signal?: AbortSignal,
) => Promise<ApiResult<EndpointOutcome>>;
type EndpointFunction<K extends EndpointId> = (
  credential: string,
  input: RequestByEndpoint[K],
  signal?: AbortSignal,
) => Promise<ApiResult<ServiceResponse<K>>>;
const mocks = new Map<EndpointId, unknown>();
export const getEndpointMock = <K extends EndpointId>(endpoint: K): Mock<EndpointFunction<K>> => {
  const mock = mocks.get(endpoint);
  if (mock === undefined) {
    throw new Error(`No endpoint mock for ${endpoint}.`);
  }
  return mock as Mock<EndpointFunction<K>>;
};
const handlers = new Map<EndpointId, Handler>();
afterEach(() => {
  handlers.clear();
  mocks.clear();
  vi.restoreAllMocks();
});
export const mockEndpoint = <K extends EndpointId>(endpoint: K) => {
  const mock =
    vi.fn<
      (
        credential: string,
        input: RequestByEndpoint[K],
        signal?: AbortSignal,
      ) => Promise<ApiResult<ServiceResponse<K>>>
    >();
  if (!vi.isMockFunction(api.requestEndpoint)) {
    handlers.clear();
    const original = api.requestEndpoint;
    vi.spyOn(api, "requestEndpoint").mockImplementation((id, credential, input, signal) => {
      const handler = handlers.get(id);
      return handler === undefined
        ? original(id, credential, input, signal)
        : handler(credential, input, signal);
    });
  }
  handlers.set(endpoint, (credential, input, signal) => {
    if (!isRequest(endpoint, input)) {
      throw new Error(`Endpoint fixture received malformed ${endpoint} input.`);
    }
    return mock(credential, input, signal);
  });
  mocks.set(endpoint, mock);
  return mock;
};
