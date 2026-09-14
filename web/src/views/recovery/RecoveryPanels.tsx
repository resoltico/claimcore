import { Button } from "react-aria-components/Button";
import type {
  PreparationDetails,
  PreparationSummary,
  RecoveryListItem,
  RecoveryPage as RecoveryPageResult,
  RevokedOperation,
} from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { ConfirmState, Inspection, RecoveryActions, RecoveryViewKind } from "./RecoveryState";

export { RecoveryImportDialog, RecoveryImports } from "./RecoveryImportPanels";

export type Listing = {
  items: RecoveryListItem[];
  cursor: string | null;
  message: string | null;
  loading: boolean;
  page: RecoveryPageResult | null;
  view: RecoveryViewKind;
  setView: (view: RecoveryViewKind) => void;
  load: (cursor: string | null) => Promise<void>;
};

const Summary = ({ item }: { item: RecoveryListItem }) =>
  item.tag === "RETAINED" ? (
    <>
      <bdi>{item.summary.command}</bdi> · <bdi>{item.summary.caseReference}</bdi> ·{" "}
      {item.summary.authority} · <bdi>{item.summary.operationId}</bdi>
    </>
  ) : (
    <>
      REVOKED · <bdi>{item.revocation.operationId}</bdi> · {item.revocation.revokedAt}
    </>
  );

const AttemptEvidence = ({
  value,
  actions,
}: {
  value: PreparationDetails;
  actions: RecoveryActions;
}) => (
  <>
    <p>
      Attempt evidence: {value.attempts.items.length} shown
      {value.attempts.legacyUncertainty ? "; legacy uncertainty remains" : "."}
    </p>
    <ul>
      {value.attempts.items.map((attempt) => (
        <li key={attempt.attemptId}>
          <bdi>{attempt.attemptId}</bdi> · {attempt.startedAt} · {attempt.settlement ?? "PENDING"}
        </li>
      ))}
    </ul>
    {value.attempts.nextCursor === null ? null : (
      <Button
        className="secondary-button"
        onPress={() => actions.loadAttempts(value.summary.operationId, value.attempts.nextCursor!)}
      >
        Load more attempts
      </Button>
    )}
  </>
);

const RetainedDetails = ({
  value,
  actions,
}: {
  value: PreparationDetails;
  actions: RecoveryActions;
}) => (
  <>
    <p>
      Operation <bdi>{value.summary.operationId}</bdi> · authority {value.summary.authority} ·
      digest <bdi>{value.summary.requestSha256 ?? "Unavailable"}</bdi>
    </p>
    <p>
      Case <bdi>{value.summary.caseReference}</bdi> · command <bdi>{value.summary.command}</bdi>
    </p>
    <p>
      Expected revision {value.expectedRevision} · canonical format {value.canonicalCommandFormat}
    </p>
    <p>
      Preparing provenance: <bdi>{value.preparingApplicationVersion}</bdi> ·{" "}
      <bdi>{value.preparingContractKind}</bdi>
    </p>
    <dl className="review-values">
      {value.authoredValues.map((entry) => (
        <div key={entry.name}>
          <dt>{entry.name}</dt>
          <dd>
            <bdi>{entry.value}</bdi>
          </dd>
        </div>
      ))}
    </dl>
    <AttemptEvidence value={value} actions={actions} />
  </>
);

const RevocationDetails = ({ operationId, revokedAt, reason }: RevokedOperation) => (
  <>
    <p>
      Operation <bdi>{operationId}</bdi> was revoked at {revokedAt}.
    </p>
    <p>{reason}</p>
    <p>This revoked authority is terminal. It cannot be resurrected or submitted.</p>
  </>
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
      {listing.items.map((item) => {
        const id = item.tag === "RETAINED" ? item.summary.operationId : item.revocation.operationId;
        return (
          <li key={id}>
            <Summary item={item} />
            <Button onPress={() => actions.inspect(item)} isDisabled={busy === id}>
              {item.tag === "RETAINED" ? "Inspect" : "Inspect revocation"}
            </Button>
          </li>
        );
      })}
    </ul>
    {listing.cursor === null ? null : (
      <Button onPress={() => void listing.load(listing.cursor)} isDisabled={listing.loading}>
        Load more recovery
      </Button>
    )}
  </>
);

const ActionButtons = ({
  summary,
  actions,
}: {
  summary: PreparationSummary;
  actions: RecoveryActions;
}) => {
  const digestAvailable = summary.requestSha256 !== null;
  const canResolve =
    summary.authority === "PENDING" &&
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
    description="The server supplied the current authority, evidence, and observation state."
    isOpen={selected !== null}
    isDismissable
    onOpenChange={(open) => {
      if (!open) onClose();
    }}
  >
    {selected === null ? null : selected.tag === "REVOKED" ? (
      <RevocationDetails {...selected.revocation} />
    ) : summary === null ? null : (
      <>
        <RetainedDetails value={selected.value.preparation} actions={actions} />
        <p>Observation: {selected.value.observation.tag}</p>
        {selected.value.observation.tag !== "FOUND" ? null : (
          <p>
            Observed accepted operation <bdi>{selected.value.observation.value.operationId}</bdi>.
          </p>
        )}
        <ActionButtons summary={summary} actions={actions} />
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
