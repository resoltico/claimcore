import { deliveryOptions } from "./deliveryOptions";
/** The only browser network handoff; callers supply immutable serialized bytes. */
export const sendHttp = (
  endpoint: { readonly path: string; readonly method: string },
  headers: Readonly<Record<string, string>>,
  body: BodyInit | undefined,
  signal: AbortSignal,
): Promise<Response> =>
  fetch(endpoint.path, {
    method: endpoint.method,
    headers,
    body: body ?? null,
    ...deliveryOptions(signal),
  });

/** Public text never establishes or refreshes a session. */
export const getCatalogHttp = (path: string, signal: AbortSignal): Promise<Response> =>
  fetch(path, {
    method: "GET",
    credentials: "omit",
    redirect: "error",
    headers: { Accept: "application/json" },
    signal,
  });

export const getAttachmentPreviewResourceHttp = (
  path: string,
  signal: AbortSignal,
): Promise<Response> =>
  fetch(path, {
    credentials: "omit",
    redirect: "error",
    signal,
  });
