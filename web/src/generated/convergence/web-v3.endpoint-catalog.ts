/* Generated from ClaimCore.Contracts. Do not edit. */
import { webV3CaseworkEndpoints } from "./web-v3.endpoint-catalog.casework";
import { webV3AuthorityEndpoints } from "./web-v3.endpoint-catalog.authority";

export const webV3WireContractFingerprint =
  "aeea395587897fb0ca56d19de24dbf59263a66f7376aafde6166d27d4cae5d34";

export const webV3HostFailureStatuses = [400, 401, 403, 404, 405, 413, 415, 429, 500] as const;

export const webV3Endpoints = [...webV3CaseworkEndpoints, ...webV3AuthorityEndpoints] as const;

export type WebV3EndpointId = (typeof webV3Endpoints)[number]["id"];

export const isWebV3EndpointId = (value: string): value is WebV3EndpointId =>
  webV3Endpoints.some((endpoint) => endpoint.id === value);
