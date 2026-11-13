import { strictByteProfile, type RequestByEndpoint, type endpoints } from "@contracts/client";
const preparedIdentity = Symbol("prepared request");
type AcceptingEndpoint = Extract<(typeof endpoints)[number], { effect: "LEDGER_EXECUTION" }>["id"];
export type PreparedRequest<K extends AcceptingEndpoint> = {
  readonly endpoint: K;
  readonly input: RequestByEndpoint[K];
  readonly bytes: string;
  readonly [preparedIdentity]: true;
};
const freeze = (value: unknown): void => {
  if (typeof value === "object" && value !== null) {
    for (const child of Object.values(value)) {
      freeze(child);
    }
    Object.freeze(value);
  }
};
export const prepareRequest = <K extends AcceptingEndpoint>(
  endpoint: K,
  input: RequestByEndpoint[K],
): PreparedRequest<K> => {
  const detached = structuredClone(input);
  freeze(detached);
  const bytes = JSON.stringify(detached);
  if (new TextEncoder().encode(bytes).length > strictByteProfile.maximumRequestBytes) {
    throw new Error("Request exceeds its byte allowance.");
  }
  return Object.freeze({
    endpoint,
    input: detached,
    bytes,
    [preparedIdentity]: true as const,
  });
};
