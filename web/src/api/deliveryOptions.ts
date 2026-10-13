/** One delivery policy for ordinary API calls and recovery exports. */
export const deliveryOptions = (signal: AbortSignal): RequestInit => ({
  credentials: "same-origin",
  redirect: "error",
  signal,
});
