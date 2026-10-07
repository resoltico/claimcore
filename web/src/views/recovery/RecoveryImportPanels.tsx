import { ReferenceSummary } from "../../components/ReferenceSummary";
import { usePresentation } from "../../presentation/context";
import { Button } from "react-aria-components/Button";
import { FileTrigger } from "react-aria-components/FileTrigger";
import type { RecoveryImportPreview } from "../../api/v3";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { ImportState, RecoveryActions } from "./RecoveryState";

const ImportPreview = ({ value }: { value: RecoveryImportPreview }) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.artifact", { kind: p.token(value.artifactKind), digest: value.sourceSha256 })}
      </p>
      <p>
        <ReferenceSummary
          id="ui.importTarget"
          values={{
            reference: value.decodedEffect.caseReference,
            command: p.commandLabel(value.decodedEffect.command),
            revision: p.integer(value.decodedEffect.expectedRevision),
          }}
        />
      </p>
      <p>{p.text("ui.importDescription")}</p>
    </>
  );
};

const ImportButton = ({ busy, onFile }: { busy: boolean; onFile: (file: File) => void }) => {
  const p = usePresentation();
  const mediaType = "application/vnd.claimcore.recovery+json";
  const label = p.text("ui.importEnvelope");
  return (
    <FileTrigger
      acceptedFileTypes={[mediaType]}
      onSelect={(files) => {
        const [file] = Array.from(files ?? []);
        if (file !== undefined) {
          onFile(file);
        }
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
    <ImportButton busy={busy !== null} onFile={(file) => void actions.preview(file)} />
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
      closeLabel={p.text("ui.cancel")}
      title={p.text("ui.importTitle")}
      description={p.text("ui.importDescription")}
      isOpen={importing !== null}
      isDismissable={busy === null}
      onOpenChange={(open) => {
        if (!open && busy === null) {
          onClose();
        }
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
