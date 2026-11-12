import type { MutationLockChange } from "../../hooks/useWorkspaceMutationLock";
import { useState } from "react";
import type { OperationReceipt } from "../../protocol/types";
import type { CaseListControl } from "../CaseList";
import { useEndpoint } from "../../hooks/useEndpoint";
import { isCurrentCase, type CurrentCase } from "../../api/service";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { Dialog } from "../../ui/Dialog";
import { WorkOperation } from "../work/WorkOperation";

export type ReassignmentSelection = { row: CaseListControl; readId: string };
/** Read one selected parent for disclosure and assignee lookup; freeze the work revision from its list row. */
export const CaseReassignment = ({
  selection,
  disclosureAvailable,
  close,
  recorded,
  onLockChange,
  refused,
}: {
  selection: ReassignmentSelection;
  disclosureAvailable: boolean;
  close: () => void;
  recorded: (receipt: OperationReceipt) => void;
  onLockChange: MutationLockChange;
  refused: () => void;
}) => {
  const p = usePresentation();
  const { row } = selection;
  const query = useEndpoint(
    "case.get",
    { target: { kind: "ID", caseId: row.caseId } },
    disclosureAvailable,
    selection.readId,
  );
  const observed = query.value?.outcome.kind === "SUCCEEDED" ? query.value.outcome.document : null;
  const [parent, setParent] = useState<CurrentCase | null>(null);
  if (parent === null && observed !== null && isCurrentCase(observed)) {
    setParent(observed);
  }
  if (parent === null) {
    return (
      <Dialog
        title={p.text("ui.reassign")}
        closeLabel={p.text("ui.close")}
        isOpen
        isDismissable
        placement="end"
        onOpenChange={(open) => {
          if (!open) {
            close();
          }
        }}
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
  if (row.assignment === null) {
    return null;
  }
  return (
    <WorkOperation
      current={parent}
      subject="WORK"
      kind="UPDATE_WORK"
      targetIdentity={selection.readId}
      binding={{
        kind: "WORK",
        target: { kind: "ID", caseId: row.caseId },
        workRevision: row.assignment.revision,
      }}
      prefill={{
        "input:responsibleActorAction": "SET",
        "field:responsibleActor": row.assignment.responsibleSeed ?? "",
      }}
      disclosureAvailable={disclosureAvailable}
      onClose={close}
      onRecorded={recorded}
      onLockChange={onLockChange}
      onDisclosureRefused={refused}
    />
  );
};
