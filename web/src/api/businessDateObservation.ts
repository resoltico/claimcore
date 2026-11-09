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
    return object(data["requester"])?.["businessDate"];
  }
  const basis =
    endpoint === "case.get" ? object(object(data["work"])?.["basis"]) : object(data["basis"]);
  return basis?.["businessDate"];
};
const contextOwned = (data: Record<string, unknown>, owner: Registration) => {
  const requester = object(data["requester"]);
  return (
    requester?.["kind"] === "ACTIVE" &&
    requester["installationId"] === owner.installationId &&
    requester["actorId"] === owner.actorId
  );
};
const humanContextOwned = (document: Record<string, unknown> | null, owner: Registration) => {
  const context = object(document?.["context"]);
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
  if (envelope?.["representation"] === "HUMAN") {
    return humanDate(endpoint, envelope, owner);
  }
  if (envelope?.["representation"] !== "DATA") {
    return undefined;
  }
  const data = responseData(response);
  if (data === null || (endpoint === "context" && !contextOwned(data, owner))) {
    return undefined;
  }
  return responseDate(endpoint, data);
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
