import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { type Affordance, type HumanDocument, taskDefinitions } from "@contracts/client";
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
const sendTermination = (action: SessionAction, credential: string, signal: AbortSignal) =>
  action.binding.request.command === apiEndpoints.terminateOwnSessions
    ? requestEndpoint(apiEndpoints.terminateOwnSessions, credential, {}, signal)
    : requestEndpoint(
        apiEndpoints.terminatePersonSessions,
        credential,
        { "input:actorId": action.binding.request["input:actorId"] },
        signal,
      );
type Reply = Awaited<ReturnType<typeof sendTermination>>;
type Feedback =
  | { kind: "completed"; self: boolean; document: HumanDocument }
  | { kind: "unknown"; document: HumanDocument | null }
  | { kind: "failed"; notice: Notice };
type Phase =
  | { kind: "idle" }
  | { kind: "confirming" }
  | { kind: "pending" }
  | { kind: "answered"; feedback: Feedback };
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
  disabled?: boolean;
  onDisclosureRefused?: ((notice?: Notice) => void) | undefined;
};
const TerminationAction = ({
  action,
  disabled,
  onDisclosureRefused,
}: {
  action: SessionAction;
  disabled: boolean;
  onDisclosureRefused: Props["onDisclosureRefused"];
}) => {
  const p = usePresentation();
  const session = useSessionContext();
  const cache = useEndpointCache();
  const credential = session.state.kind === "authenticated" ? session.state.token : "";
  const key = `${session.state.epoch}:${credential}:${JSON.stringify(action.binding)}`;
  const [state, setState] = useState<{ key: string; phase: Phase }>({
    key,
    phase: { kind: "idle" },
  });
  const phase: Phase = state.key === key ? state.phase : { kind: "idle" };
  const pending = useRef<{ key: string; controller: AbortController } | null>(null);
  const dispatching = useRef(false);
  const owner = useRef(key);
  useLayoutEffect(() => {
    owner.current = key;
  }, [key]);
  useEffect(
    () => () => {
      const request = pending.current;
      if (request?.key === key) {
        request.controller.abort();
      }
    },
    [key],
  );
  const definition = taskDefinitions.find((value) => value.id === action.task)!;
  const label = renderCatalogKey(p, action.labelId);
  const execute = async () => {
    if (dispatching.current || credential === "") {
      return;
    }
    dispatching.current = true;
    const controller = new AbortController();
    pending.current = { key, controller };
    setState({ key, phase: { kind: "pending" } });
    const result = await sendTermination(action, credential, controller.signal);
    dispatching.current = false;
    if (controller.signal.aborted || owner.current !== key) {
      return;
    }
    if (isDisclosureRefused(result)) {
      cache?.invalidate();
      onDisclosureRefused?.(resultNotice(result));
      if (result.kind === "hostFailure") {
        void session.refresh();
      }
    }
    const feedback = feedbackFor(result);
    setState({ key, phase: { kind: "answered", feedback } });
    if (feedback.kind === "completed") {
      cache?.invalidate();
      if (feedback.self) {
        session.endedBrowserSessions(session.state.epoch);
      }
    }
  };
  const busy = phase.kind === "pending";
  const feedback = phase.kind === "answered" ? phase.feedback : null;
  return (
    <>
      <Button
        variant="secondary"
        isDisabled={disabled || busy || action.blocked !== null}
        onPress={() => {
          setState({ key, phase: { kind: "confirming" } });
        }}
      >
        {label}
      </Button>
      {feedback?.kind === "completed" ? <FeedbackView feedback={feedback} /> : null}
      <Dialog
        title={label}
        closeLabel={p.text("ui.close")}
        isOpen={dialogOpen(phase)}
        isDismissable={!busy}
        onOpenChange={(open) => {
          if (!open && !busy) {
            setState({ key, phase: { kind: "idle" } });
          }
        }}
        footer={
          <Button
            isDisabled={phase.kind !== "confirming"}
            onPress={() => {
              guardedDeparture(() => {
                void execute();
              });
            }}
          >
            {label}
          </Button>
        }
      >
        <p>{renderCatalogKey(p, definition.meaningId)}</p>
        {feedback === null ? null : <FeedbackView feedback={feedback} />}
      </Dialog>
    </>
  );
};
export const BrowserSessionCommands = ({
  actions,
  disabled = false,
  onDisclosureRefused,
}: Props) => (
  <>
    {actions.filter(sessionAction).map((action) => (
      <TerminationAction
        key={action.task}
        action={action}
        disabled={disabled}
        onDisclosureRefused={onDisclosureRefused}
      />
    ))}
  </>
);
export const OwnBrowserSessions = () => {
  const supplied = useRequesterDocument();
  const context = useEndpoint(apiEndpoints.context, {}, supplied === null);
  const outcome = context.value?.outcome;
  const document = supplied ?? (outcome?.kind === "SUCCEEDED" ? outcome.document : null);
  return document === null ? null : <BrowserSessionCommands actions={document.actions} />;
};
