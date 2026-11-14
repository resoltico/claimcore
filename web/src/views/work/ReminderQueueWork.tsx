import type { OperationAffordance } from "../../operation/affordances";
import { useAcceptedOperation } from "../../hooks/useAcceptedOperation";
import { ObservedReceipt } from "../operation/ObservedReceipt";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useRef, useState } from "react";
import { useSessionContext } from "../../session/SessionContext";
import type { ReminderListRequest } from "@contracts/client";
import type { OperationReceipt } from "../../protocol/types";

import { operationTask } from "../../protocol/taskCatalog";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { type CurrentCase, isCurrentCase } from "../../api/service";

import { renderCatalogKey } from "../../presentation/messages";
import { Dialog } from "../../ui/Dialog";
import { ReminderList } from "./ReminderList";
import { WorkOperation } from "./WorkOperation";

type Selection = {
  action: Extract<OperationAffordance, { binding: { kind: "REMINDER" } }>;
  anchor: string | null;
  readId: string;
};
const isReminderAction = (action: OperationAffordance): action is Selection["action"] =>
  action.binding.kind === "REMINDER";
const SelectedReminder = ({
  selection,
  close,
  recorded,
  onLockChange,
  disclosureAvailable,
  refused,
}: {
  selection: Selection;
  close: () => void;
  recorded: (receipt: OperationReceipt, operationId: string) => void;
  onLockChange: MutationLockChange;
  disclosureAvailable: boolean;
  refused: () => void;
}) => {
  const p = usePresentation();
  const { action } = selection;
  const kind = action.task;
  const { binding } = action;
  const query = useEndpoint(
    "case.get",
    { target: binding.target },
    disclosureAvailable,
    selection.readId,
  );
  const current = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
  const [parent, setParent] = useState<CurrentCase | null>(null);
  if (parent === null && current !== null && isCurrentCase(current)) {
    setParent(current);
  }
  const task = operationTask(kind);
  const title = renderCatalogKey(p, task.labelId);
  if (parent === null) {
    return (
      <Dialog
        title={title}
        closeLabel={p.text("ui.close")}
        isOpen
        isDismissable
        onOpenChange={(open) => {
          if (!open) {
            close();
          }
        }}
        placement="end"
      >
        {query.loading ? (
          <p role="status">{p.text("ui.loadingCurrentCase")}</p>
        ) : (
          <p role="alert">
            <NoticeView value={query.message ?? { kind: "local", reason: "incomplete" }} />
          </p>
        )}
      </Dialog>
    );
  }
  return (
    <WorkOperation
      current={parent}
      subject="REMINDER"
      kind={task.act}
      targetIdentity={"reminderId" in binding ? binding.reminderId : action.task}
      {...(kind !== "reminder.complete" || selection.anchor === null
        ? {}
        : { completionAnchor: selection.anchor })}
      affordance={action}
      binding={binding}
      disclosureAvailable={disclosureAvailable}
      onClose={close}
      onRecorded={recorded}
      onLockChange={onLockChange}
      onDisclosureRefused={refused}
    />
  );
};
/** A queue action owns one selected reminder and one explicit parent read, never per-row reads. */
type QueueProps = {
  filter: Omit<ReminderListRequest, "target" | "cursor" | "limit">;
  onLockChange: MutationLockChange;
  disclosureAvailable: boolean;
};
const AvailableReminderQueueWork = ({ filter, onLockChange, disclosureAvailable }: QueueProps) => {
  const p = usePresentation();
  const [selection, setSelection] = useState<Selection | null>(null);
  const [refresh, setRefresh] = useState(0);
  const done = useAcceptedOperation();
  const [masked, setMasked] = useState(false);
  const focus = useRef<string | null>(null);
  const close = () => {
    setSelection(null);
    requestAnimationFrame(() => {
      if (focus.current !== null) {
        document.getElementById(focus.current)?.focus();
      }
    });
  };
  return (
    <>
      {done.operationId === null || !disclosureAvailable || masked ? null : (
        <ObservedReceipt key={done.operationId} operationId={done.operationId} />
      )}
      {disclosureAvailable && !masked ? (
        <ReminderList
          key={refresh}
          filter={filter}
          onAct={(action, originId?: string) => {
            focus.current =
              originId ??
              (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
            done.clear();
            if (isReminderAction(action)) {
              setSelection({
                action,
                anchor: focus.current,
                readId: crypto.randomUUID(),
              });
            }
          }}
        />
      ) : null}
      {masked && selection === null ? (
        <button
          type="button"
          onClick={() => {
            setMasked(false);
            setRefresh((value) => value + 1);
          }}
        >
          {p.text("ui.refreshWork")}
        </button>
      ) : null}
      {selection === null ? null : (
        <SelectedReminder
          key={selection.readId}
          selection={selection}
          close={close}
          onLockChange={onLockChange}
          disclosureAvailable={disclosureAvailable}
          refused={() => {
            setMasked(true);
          }}
          recorded={(_receipt, operationId) => {
            done.accept(operationId);
            setMasked(false);
            setRefresh((value) => value + 1);
            close();
          }}
        />
      )}
    </>
  );
};

/** The selected reminder and accepted receipt belong to one authenticated session. */
export const ReminderQueueWork = (props: QueueProps) => {
  const { state } = useSessionContext();
  return state.kind === "authenticated" ? (
    <AvailableReminderQueueWork key={`${state.epoch}:${state.token}`} {...props} />
  ) : null;
};
