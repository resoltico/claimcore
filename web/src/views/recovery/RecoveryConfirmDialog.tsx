import { Button } from "react-aria-components/Button";
import { AccessibleModal } from "../../components/AccessibleModal";
import { usePresentation } from "../../presentation/context";
import type { ConfirmState, RecoveryActions } from "./RecoveryState";

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
      title={
        confirm?.action === "EXPORT"
          ? p.text("ui.exportTitle")
          : confirm?.action === "RESOLVE"
            ? p.text("ui.resolveTitle")
            : p.text("ui.dismissTitle")
      }
      description={
        confirm?.action === "EXPORT" ? p.text("ui.exportWarning") : p.text("ui.recoveryConfirmHint")
      }
      isOpen={confirm !== null}
      isDismissable={busy === null}
      onOpenChange={(open) => {
        if (!open && busy === null) onClose();
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
        : p.text(
            confirm.action === "RESOLVE"
              ? "ui.confirmResolve"
              : confirm.action === "DISMISS"
                ? "ui.confirmDismiss"
                : "ui.confirmExport",
          )}
    </Button>
  );
};
