import { NoticeView } from "../../presentation/Message";
import { usePresentation } from "../../presentation/context";
import type { Notice } from "../../api/notices";
import { Button } from "react-aria-components/Button";
import type { PreparationSummary } from "../../api/v2";
import {
  RecoveryConfirmDialog,
  RecoveryDetailsDialog,
  RecoveryImportDialog,
  RecoveryImports,
  RecoveryList,
  type Listing,
} from "./RecoveryPanels";
import type { ConfirmState, ImportState, Inspection, RecoveryActions } from "./RecoveryState";

type DialogProps = {
  selected: Inspection | null;
  summary: PreparationSummary | null;
  confirm: ConfirmState | null;
  importing: ImportState | null;
  busy: string | null;
  actions: RecoveryActions;
  closeDetails: () => void;
  closeConfirm: () => void;
  closeImport: () => void;
};

type RecoveryPageProps = DialogProps & { listing: Listing; message: Notice | null };

const RecoveryHeading = ({ listing }: { listing: Listing }) => {
  const p = usePresentation();
  return (
    <div className="section-heading">
      <h2 id="recovery-title">{p.text("ui.recovery")}</h2>
      <label>
        {p.text("ui.recoveryView")}
        <select
          aria-label={p.text("ui.recoveryView")}
          value={listing.view}
          disabled={listing.loading}
          onChange={(event) =>
            listing.setView(event.target.value === "TERMINAL" ? "TERMINAL" : "PENDING")
          }
        >
          <option value="PENDING">{p.text("ui.pending")}</option>
          <option value="TERMINAL">{p.text("ui.terminal")}</option>
        </select>
      </label>
      <Button onPress={() => void listing.load(null)} isDisabled={listing.loading}>
        {p.text("ui.reload")}
      </Button>
    </div>
  );
};

const Capacity = ({ listing }: { listing: Listing }) => {
  const p = usePresentation();
  const page = listing.page;
  if (page === null) return null;
  return (
    <p
      className={page.nearCapacity ? "notice" : undefined}
      role={page.nearCapacity ? "status" : undefined}
    >
      {p.text("ui.capacity", {
        count: p.integer(String(page.pendingPreparationCount)),
        maximum: p.integer(String(page.maximumPendingPreparations)),
        bytes: p.integer(String(page.pendingCanonicalRequestBytes)),
        maximumBytes: p.integer(String(page.maximumPendingCanonicalRequestBytes)),
      })}
      {page.nearCapacity ? p.text("ui.nearCapacity") : null}
    </p>
  );
};

const RecoveryDialogs = ({
  selected,
  summary,
  confirm,
  importing,
  busy,
  actions,
  closeDetails,
  closeConfirm,
  closeImport,
}: DialogProps) => (
  <>
    <RecoveryDetailsDialog
      selected={selected}
      summary={summary}
      onClose={closeDetails}
      actions={actions}
    />
    <RecoveryConfirmDialog confirm={confirm} busy={busy} onClose={closeConfirm} actions={actions} />
    <RecoveryImportDialog
      importing={importing}
      busy={busy}
      onClose={closeImport}
      actions={actions}
    />
  </>
);

export const RecoveryPage = ({
  listing,
  message,
  busy,
  actions,
  ...dialogs
}: RecoveryPageProps) => {
  const p = usePresentation();
  return (
    <section aria-labelledby="recovery-title">
      <RecoveryHeading listing={listing} />
      <p>{p.text("ui.recoveryHint")}</p>
      <Capacity listing={listing} />
      <RecoveryImports busy={busy} actions={actions} />
      {message === null ? null : (
        <p className="notice" role="status">
          <NoticeView value={message} />
        </p>
      )}
      <RecoveryList listing={listing} busy={busy} actions={actions} />
      <RecoveryDialogs busy={busy} actions={actions} {...dialogs} />
    </section>
  );
};
