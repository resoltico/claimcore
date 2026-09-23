import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import type { Notice } from "../../api/notices";
import { Button } from "react-aria-components/Button";
import type {
  PreparationDetails,
  PreparationSummary,
  RecoveryListItem,
  RecoveryPage as RecoveryPageResult,
  RevokedOperation,
} from "../../api/v2";
import { AccessibleModal } from "../../components/AccessibleModal";
import type { Inspection, RecoveryActions, RecoveryViewKind } from "./RecoveryState";

export { RecoveryImportDialog, RecoveryImports } from "./RecoveryImportPanels";

export type Listing = {
  items: RecoveryListItem[];
  cursor: string | null;
  message: Notice | null;
  loading: boolean;
  page: RecoveryPageResult | null;
  view: RecoveryViewKind;
  setView: (view: RecoveryViewKind) => void;
  load: (cursor: string | null) => Promise<void>;
};

const Summary = ({ item }: { item: RecoveryListItem }) => {
  const p = usePresentation();
  return item.tag === "RETAINED" ? (
    <>
      {p.text("ui.recoverySummary", {
        command: p.commandLabel(item.summary.command),
        reference: item.summary.caseReference,
        authority: p.token(item.summary.authority),
        operationId: item.summary.operationId,
      })}
    </>
  ) : (
    <>
      {p.text("ui.revocationSummary", {
        operationId: item.revocation.operationId,
        timestamp: item.revocation.revokedAt,
      })}
    </>
  );
};

const AttemptEvidence = ({
  value,
  actions,
}: {
  value: PreparationDetails;
  actions: RecoveryActions;
}) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.attemptCount", { count: value.attempts.items.length })}
        {value.attempts.legacyUncertainty ? p.text("ui.legacyUncertainty") : null}
      </p>
      <ul>
        {value.attempts.items.map((attempt) => (
          <li key={attempt.attemptId}>
            <bdi>{attempt.attemptId}</bdi> · {attempt.startedAt} ·{" "}
            {p.token(attempt.settlement ?? "PENDING")}
          </li>
        ))}
      </ul>
      {value.attempts.nextCursor === null ? null : (
        <Button
          className="secondary-button"
          onPress={() =>
            actions.loadAttempts(value.summary.operationId, value.attempts.nextCursor!)
          }
        >
          {p.text("ui.moreAttempts")}
        </Button>
      )}
    </>
  );
};

const RetainedDetails = ({
  value,
  actions,
}: {
  value: PreparationDetails;
  actions: RecoveryActions;
}) => {
  const p = usePresentation();
  return (
    <>
      <p>
        {p.text("ui.retainedIdentity", {
          operationId: value.summary.operationId,
          authority: p.token(value.summary.authority),
          digest: value.summary.requestSha256 ?? p.text("ui.unavailable"),
        })}
      </p>
      <p>
        {p.text("ui.retainedCommand", {
          reference: value.summary.caseReference,
          command: p.commandLabel(value.summary.command),
        })}
      </p>
      <p>
        {p.text("ui.retainedFormat", {
          revision: p.integer(value.expectedRevision),
          format: String(value.canonicalCommandFormat),
        })}
      </p>
      <p>
        {p.text("ui.provenance", {
          version: value.preparingApplicationVersion,
          contract: value.preparingContractKind,
        })}
      </p>
      <dl className="review-values">
        {value.authoredValues.map((entry) => (
          <div key={entry.name}>
            <dt>{p.fieldLabel(entry.name)}</dt>
            <dd>
              <bdi>{entry.value}</bdi>
            </dd>
          </div>
        ))}
      </dl>
      <AttemptEvidence value={value} actions={actions} />
    </>
  );
};

const RevocationDetails = ({ operationId, revokedAt }: RevokedOperation) => {
  const p = usePresentation();
  return (
    <>
      <p>{p.text("ui.revokedOperation", { operationId, timestamp: revokedAt })}</p>
      <p>{p.text("ui.revokedHint")}</p>
    </>
  );
};

export const RecoveryList = ({
  listing,
  busy,
  actions,
}: {
  listing: Listing;
  busy: string | null;
  actions: RecoveryActions;
}) => {
  const p = usePresentation();
  return (
    <>
      {listing.message === null ? null : (
        <p className="error" role="alert">
          <NoticeView value={listing.message} />
        </p>
      )}
      <ul className="recovery-list">
        {listing.items.map((item) => {
          const id =
            item.tag === "RETAINED" ? item.summary.operationId : item.revocation.operationId;
          return (
            <li key={id}>
              <Summary item={item} />
              <Button onPress={() => actions.inspect(item)} isDisabled={busy === id}>
                {item.tag === "RETAINED" ? p.text("ui.inspect") : p.text("ui.inspectRevocation")}
              </Button>
            </li>
          );
        })}
      </ul>
      {listing.cursor === null ? null : (
        <Button onPress={() => void listing.load(listing.cursor)} isDisabled={listing.loading}>
          {p.text("ui.moreRecovery")}
        </Button>
      )}
    </>
  );
};

const ActionButtons = ({
  summary,
  actions,
}: {
  summary: PreparationSummary;
  actions: RecoveryActions;
}) => {
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
        <Button onPress={() => actions.choose("RESOLVE", summary)}>
          {p.text("ui.resolveExact")}
        </Button>
      ) : null}
      {canDismiss ? (
        <Button className="secondary-button" onPress={() => actions.choose("DISMISS", summary)}>
          {p.text("ui.dismissPreparation")}
        </Button>
      ) : null}
      {digestAvailable && summary.availableActions.includes("EXPORT") ? (
        <Button className="secondary-button" onPress={() => actions.exportItem(summary)}>
          {p.text("ui.exportEnvelope")}
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
}) => {
  const p = usePresentation();
  return (
    <AccessibleModal
      title={p.text("ui.recoveryDetails")}
      description={p.text("ui.recoveryDetailsHint")}
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
          <p>{p.text("ui.observation", { state: p.token(selected.value.observation.tag) })}</p>
          {selected.value.observation.tag !== "FOUND" ? null : (
            <p>
              {p.text("ui.observedAccepted", {
                operationId: selected.value.observation.value.operationId,
              })}
            </p>
          )}
          <ActionButtons summary={summary} actions={actions} />
        </>
      )}
    </AccessibleModal>
  );
};

export { RecoveryConfirmDialog } from "./RecoveryConfirmDialog";
