import { apiEndpoints } from "../../api/service";
import { isOperationAffordance } from "../../operation/affordances";
import { useContentRoute } from "../../hooks/SettingsDetourContext";
import { TaskControls } from "../../ui/TaskControls";
import type { CaseSelector, Affordance } from "@contracts/client";
import { useEffect, useRef } from "react";
import { useEndpoint } from "../../hooks/useEndpoint";
import { isDisclosureRefused } from "../../api/outcomes";
import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import { ButtonControl as Button } from "../../ui/ButtonControl";
import { ScreenView } from "../../ui/ScreenView";
import { reminderActionPresentation, type ReminderActHandler } from "./reminderActions";

// The same reminder may also have controls in the case's pending panel.
const selectedActionPresentation = (action: Affordance) => {
  const display = reminderActionPresentation(action);
  return display.id === undefined ? display : { ...display, id: `selected-${display.id}` };
};

/** Exact reads retain parent checks, current disclosure and supplied action versions. */
export const SelectedReminder = ({
  enabled = true,
  target,
  reminderId,
  observation,
  onAct,
  onDisclosureRefused,
}: {
  enabled?: boolean;
  target: CaseSelector;
  reminderId: string;
  observation?: string;
  onAct: ReminderActHandler;
  onDisclosureRefused: () => void;
}) => {
  const p = usePresentation();
  const read = useEndpoint(apiEndpoints.reminderGet, { target, reminderId }, enabled, observation);
  const notified = useRef(false);
  useEffect(() => {
    if (!enabled) {
      return;
    }
    if (read.result !== null && isDisclosureRefused(read.result)) {
      if (!notified.current) {
        notified.current = true;
        onDisclosureRefused();
      }
    } else if (read.value?.outcome.kind === "SUCCEEDED") {
      notified.current = false;
    }
  }, [enabled, read.result, read.value, onDisclosureRefused]);
  const document = read.value?.outcome.kind === "SUCCEEDED" ? read.value.outcome.document : null;
  const onAction = (action: Affordance, originId?: string) => {
    if (isOperationAffordance(action)) {
      onAct(action, originId);
    }
  };
  if (!enabled) {
    return null;
  }
  return (
    <section className="selected-reminder" aria-busy={read.loading}>
      {read.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {read.message === null ? null : (
        <p role="alert">
          <NoticeView value={read.message} />
        </p>
      )}
      {read.message === null ||
      (read.message.kind === "human" &&
        !["RETRY_SAFE", "READ_CURRENT", "REVIEW_LATEST"].includes(
          read.message.advice.action,
        )) ? null : (
        <Button isDisabled={read.loading} onPress={read.reload}>
          {p.text("ui.tryAgain")}
        </Button>
      )}
      {document === null ? null : (
        <>
          <ScreenView
            view={document.screen}
            timeZone={
              document.basis.kind === "SNAPSHOT" ? document.basis.observation.timeZone : "UTC"
            }
            actionPresentation={selectedActionPresentation}
            onAction={(action, _row, originId) => {
              onAction(action, originId);
            }}
          />
          <TaskControls
            actions={document.actions}
            onAction={onAction}
            actionPresentation={selectedActionPresentation}
          />
        </>
      )}
    </section>
  );
};

export const RoutedReminder = ({
  refresh,
  ...props
}: Omit<Parameters<typeof SelectedReminder>[0], "reminderId"> & { refresh: number }) => {
  const route = useContentRoute();
  return route.screen === "case" && route.reminderId !== undefined ? (
    <SelectedReminder
      key={`${route.reminderId}:${refresh}`}
      {...props}
      reminderId={route.reminderId}
    />
  ) : null;
};
