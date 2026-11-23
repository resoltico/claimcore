import { CsvExportButton } from "../../components/CsvExportButton";
import { apiEndpoints, type CurrentCase, isCurrentCase } from "../../api/service";
import type { BoundOperationAffordance, OperationAffordance } from "../../operation/affordances";

import { useAcceptedOperation } from "../../hooks/useAcceptedOperation";
import { ReceiptFeedback, receiptOutcomeMessage } from "../operation/ReceiptFeedback";
import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useRef, useState } from "react";
import { useSessionContext } from "../../session/SessionContext";
import type { ReminderListRequest } from "@contracts/client";
import type { OperationReceipt } from "../../protocol/types";

import { operationTask } from "../../protocol/taskCatalog";
import { useEndpoint } from "../../hooks/useEndpoint";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";

import { renderCatalogKey } from "../../presentation/messages";
import { Dialog } from "../../ui/Dialog";
import { ReminderList } from "./ReminderList";
import { WorkOperation } from "./WorkOperation";

type Selection = {
  action: BoundOperationAffordance<"REMINDER">;
  anchor: string | null;
  readId: string;
  implicit: boolean;
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
    apiEndpoints.caseGet,
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
      implicit={selection.implicit}
      kind={task.id}
      targetIdentity={"reminderId" in binding ? binding.reminderId : action.task}
      {...(selection.anchor === null ? {} : { completionAnchor: selection.anchor })}
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
  filter: Omit<ReminderListRequest, "cursor" | "limit">;
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
      <CsvExportButton
        selection={{ query: apiEndpoints.reminderList, selection: filter }}
        available={disclosureAvailable && !masked}
      />
      {done.operationId === null || !disclosureAvailable || masked ? null : (
        <ReceiptFeedback
          key={done.operationId}
          operationId={done.operationId}
          messageId={done.messageId}
          dismiss={done.clear}
        />
      )}
      {disclosureAvailable && !masked ? (
        <ReminderList
          key={refresh}
          filter={filter}
          onAct={(action, originId, implicit = false) => {
            focus.current =
              originId ??
              (document.activeElement instanceof HTMLElement ? document.activeElement.id : null);
            done.clear();
            if (isReminderAction(action)) {
              setSelection({
                action,
                implicit,
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
          recorded={(receipt, operationId) => {
            done.accept(operationId, receiptOutcomeMessage(receipt));
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
