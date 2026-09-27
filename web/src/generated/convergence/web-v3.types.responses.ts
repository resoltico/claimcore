/* Generated from ClaimCore.Contracts. Do not edit. */
import type { WebV3EndpointId } from "./web-v3.endpoint-catalog";
import type { WebV3ReadResponseByEndpoint } from "./web-v3.types.responses.read";
import type { WebV3AuthorityResponseByEndpoint } from "./web-v3.types.responses.authority";
import type { WebV3LifecycleResponseByEndpoint } from "./web-v3.types.responses.lifecycle";
import type { WebV3CommandResponseByEndpoint } from "./web-v3.types.responses.command";
import type { WebV3RecoveryResponseByEndpoint } from "./web-v3.types.responses.recovery";

export type WebV3ResponseByEndpoint = WebV3ReadResponseByEndpoint &
  WebV3AuthorityResponseByEndpoint &
  WebV3LifecycleResponseByEndpoint &
  WebV3CommandResponseByEndpoint &
  WebV3RecoveryResponseByEndpoint;
export type WebV3Response<K extends WebV3EndpointId> = WebV3ResponseByEndpoint[K];
export type EndpointOutcome = WebV3Response<WebV3EndpointId>;
