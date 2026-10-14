/** One delivery policy for same-origin API calls. */
export const deliveryOptions = (signal: AbortSignal): RequestInit => ({
  credentials: "same-origin",
  redirect: "error",
  cache: "no-store",
  signal,
});
