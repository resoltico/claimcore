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
    <Button onPress={() => void listing.load(null)} isDisabled={listing.loading}>
      Reload
    </Button>
  </div>
);

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
      Recovery records technical uncertainty. Inspect current server state before resolving or
      dismissing an item.
    </p>
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
