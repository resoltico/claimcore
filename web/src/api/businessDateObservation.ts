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
const humanContextOwned = (document: Record<string, unknown> | null, owner: Registration) => {
  const context = object(document?.["subject"]);
  return (
    context?.["kind"] === "REQUESTER" &&
    context["actorId"] === owner.actorId &&
    context["installationId"] === owner.installationId
  );
};
const humanDocument = (outcome: Record<string, unknown> | null) => {
  switch (outcome?.["kind"]) {
    case "SUCCEEDED":
    case "ACCEPTED":
    case "ALREADY_ACCEPTED":
      return object(outcome?.["document"]);
    case "REVIEWED":
      return object(object(outcome?.["review"])?.["document"]);
    default:
      return null;
  }
};
const humanDate = (endpoint: string, envelope: Record<string, unknown>, owner: Registration) => {
  const document = humanDocument(object(envelope["outcome"]));
  if (endpoint === "context" && !humanContextOwned(document, owner)) {
    return undefined;
  }
  const basis = object(document?.["basis"]);
  const observation = basis?.["kind"] === "SNAPSHOT" ? object(basis["observation"]) : null;
  return observation?.["businessDate"];
};
const observedDate = (endpoint: string, response: unknown, owner: Registration) => {
  const envelope = object(response);
  return envelope?.["representation"] === "HUMAN"
    ? humanDate(endpoint, envelope, owner)
    : undefined;
};
/** Capture ownership at dispatch: late replies cannot attach themselves to a newer session. */
export const captureBusinessDateObserver = (endpoint: string, credential: string | undefined) => {
  const owner = current?.credential === credential ? current : null;
  const dispatch = owner === null ? 0 : ++owner.dispatched;
  return (response: unknown): void => {
    if (owner === null || current !== owner || dispatch < owner.observedDispatch) {
      return;
    }
    const date = observedDate(endpoint, response, owner);
    if (typeof date === "string") {
      owner.observedDispatch = dispatch;
      owner.observed(date);
    }
  };
};
