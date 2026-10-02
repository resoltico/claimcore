import { createRecoveryAdmission, createInspectionControl } from "../hooks/recoveryControl";
import type { Notice } from "../api/notices";
import { useCallback, useEffect, useMemo, useState } from "react";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
import { Button } from "react-aria-components/Button";
import type { RecoveryTarget } from "../domain/operationState";
import type {
  RecoveryListItem,
  RecoveryPage as RecoveryPageResult,
  WebV3Response,
} from "../api/v3";
import { v3 } from "../api/v3";
import { useRetryablePage } from "../hooks/useRead";
import { RecoveryPage } from "./recovery/RecoveryPage";
import {
  page,
  recoveryActions,
  type ConfirmState,
  type ImportState,
  type Inspection,
  type RecoveryViewKind,
} from "./recovery/RecoveryState";

type RecoveryViewProps = {
  token: string;
  target?: RecoveryTarget | null;
  onRecovery: (target: RecoveryTarget) => void;
  onMutationLockChange: (locked: boolean) => void;
};
type Listing = Parameters<typeof recoveryActions>[1];

const whenRecoveryIdle =
  <Args extends unknown[]>(idle: () => boolean, action: (...args: Args) => void) =>
  (...args: Args): void => {
    if (idle()) {
      action(...args);
    }
  };

const guardActions = (
  actions: ReturnType<typeof recoveryActions>,
  idle: () => boolean,
  admission: ReturnType<typeof createRecoveryAdmission>,
  onLock: RecoveryViewProps["onMutationLockChange"],
) => ({
  ...actions,
  inspectId: whenRecoveryIdle(idle, actions.inspectId),
  inspect: whenRecoveryIdle(idle, actions.inspect),
  loadAttempts: whenRecoveryIdle(idle, actions.loadAttempts),
  choose: whenRecoveryIdle(idle, actions.choose),
  exportItem: whenRecoveryIdle(idle, actions.exportItem),
  preview: (file: File) => admission.read(() => actions.preview(file)),
  act: () => admission.mutate(actions.act, onLock),
  retain: () => admission.mutate(actions.retain, onLock),
});

const useRecoveryControl = () => {
  const admission = useMemo(() => createRecoveryAdmission(), []);
  const inspection = useMemo(() => createInspectionControl(), []);
  useEffect(
    () => () => {
      inspection.cancel();
    },
    [inspection],
  );
  return { admission, inspection };
};

const useRecoveryDialogs = (props: RecoveryViewProps, listing: Listing) => {
  const { token, onRecovery, onMutationLockChange } = props;
  const [selected, setSelected] = useState<Inspection | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [importing, setImporting] = useState<ImportState | null>(null);
  const [message, setMessage] = useState<Notice | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const { admission, inspection } = useRecoveryControl();
  const actions = recoveryActions(token, listing, {
    inspection,
    onRecovery,
    setSelected,
    confirm,
    setConfirm,
    importing,
    setImporting,
    setMessage,
    setBusy,
  });
  const guardedActions = guardActions(
    actions,
    () => admission.idle() && inspection.idle(),
    admission,
    onMutationLockChange,
  );
  const closeDetails = (): void => {
    inspection.cancel();
    setBusy(null);
    setSelected(null);
  };
  return {
    selected,
    message,
    busy,
    confirm,
    importing,
    actions: guardedActions,
    closeDetails,
    closeConfirm: () => {
      setConfirm(null);
    },
    closeImport: () => {
      setImporting(null);
    },
  };
};

export const RecoveryView = (props: RecoveryViewProps) => {
  const { token, target = null } = props;
  const p = usePresentation();
  const [view, setView] = useState<RecoveryViewKind>("PENDING");
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) =>
      v3.recoveryList(view, cursor, 50, token, signal),
    [token, view],
  );
  const recoveryPage = useRetryablePage<
    WebV3Response<"recovery.list">,
    RecoveryListItem,
    RecoveryPageResult
  >(request, page);
  const listing = { ...recoveryPage, view, setView };
  const dialogs = useRecoveryDialogs(props, listing);
  return (
    <>
      {target === null ? null : (
        <div role={target.message.kind === "recovery" ? "alert" : "status"}>
          <NoticeView value={target.message} />
          <p>
            {p.text("ui.operationDigest", {
              operationId: target.operationId,
              digest: target.requestSha256 ?? p.text("ui.unavailable"),
            })}
          </p>
          <Button
            onPress={() => {
              dialogs.actions.inspectId(target.operationId);
            }}
            isDisabled={dialogs.busy !== null}
          >
            {p.text("ui.inspect")}
          </Button>
        </div>
      )}
      <RecoveryPage
        listing={listing}
        {...dialogs}
        message={dialogs.message === target?.message ? null : dialogs.message}
      />
    </>
  );
};
