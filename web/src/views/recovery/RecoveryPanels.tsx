import { Button } from "react-aria-components/Button";
import { FileTrigger } from "react-aria-components/FileTrigger";
import type { PreparationSummary, RecoveryImportPreview } from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type {
  ConfirmState,
  ImportKind,
  ImportState,
  Inspection,
  RecoveryActions,
} from "./RecoveryState";

export type Listing = {
  items: PreparationSummary[];
  cursor: string | null;
  message: string | null;
  loading: boolean;
  load: (cursor: string | null) => Promise<void>;
};

const Summary = ({ item }: { item: PreparationSummary }) => (
  <>
    <bdi>{item.command}</bdi> · <bdi>{item.caseReference}</bdi> · {item.state} ·{" "}
    <bdi>{item.operationId}</bdi>
  </>
);

const InspectDetails = ({ value }: { value: Inspection }) => (
  <>
    <p>
      Operation <bdi>{value.preparation.summary.operationId}</bdi> · digest{" "}
      <bdi>{value.preparation.summary.requestSha256 ?? "Unavailable"}</bdi>
    </p>
    <p>
      Case <bdi>{value.preparation.summary.caseReference}</bdi> · command{" "}
      <bdi>{value.preparation.summary.command}</bdi>
    </p>
    <p>
      Expected revision {value.preparation.expectedRevision} · canonical format{" "}
      {value.preparation.canonicalCommandFormat}
    </p>
    <p>
      Preparing provenance: <bdi>{value.preparation.preparingApplicationVersion}</bdi> ·{" "}
      <bdi>{value.preparation.preparingContractKind}</bdi>
    </p>
    <dl className="review-values">
      {value.preparation.authoredValues.map((entry) => (
        <div key={entry.name}>
          <dt>{entry.name}</dt>
          <dd>
            <bdi>{entry.value}</bdi>
          </dd>
        </div>
      ))}
    </dl>
    <p>Observation: {value.observation.tag}</p>
    {value.observation.tag !== "FOUND" ? null : (
      <p>
        Observed accepted operation <bdi>{value.observation.value.operationId}</bdi>.
      </p>
    )}
  </>
);

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

export const RecoveryList = ({
  listing,
  busy,
  actions,
}: {
  listing: Listing;
  busy: string | null;
  actions: RecoveryActions;
}) => (
  <>
    {listing.message === null ? null : (
      <p className="error" role="alert">
        {listing.message}
      </p>
    )}
    <ul className="recovery-list">
      {listing.items.map((item) => (
        <li key={item.operationId}>
          <Summary item={item} />
          <Button onPress={() => actions.inspect(item)} isDisabled={busy === item.operationId}>
            Inspect
          </Button>
        </li>
      ))}
    </ul>
    {listing.cursor === null ? null : (
      <Button onPress={() => void listing.load(listing.cursor)} isDisabled={listing.loading}>
        Load more recovery
      </Button>
    )}
  </>
);

const ActionButtons = ({
  selected,
  summary,
  actions,
}: {
  selected: Inspection;
  summary: PreparationSummary;
  actions: RecoveryActions;
}) => {
  const digestAvailable = summary.requestSha256 !== null;
  const canResolve =
    selected.observation.tag !== "FOUND" &&
    summary.availableActions.includes("RESOLVE") &&
    digestAvailable;
  const canDismiss = summary.availableActions.includes("DISMISS") && digestAvailable;
  return (
    <div className="dialog-actions">
      {canResolve ? (
        <Button onPress={() => actions.choose("RESOLVE", summary)}>
          Resolve exact preparation
        </Button>
      ) : null}
      {canDismiss ? (
        <Button className="secondary-button" onPress={() => actions.choose("DISMISS", summary)}>
          Dismiss preparation
        </Button>
      ) : null}
      {digestAvailable && summary.availableActions.includes("EXPORT") ? (
        <Button className="secondary-button" onPress={() => actions.exportItem(summary)}>
          Export recovery envelope
        </Button>
      ) : null}
    </div>
  );
};

export const RecoveryDetailsDialog = ({
  selected,
  summary,
  onClose,
  actions,
}: {
  selected: Inspection | null;
  summary: PreparationSummary | null;
  onClose: () => void;
  actions: RecoveryActions;
}) => (
  <AccessibleModal
    title="Recovery details"
    description="The server supplied the current preparation, evidence, and observation state."
    isOpen={selected !== null}
    isDismissable
    onOpenChange={(open) => {
      if (!open) onClose();
    }}
  >
    {selected === null || summary === null ? null : (
      <>
        <InspectDetails value={selected} />
        <ActionButtons selected={selected} summary={summary} actions={actions} />
      </>
    )}
  </AccessibleModal>
);

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
}) => (
  <AccessibleModal
    title={
      confirm?.action === "EXPORT"
        ? "Export recovery envelope?"
        : confirm?.action === "RESOLVE"
          ? "Resolve exact preparation?"
          : "Dismiss preparation?"
    }
    description={
      confirm?.action === "EXPORT"
        ? "This file contains claimant data and recovery bytes. Save it only in private storage."
        : "This action uses the displayed operation ID and request digest. The server remains authoritative."
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
          Operation <bdi>{confirm.item.operationId}</bdi> · digest{" "}
          <bdi>{confirm.item.requestSha256 ?? "Unavailable"}</bdi>
        </p>
        <Button onPress={() => void actions.act()} isDisabled={busy !== null}>
          {busy === confirm.item.operationId
            ? "Working…"
            : `Confirm ${confirm.action.toLowerCase()}`}
        </Button>
      </>
    )}
  </AccessibleModal>
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
