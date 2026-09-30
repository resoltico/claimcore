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
