import { useEffect, useLayoutEffect, useRef, useState } from "react";
import {
  type Affordance,
  type HumanDocument,
  type SessionTerminationScope,
  taskDefinitions,
} from "@contracts/client";
import { apiEndpoints, requestEndpoint, resultNotice } from "../../api/service";
import { isDisclosureRefused } from "../../api/outcomes";
import { useRequesterDocument } from "../../hooks/useRequesterContext";
import { useEndpoint } from "../../hooks/useEndpoint";
import { useEndpointCache } from "../../hooks/EndpointProvider";
import { useSessionContext } from "../../session/SessionContext";
import { usePresentation } from "../../presentation/context";
import { renderCatalogKey } from "../../presentation/messages";
import { NoticeView } from "../../presentation/Message";
import type { Notice } from "../../protocol/notices";
import { guardedDeparture } from "../../routing/navigation";
import {
  clearTermination,
  couldReclaimTermination,
  expiredAt,
  recoveryFor,
  retainTermination,
  retiredAt,
  terminationScope,
  TerminationCapacityError,
  type TerminationAttempt,
} from "./terminationRecovery";
import { operationId } from "../../operation/operationId";
import { Button } from "../../ui/Button";
import { Dialog } from "../../ui/Dialog";
import { ScreenView } from "../../ui/ScreenView";

type SessionAction = Extract<
  Affordance,
  {
    readonly task:
      typeof apiEndpoints.terminateOwnSessions | typeof apiEndpoints.terminatePersonSessions;
  }
>;
const sessionAction = (action: Affordance): action is SessionAction =>
  action.task === apiEndpoints.terminateOwnSessions ||
  action.task === apiEndpoints.terminatePersonSessions;
const uncertain: Notice = {
  kind: "human",
  advice: {
    message: { messageId: "label.sessionTerminationUnknown", arguments: [] },
    action: "STOP_AND_INVESTIGATE",
  },
};
const sendTermination = (
  action: SessionAction,
  credential: string,
  attempt: TerminationAttempt,
  mode: "cut" | "settle" | "retirement",
  signal: AbortSignal,
) => {
  const input = {
    "input:operationId": attempt.id,
    ...(mode === "settle" ? { "input:settleOnly": true } : {}),
    ...(mode === "retirement" ? { "input:confirmRetirement": true } : {}),
  };
  return action.binding.request.command === apiEndpoints.terminateOwnSessions
    ? requestEndpoint(
        apiEndpoints.terminateOwnSessions,
        credential,
        input,
        signal,
        attempt.installationId,
      )
    : requestEndpoint(
        apiEndpoints.terminatePersonSessions,
        credential,
        { ...input, "input:actorId": action.binding.request["input:actorId"] },
        signal,
        attempt.installationId,
      );
};
type Reply = Awaited<ReturnType<typeof sendTermination>>;
type Feedback =
  | { kind: "completed"; self: boolean; document: HumanDocument }
  | { kind: "unknown"; document: HumanDocument | null }
  | { kind: "failed"; notice: Notice };
type Phase =
  | { kind: "idle" }
  | {
      kind: "confirming";
      attempt: TerminationAttempt;
      resume: boolean;
      problem?: PreparationProblem;
    }
  | { kind: "pending" }
  | { kind: "answered"; feedback: Feedback; attempt: TerminationAttempt };
const notStarted = (result: Reply) =>
  (result.kind === "hostFailure" && result.failure.phase === "NOT_STARTED") ||
  (result.kind === "deliveryFailure" && result.dispatched === false);
const feedbackFor = (result: Reply): Feedback => {
  if (result.kind !== "outcome") {
    return notStarted(result)
      ? { kind: "failed", notice: resultNotice(result) }
      : { kind: "unknown", document: null };
  }
  const { outcome } = result.value;
  switch (outcome.kind) {
    case "SUCCEEDED":
      return { kind: "completed", self: outcome.self, document: outcome.document };
    case "RETIREMENT_CONFIRMED":
      return { kind: "unknown", document: null };
    case "OUTCOME_UNKNOWN":
      return { kind: "unknown", document: outcome.document };
    case "REJECTED":
    case "FAILED":
      return { kind: "failed", notice: resultNotice(result) };
  }
};
const dialogOpen = (phase: Phase) => {
  switch (phase.kind) {
    case "idle":
      return false;
    case "answered":
      return phase.feedback.kind !== "completed";
    case "confirming":
    case "pending":
      return true;
  }
};
const FeedbackView = ({ feedback }: { feedback: Feedback }) => {
  if (feedback.kind === "failed") {
    return (
      <p role="alert">
        <NoticeView value={feedback.notice} />
      </p>
    );
  }
  if (feedback.document !== null) {
    return <ScreenView view={feedback.document.screen} timeZone="UTC" />;
  }
  return (
    <p role="alert">
      <NoticeView value={uncertain} />
    </p>
  );
};
type Props = {
  actions: ReadonlyArray<Affordance>;
  scope?: SessionTerminationScope | undefined;
  disabled?: boolean;
  onDisclosureRefused?: ((notice?: Notice) => void) | undefined;
};
type ActionProps = {
  action: SessionAction;
  scope: SessionTerminationScope;
  disabled: boolean;
  onDisclosureRefused: Props["onDisclosureRefused"];
};

type PreparationProblem = "unavailable" | "mismatch" | "expired" | "outsideWindow";
type Observation =
  { kind: "observed"; scope: SessionTerminationScope } | { kind: "unavailable" | "mismatch" };

const targetObserved = async (
  attempt: TerminationAttempt,
  credential: string,
  signal: AbortSignal,
): Promise<"match" | "mismatch" | "unavailable"> => {
  if (attempt.target === "self") {
    return "match";
  }
  const result = await requestEndpoint(
    apiEndpoints.peopleGrants,
    credential,
    { actorId: attempt.target, limit: 1 },
    signal,
    attempt.installationId,
  );
  if (result.kind !== "outcome" || result.value.outcome.kind === "FAILED") {
    return "unavailable";
  }
  if (result.value.outcome.kind !== "SUCCEEDED") {
    return "mismatch";
  }
  const { document } = result.value.outcome;
  const matches =
    document.subject.kind === "PERSON" &&
    document.subject.actorId === attempt.target &&
    document.actions.some(
      (action) =>
        action.task === apiEndpoints.terminatePersonSessions &&
        action.blocked === null &&
        action.binding.request["input:actorId"] === attempt.target,
    );
  return matches ? "match" : "mismatch";
};

const observeTerminationScope = async (
  attempt: TerminationAttempt,
  credential: string,
  signal: AbortSignal,
): Promise<Observation> => {
  const result = await requestEndpoint(
    apiEndpoints.context,
    credential,
    {},
    signal,
    attempt.installationId,
  );
  if (result.kind !== "outcome" || result.value.outcome.kind !== "SUCCEEDED") {
    return {
      kind:
        result.kind === "hostFailure" && result.failure.code === "INSTALLATION_MISMATCH"
          ? "mismatch"
          : "unavailable",
    };
  }
  const { document } = result.value.outcome;
  const scope =
    document.subject.kind === "REQUESTER" ? document.subject.terminationScope : undefined;
  if (
    scope?.installationId !== attempt.installationId ||
    scope.requesterKey !== attempt.requesterKey
  ) {
    return { kind: "mismatch" };
  }
  const target = await targetObserved(attempt, credential, signal);
  if (target !== "match") {
    return { kind: target };
  }
  return { kind: "observed", scope };
};

const publish = (attempt: TerminationAttempt, cutoff: string | null) => {
  try {
    retainTermination(attempt, cutoff);
    return "ready";
  } catch (error) {
    return error instanceof TerminationCapacityError ? "capacity" : "unavailable";
  }
};

const resumeStatus = (
  attempt: TerminationAttempt,
  scope: SessionTerminationScope,
): PreparationProblem | "ready" => {
  const stored = recoveryFor(attempt, scope.retirementCutoff);
  if (stored.kind === "unavailable") {
    return "unavailable";
  }
  if (!stored.attempts.some((saved) => saved.id === attempt.id)) {
    return "mismatch";
  }
  if (retiredAt(attempt.id, scope.retirementCutoff)) {
    return "expired";
  }
  return expiredAt(attempt.id, scope.observedAt) ? "outsideWindow" : "ready";
};

const reclaimTermination = async (
  attempt: TerminationAttempt,
  scope: SessionTerminationScope,
  observe: () => Promise<Observation>,
  confirmRetirement: () => Promise<boolean>,
) => {
  if (!couldReclaimTermination(attempt, scope.observedAt) || !(await confirmRetirement())) {
    return { status: "unavailable", scope } as const;
  }
  const committed = await observe();
  if (committed.kind !== "observed") {
    return { status: committed.kind, scope } as const;
  }
  const published = publish(attempt, committed.scope.retirementCutoff);
  return {
    status: published === "capacity" ? "unavailable" : published,
    scope: committed.scope,
  } as const;
};

const prepareTermination = async (
  attempt: TerminationAttempt,
  resume: boolean,
  scope: SessionTerminationScope,
  observe: () => Promise<Observation>,
  confirmRetirement: () => Promise<boolean>,
) => {
  if (!resume) {
    const prepared = publish(attempt, scope.retirementCutoff);
    if (prepared !== "capacity") {
      return { status: prepared, scope } as const;
    }
  }
  const observation = await observe();
  if (observation.kind !== "observed") {
    return { status: observation.kind, scope } as const;
  }
  const confirmed = observation.scope;
  if (resume) {
    return { status: resumeStatus(attempt, confirmed), scope: confirmed } as const;
  }

  const prepared = publish(attempt, confirmed.retirementCutoff);
  return prepared === "capacity"
    ? reclaimTermination(attempt, confirmed, observe, confirmRetirement)
    : ({ status: prepared, scope: confirmed } as const);
};

const keepsAttempt = (feedback: Feedback, resume: boolean) =>
  feedback.kind === "unknown" || (resume && feedback.kind === "failed");

const useTerminationRequest = (
  action: SessionAction,
  key: string,
  onDisclosureRefused: Props["onDisclosureRefused"],
) => {
  const session = useSessionContext();
  const cache = useEndpointCache();
  const credential = session.state.kind === "authenticated" ? session.state.token : "";
  const inFlight = useRef<{ key: string; controller: AbortController } | null>(null);
  const owner = useRef(key);
  useLayoutEffect(() => {
    owner.current = key;
  }, [key]);
  useEffect(
    () => () => {
      if (inFlight.current?.key === key) {
        inFlight.current.controller.abort();
      }
    },
    [key],
  );

  const observeReply = (result: Reply, feedback: Feedback, resume: boolean) => {
    if (isDisclosureRefused(result)) {
      cache?.invalidate();
      onDisclosureRefused?.(resultNotice(result));
      if (result.kind === "hostFailure") {
        void session.refresh();
      }
    }
    if (feedback.kind === "completed" && !resume) {
      cache?.invalidate();
      if (feedback.self) {
        session.refreshAfterTermination(session.state.epoch);
      }
    }
  };

  return async (attempt: TerminationAttempt, resume: boolean, scope: SessionTerminationScope) => {
    if (inFlight.current !== null || credential === "") {
      return null;
    }
    const controller = new AbortController();
    inFlight.current = { key, controller };
    const current = () => !controller.signal.aborted && owner.current === key;
    const observe = async (): Promise<Observation> => {
      const observation = await observeTerminationScope(attempt, credential, controller.signal);
      return current() ? observation : { kind: "mismatch" };
    };
    // Retirement uses the same action authority and ordering without issuing a cut.
    const confirmRetirement = async () => {
      const result = await sendTermination(
        action,
        credential,
        attempt,
        "retirement",
        controller.signal,
      );
      return (
        current() &&
        result.kind === "outcome" &&
        result.value.outcome.kind === "RETIREMENT_CONFIRMED"
      );
    };
    const prepared = await prepareTermination(attempt, resume, scope, observe, confirmRetirement);
    if (!current()) {
      inFlight.current = null;
      return null;
    }
    if (prepared.status !== "ready") {
      inFlight.current = null;
      return {
        kind: "blocked" as const,
        scope: prepared.scope,
        problem: prepared.status,
      };
    }
    const result = await sendTermination(
      action,
      credential,
      attempt,
      resume ? "settle" : "cut",
      controller.signal,
    );
    inFlight.current = null;
    if (!current()) {
      return null;
    }
    const feedback = feedbackFor(result);
    return {
      kind: "replied" as const,
      scope: prepared.scope,
      feedback,
      observe: () => {
        observeReply(result, feedback, resume);
      },
    };
  };
};

const useTermination = ({ action, scope, disabled, onDisclosureRefused }: ActionProps) => {
  const { state: session } = useSessionContext();
  const binding = action.binding.request;
  const target =
    binding.command === apiEndpoints.terminateOwnSessions ? "self" : binding["input:actorId"];
  const lane = terminationScope(scope, target);
  const key = `${session.epoch}:${JSON.stringify(lane)}`;
  const [state, setState] = useState<{ key: string; phase: Phase }>({
    key,
    phase: { kind: "idle" },
  });
  const [storageFailed, setStorageFailed] = useState(false);
  const phase: Phase = state.key === key ? state.phase : { kind: "idle" };
  const [observation, setObservation] = useState({ key, base: scope.observedAt, scope });
  const currentScope =
    observation.key === key && observation.base === scope.observedAt ? observation.scope : scope;
  const recovery = recoveryFor(lane, currentScope.retirementCutoff);
  const recovered = recovery.kind === "available" ? recovery.attempts : [];
  const other = recovery.kind === "available" && recovery.other;
  const damaged = recovery.kind === "available" && recovery.damaged;
  const expired = (attempt: TerminationAttempt) =>
    retiredAt(attempt.id, currentScope.retirementCutoff);
  const outsideWindow = (attempt: TerminationAttempt) =>
    !expired(attempt) && expiredAt(attempt.id, currentScope.observedAt);
  const busy = phase.kind === "pending";
  const ready = !disabled && !busy && action.blocked === null && recovery.kind === "available";
  const send = useTerminationRequest(action, key, onDisclosureRefused);
  const change = (next: Phase) => {
    setState({ key, phase: next });
  };
  const close = () => {
    change({ kind: "idle" });
  };
  const open = (attempt?: TerminationAttempt) => {
    change({
      kind: "confirming",
      attempt: attempt ?? { ...lane, id: operationId() },
      resume: attempt !== undefined,
    });
  };
  const forget = (attempt: TerminationAttempt) => {
    if (expired(attempt)) {
      try {
        clearTermination(attempt);
        close();
      } catch {
        setStorageFailed(true);
      }
    }
  };
  const confirm = async (attempt: TerminationAttempt, resume: boolean) => {
    if (!ready) {
      return;
    }
    change({ kind: "pending" });
    const result = await send(attempt, resume, currentScope);
    if (result === null) {
      return;
    }
    setObservation({ key, base: scope.observedAt, scope: result.scope });
    if (result.kind === "blocked") {
      change({ kind: "confirming", attempt, resume, problem: result.problem });
      return;
    }
    if (!keepsAttempt(result.feedback, resume)) {
      try {
        clearTermination(attempt);
      } catch {
        setStorageFailed(true);
      }
    }
    change({ kind: "answered", feedback: result.feedback, attempt });
    result.observe();
  };
  return {
    recovered,
    other,
    expired,
    outsideWindow,
    damaged,
    busy,
    ready,
    phase,
    open,
    close,
    confirm,
    forget,
    unavailable: storageFailed || recovery.kind === "unavailable",
  };
};

type TerminationModel = ReturnType<typeof useTermination>;

const SavedTermination = ({
  model,
  attempt,
}: {
  model: TerminationModel;
  attempt: TerminationAttempt;
}) => {
  const p = usePresentation();
  if (model.expired(attempt)) {
    return (
      <>
        <p role="status">{p.text("ui.terminationRecoveryExpired")}</p>
        <Button
          variant="secondary"
          isDisabled={model.busy}
          onPress={() => {
            model.forget(attempt);
          }}
        >
          {p.text("ui.forgetTermination")}
        </Button>
      </>
    );
  }
  return (
    <>
      {model.outsideWindow(attempt) ? (
        <p role="status">{p.text("ui.terminationOutsideWindow")}</p>
      ) : null}
      <Button
        variant="secondary"
        isDisabled={!model.ready}
        onPress={() => {
          model.open(attempt);
        }}
      >
        {p.text("ui.resumeTermination")}
      </Button>
    </>
  );
};

const SavedTerminations = ({ model }: { model: TerminationModel }) => {
  const [first] = model.recovered;
  if (first === undefined) {
    return null;
  }
  if (model.recovered.length === 1) {
    return <SavedTermination model={model} attempt={first} />;
  }
  return (
    <ul>
      {model.recovered.map((attempt) => (
        <li key={attempt.id}>
          <code>{attempt.id}</code>
          <SavedTermination model={model} attempt={attempt} />
        </li>
      ))}
    </ul>
  );
};

const TerminationChoices = ({ model, label }: { model: TerminationModel; label: string }) => {
  const p = usePresentation();
  const { recovered, other, damaged, ready, unavailable, open } = model;
  const startsNew = recovered.length > 0 || other || damaged;
  return (
    <>
      <SavedTerminations model={model} />
      {other && recovered.length === 0 ? (
        <p role="status">{p.text("ui.terminationScopeMismatch")}</p>
      ) : null}
      {damaged ? <p role="status">{p.text("ui.terminationRecoveryDamaged")}</p> : null}
      {unavailable ? <p role="alert">{p.text("ui.recoveryListingUnavailable")}</p> : null}
      <Button
        variant="secondary"
        isDisabled={!ready}
        onPress={() => {
          open();
        }}
      >
        {startsNew ? p.text("ui.newTermination") : label}
      </Button>
    </>
  );
};

const confirmationFor = (phase: Phase) => {
  switch (phase.kind) {
    case "confirming":
      return phase;
    case "answered":
      return phase.feedback.kind === "unknown" ? { attempt: phase.attempt, resume: true } : null;
    default:
      return null;
  }
};

const preparationMessage = (phase: Phase) => {
  if (phase.kind !== "confirming") {
    return undefined;
  }
  switch (phase.problem) {
    case "expired":
      return "ui.terminationRecoveryExpired";
    case "outsideWindow":
      return "ui.terminationOutsideWindow";
    case "mismatch":
      return "ui.terminationScopeMismatch";
    case "unavailable":
      return "ui.recoveryListingUnavailable";
    default:
      return undefined;
  }
};

const PreparationNotice = ({ phase }: { phase: Phase }) => {
  const p = usePresentation();
  const problem = preparationMessage(phase);
  return problem === undefined ? null : <p role="alert">{p.text(problem)}</p>;
};

const TerminationDialog = ({
  model,
  label,
  meaningId,
}: {
  model: TerminationModel;
  label: string;
  meaningId: string;
}) => {
  const p = usePresentation();
  const { phase, busy, expired, ready, recovered, close, confirm } = model;
  const feedback = phase.kind === "answered" ? phase.feedback : null;
  const confirmation = confirmationFor(phase);
  const settlement = confirmation?.resume === true;
  const enabled = ready && confirmation !== null && !(settlement && expired(confirmation.attempt));
  const commit = () => {
    if (confirmation !== null) {
      guardedDeparture(() => {
        void confirm(confirmation.attempt, confirmation.resume);
      });
    }
  };
  const onOpenChange = (open: boolean) => {
    if (!open && !busy) {
      close();
    }
  };

  return (
    <Dialog
      title={label}
      closeLabel={p.text("ui.close")}
      isOpen={dialogOpen(phase)}
      isDismissable={!busy}
      onOpenChange={onOpenChange}
      footer={
        <Button isDisabled={!enabled} onPress={commit}>
          {settlement ? p.text("ui.resumeTermination") : label}
        </Button>
      }
    >
      <p>
        {settlement ? p.text("ui.resumeTerminationDescription") : renderCatalogKey(p, meaningId)}
      </p>
      {confirmation?.resume === false && recovered.length > 0 ? (
        <p>{p.text("ui.newTerminationDescription")}</p>
      ) : null}
      <PreparationNotice phase={phase} />
      {feedback === null ? null : <FeedbackView feedback={feedback} />}
    </Dialog>
  );
};

const TerminationAction = (props: ActionProps) => {
  const model = useTermination(props);
  const p = usePresentation();
  const definition = taskDefinitions.find((value) => value.id === props.action.task)!;
  const label = renderCatalogKey(p, props.action.labelId);
  return (
    <>
      <TerminationChoices model={model} label={label} />
      {model.phase.kind === "answered" && model.phase.feedback.kind === "completed" ? (
        <FeedbackView feedback={model.phase.feedback} />
      ) : null}
      <TerminationDialog model={model} label={label} meaningId={definition.meaningId} />
    </>
  );
};

export const BrowserSessionCommands = ({
  actions,
  scope,
  disabled = false,
  onDisclosureRefused,
}: Props) => {
  const requester = useRequesterDocument();
  const admitted = scope ?? requester?.subject.terminationScope;
  return admitted === undefined ? null : (
    <>
      {actions.filter(sessionAction).map((action) => (
        <TerminationAction
          key={action.task}
          action={action}
          scope={admitted}
          disabled={disabled}
          onDisclosureRefused={onDisclosureRefused}
        />
      ))}
    </>
  );
};
export const OwnBrowserSessions = () => {
  const supplied = useRequesterDocument();
  const context = useEndpoint(apiEndpoints.context, {}, supplied === null);
  const outcome = context.value?.outcome;
  const document = supplied ?? (outcome?.kind === "SUCCEEDED" ? outcome.document : null);
  return document === null ? null : (
    <BrowserSessionCommands
      actions={document.actions}
      scope={document.subject.kind === "REQUESTER" ? document.subject.terminationScope : undefined}
    />
  );
};
