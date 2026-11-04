/** Only the current authenticated workspace may receive dates from validated responses. */
type DateObserver = {
  credential: string;
  installationId: string;
  actorId: string;
  observed: (date: string) => void;
};
type Registration = DateObserver & { dispatched: number; observedDispatch: number };
let current: Registration | null = null;
export const subscribeBusinessDates = (observer: DateObserver): (() => void) => {
  const registration = { ...observer, dispatched: 0, observedDispatch: 0 };
  current = registration;
  return () => {
    if (current === registration) {
      current = null;
    }
  };
};
const isObject = (value: unknown): value is Record<string, unknown> =>
  typeof value === "object" && value !== null;
const object = (value: unknown): Record<string, unknown> | null => (isObject(value) ? value : null);
const responseData = (response: unknown) => {
  const outcome = object(object(response)?.["outcome"]);
  return outcome?.["kind"] === "SUCCEEDED" ? object(outcome["data"]) : null;
};
const responseDate = (endpoint: string, data: Record<string, unknown>) => {
  if (endpoint === "context") {
    return data["businessDate"];
  }
  const basis =
    endpoint === "case.get" ? object(object(data["work"])?.["basis"]) : object(data["basis"]);
  return basis?.["businessDate"];
};
/** Capture ownership at dispatch: late replies cannot attach themselves to a newer session. */
export const captureBusinessDateObserver = (endpoint: string, credential: string | undefined) => {
  const owner = current?.credential === credential ? current : null;
  const dispatch = owner === null ? 0 : ++owner.dispatched;
  return (response: unknown): void => {
    if (owner === null || current !== owner || dispatch < owner.observedDispatch) {
      return;
    }
    const data = responseData(response);
    if (data === null) {
      return;
    }
    if (
      endpoint === "context" &&
      (data["installationId"] !== owner.installationId || data["actorId"] !== owner.actorId)
    ) {
      return;
    }
    const date = responseDate(endpoint, data);
    if (typeof date === "string") {
      owner.observedDispatch = dispatch;
      owner.observed(date);
    }
  };
};
