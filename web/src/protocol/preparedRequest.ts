import type { RequestByEndpoint, endpoints } from "@contracts/client";
import { buildRequest, requestIssue } from "./client";
export class ProtocolInputError extends Error {
  readonly path: ReadonlyArray<string>;
  constructor(path: ReadonlyArray<string>) {
    super("Input does not satisfy the wire format.");
    this.path = path;
  }
}
const preparedIdentity = Symbol("prepared request");
type AcceptingEndpoint = Extract<(typeof endpoints)[number], { mutates: true }>["id"];
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
  const assembled = buildRequest(endpoint, input);
  const issue = requestIssue(endpoint, assembled);
  if (issue !== null) {
    throw new ProtocolInputError(issue);
  }
  const detached = structuredClone(assembled);
  freeze(detached);
  return Object.freeze({
    endpoint,
    input: detached,
    bytes: JSON.stringify(detached),
    [preparedIdentity]: true as const,
  });
};
