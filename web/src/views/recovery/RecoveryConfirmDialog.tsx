import type { PreparationSummary } from "../../api/v3";
import { Button } from "react-aria-components/Button";
import { AccessibleModal } from "../../components/AccessibleModal";
import { usePresentation } from "../../presentation/context";
import type { ConfirmState, RecoveryActions } from "./RecoveryState";

const titleKeys = {
  EXPORT: "ui.exportTitle",
  RESOLVE: "ui.resolveTitle",
  DISMISS: "ui.dismissTitle",
} as const;
const confirmKeys = {
  RESOLVE: "ui.confirmResolve",
  DISMISS: "ui.confirmDismiss",
  EXPORT: "ui.confirmExport",
} as const;

const ConfirmationButton = ({
  confirm,
  busy,
  actions,
}: {
  confirm: ConfirmState;
  busy: string | null;
  actions: RecoveryActions;
}) => {
  const p = usePresentation();
  return (
    <Button onPress={() => void actions.act()} isDisabled={busy !== null}>
      {busy === confirm.item.operationId
        ? p.text("ui.working")
        : p.text(confirmKeys[confirm.action])}
    </Button>
  );
};

export const RecoveryConfirmDialog = ({
  confirm,
  busy,
  onClose,
  actions,
}: {
  confirm: ConfirmState | null;
  busy: string | null;
  onClose: () => void;
  actions: RecoveryActions;
}) => {
  const p = usePresentation();
  return (
    <AccessibleModal
      title={p.text(confirm === null ? "ui.dismissTitle" : titleKeys[confirm.action])}
      description={
        confirm?.action === "EXPORT" ? p.text("ui.exportWarning") : p.text("ui.recoveryConfirmHint")
      }
      isOpen={confirm !== null}
      isDismissable={busy === null}
      onOpenChange={(open) => {
        if (!open && busy === null) {
          onClose();
        }
      }}
    >
      {confirm === null ? null : (
        <>
          <p>
            {p.text("ui.operationDigest", {
              operationId: confirm.item.operationId,
              digest: confirm.item.requestSha256 ?? p.text("ui.unavailable"),
            })}
          </p>
          <ConfirmationButton confirm={confirm} busy={busy} actions={actions} />
        </>
      )}
    </AccessibleModal>
  );
};

export const RecoveryActionButtons = (props: {
  summary: PreparationSummary;
  actions: RecoveryActions;
  busy: boolean;
}) => {
  const { summary, actions, busy } = props;
  const p = usePresentation();
  const digestAvailable = summary.requestSha256 !== null;
  const canResolve =
    summary.authority === "PENDING" &&
    summary.availableActions.includes("RESOLVE") &&
    digestAvailable;
  const canDismiss = summary.availableActions.includes("DISMISS") && digestAvailable;
  return (
    <div className="dialog-actions">
      {canResolve ? (
        <Button
          isDisabled={busy}
          onPress={() => {
            actions.choose("RESOLVE", summary);
          }}
        >
          {p.text("ui.resolveExact")}
        </Button>
      ) : null}
      {canDismiss ? (
        <Button
          isDisabled={busy}
          className="secondary-button"
          onPress={() => {
            actions.choose("DISMISS", summary);
          }}
        >
          {p.text("ui.dismissPreparation")}
        </Button>
      ) : null}
      {digestAvailable && summary.availableActions.includes("EXPORT") ? (
        <Button
          isDisabled={busy}
          className="secondary-button"
          onPress={() => {
            actions.exportItem(summary);
          }}
        >
          {p.text("ui.exportEnvelope")}
        </Button>
      ) : null}
    </div>
  );
};
