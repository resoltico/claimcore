import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { FileTrigger } from "react-aria-components/FileTrigger";
import type { RecoveryImportPreview } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { ImportKind, ImportState, RecoveryActions } from "./RecoveryState";

const ImportPreview = ({ value }: { value: RecoveryImportPreview }) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.artifact", { kind: p.token(value.artifactKind), digest: value.sourceSha256 })}
      </p>
      <p>
        {p.text("ui.importTarget", {
          reference: value.decodedEffect.caseReference,
          command: p.commandLabel(value.decodedEffect.command),
          revision: p.integer(value.decodedEffect.expectedRevision),
        })}
      </p>
      <p>{p.text("ui.importHint")}</p>
    </>
  );
};

const ImportButton = ({
  kind,
  busy,
  onFile,
}: {
  kind: ImportKind;
  busy: boolean;
  onFile: (kind: ImportKind, file: File) => void;
}) => {
  const p = usePresentation();
  const envelope = kind === "ENVELOPE";
  const mediaType = envelope
    ? "application/vnd.claimcore.recovery+json"
    : "application/vnd.claimcore.canonical-command+json";
  const label = p.text(envelope ? "ui.importEnvelope" : "ui.importRecord");
  return (
    <FileTrigger
      acceptedFileTypes={[mediaType]}
      onSelect={(files) => {
        const file = files?.item(0);
        if (file !== null && file !== undefined) onFile(kind, file);
      }}
    >
      <Button isDisabled={busy}>{label}</Button>
    </FileTrigger>
  );
};

export const RecoveryImports = ({
  busy,
  actions,
}: {
  busy: string | null;
  actions: RecoveryActions;
}) => (
  <div className="actions">
    <ImportButton kind="ENVELOPE" busy={busy === "import-ENVELOPE"} onFile={actions.preview} />
    <ImportButton kind="RECORD" busy={busy === "import-RECORD"} onFile={actions.preview} />
  </div>
);

export const RecoveryImportDialog = ({
  importing,
  busy,
  onClose,
  actions,
}: {
  importing: ImportState | null;
  busy: string | null;
  onClose: () => void;
  actions: RecoveryActions;
}) => {
  const p = usePresentation();
  return (
    <AccessibleModal
      title={p.text("ui.importTitle")}
      description={p.text("ui.importDescription")}
      isOpen={importing !== null}
      isDismissable={busy === null}
      onOpenChange={(open) => {
        if (!open && busy === null) onClose();
      }}
    >
      {importing === null ? null : (
        <>
          <ImportPreview value={importing.preview} />
          <Button onPress={() => void actions.retain()} isDisabled={busy !== null}>
            {p.text("ui.retainForRecovery")}
          </Button>
        </>
      )}
    </AccessibleModal>
  );
};
