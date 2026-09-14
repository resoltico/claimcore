import { Button } from "react-aria-components/Button";
import { FileTrigger } from "react-aria-components/FileTrigger";
import type { RecoveryImportPreview } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { ImportKind, ImportState, RecoveryActions } from "./RecoveryState";

const ImportPreview = ({ value }: { value: RecoveryImportPreview }) => (
  <>
    <p>
      Artifact {value.artifactKind} · source digest <bdi>{value.sourceSha256}</bdi>
    </p>
    <p>
      Decoded target <bdi>{value.decodedEffect.caseReference}</bdi> · command{" "}
      {value.decodedEffect.command} · expected revision {value.decodedEffect.expectedRevision}
    </p>
    <p>
      The artifact is retained for explicit Recovery action; it is never submitted automatically.
    </p>
  </>
);

const ImportButton = ({
  kind,
  busy,
  onFile,
}: {
  kind: ImportKind;
  busy: boolean;
  onFile: (kind: ImportKind, file: File) => void;
}) => {
  const envelope = kind === "ENVELOPE";
  const mediaType = envelope
    ? "application/vnd.claimcore.recovery+json"
    : "application/vnd.claimcore.canonical-command+json";
  const label = envelope ? "Import recovery envelope" : "Import canonical record";
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
}) => (
  <AccessibleModal
    title="Retain imported recovery material?"
    description="The preview is retained only after confirmation and is never submitted automatically."
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
          Retain for Recovery
        </Button>
      </>
    )}
  </AccessibleModal>
);
