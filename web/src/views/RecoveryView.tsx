import { useCallback, useRef, useState } from "react";
import type { PreparationSummary } from "../api/v2";
import { v2 } from "../api/v2";
import { useRetryablePage } from "../hooks/useV2Read";
import { RecoveryPage } from "./recovery/RecoveryPage";
import {
  page,
  onceWhilePending,
  recoveryActions,
  type ConfirmState,
  type ImportState,
  type Inspection,
} from "./recovery/RecoveryState";

type RecoveryViewProps = { token: string };
type Listing = Parameters<typeof recoveryActions>[1];

const useRecoveryDialogs = (token: string, listing: Listing) => {
  const [selected, setSelected] = useState<Inspection | null>(null);
  const [selectedSummary, setSelectedSummary] = useState<PreparationSummary | null>(null);
  const [confirm, setConfirm] = useState<ConfirmState | null>(null);
  const [importing, setImporting] = useState<ImportState | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const actionInFlight = useRef(false);
  const actions = recoveryActions(token, listing, {
    selected,
    setSelected,
    selectedSummary,
    setSelectedSummary,
    confirm,
    setConfirm,
    importing,
    setImporting,
    message,
    setMessage,
    busy,
    setBusy,
  });
  const guardedActions = {
    ...actions,
    act: () => onceWhilePending(actionInFlight, actions.act),
    retain: () => onceWhilePending(actionInFlight, actions.retain),
  };
  const closeDetails = (): void => {
    setSelected(null);
    setSelectedSummary(null);
  };
  return {
    selected,
    summary: selectedSummary,
    confirm,
    importing,
    message,
    busy,
    actions: guardedActions,
    closeDetails,
    closeConfirm: () => setConfirm(null),
    closeImport: () => setImporting(null),
  };
};

export const RecoveryView = ({ token }: RecoveryViewProps) => {
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) => v2.recoveryList(cursor, 50, token, signal),
    [token],
  );
  const listing = useRetryablePage(request, page);
  const dialogs = useRecoveryDialogs(token, listing);
  return <RecoveryPage listing={listing} {...dialogs} />;
};
