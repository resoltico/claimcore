/* Generated from ClaimCore.Contracts. Do not edit. */
import type { WebV2EndpointId } from "./web-v2.endpoint-catalog";
import type { HostFailure } from "./web-v2.types.core";
import type { WebV2ReadResponseByEndpoint } from "./web-v2.types.responses.read";
import type { WebV2CommandResponseByEndpoint } from "./web-v2.types.responses.command";
import type { WebV2RecoveryResponseByEndpoint } from "./web-v2.types.responses.recovery";

export type WebV2ResponseByEndpoint = WebV2ReadResponseByEndpoint &
  WebV2CommandResponseByEndpoint &
  WebV2RecoveryResponseByEndpoint;
export type WebV2Response<K extends WebV2EndpointId> = WebV2ResponseByEndpoint[K];
export type EndpointOutcome = WebV2Response<WebV2EndpointId>;
export type ApiResult<T> =
  | { readonly kind: "outcome"; readonly value: T; readonly status: number }
  | { readonly kind: "hostFailure"; readonly failure: HostFailure; readonly status: number }
  | { readonly kind: "deliveryFailure"; readonly message: string };
