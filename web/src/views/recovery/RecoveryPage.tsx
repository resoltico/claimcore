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

type RecoveryPageProps = DialogProps & { listing: Listing; message: string | null };

const RecoveryHeading = ({ listing }: { listing: Listing }) => (
  <div className="section-heading">
    <h2 id="recovery-title">Recovery</h2>
    <label>
      Recovery view
      <select
        aria-label="Recovery view"
        value={listing.view}
        disabled={listing.loading}
        onChange={(event) =>
          listing.setView(event.target.value === "TERMINAL" ? "TERMINAL" : "PENDING")
        }
      >
        <option value="PENDING">Pending</option>
        <option value="TERMINAL">Terminal</option>
      </select>
    </label>
    <Button onPress={() => void listing.load(null)} isDisabled={listing.loading}>
      Reload
    </Button>
  </div>
);

const Capacity = ({ listing }: { listing: Listing }) => {
  const page = listing.page;
  if (page === null) return null;
  return (
    <p
      className={page.nearCapacity ? "notice" : undefined}
      role={page.nearCapacity ? "status" : undefined}
    >
      Pending recovery capacity: {page.pendingPreparationCount} / {page.maximumPendingPreparations}{" "}
      preparations · {page.pendingCanonicalRequestBytes} /{" "}
      {page.maximumPendingCanonicalRequestBytes} canonical bytes
      {page.nearCapacity ? ". Near capacity; resolve or dismiss known pending items." : "."}
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
}: RecoveryPageProps) => (
  <section aria-labelledby="recovery-title">
    <RecoveryHeading listing={listing} />
    <p>
      Pending recovery is shown by default. Terminal view records accepted, dismissed, and revoked
      authority without presenting it as executable work. Inspect server state before resolving or
      dismissing an item.
    </p>
    <Capacity listing={listing} />
    <RecoveryImports busy={busy} actions={actions} />
    {message === null ? null : (
      <p className="notice" role="status">
        {message}
      </p>
    )}
    <RecoveryList listing={listing} busy={busy} actions={actions} />
    <RecoveryDialogs busy={busy} actions={actions} {...dialogs} />
  </section>
);
