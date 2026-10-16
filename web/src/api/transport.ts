import { deliveryOptions } from "./deliveryOptions";
/** The only browser network handoff; callers supply immutable serialized bytes. */
export const sendHttp = (
  endpoint: { readonly path: string; readonly method: string },
  headers: Readonly<Record<string, string>>,
  body: string | undefined,
  signal: AbortSignal,
): Promise<Response> =>
  fetch(endpoint.path, {
    method: endpoint.method,
    headers,
    body: body ?? null,
    ...deliveryOptions(signal),
  });
