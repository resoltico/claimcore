import { listCountMessages, type WorkDateBasis } from "@contracts/client";
import { usePresentation } from "../presentation/context";
import { Button } from "./Button";
import { Icon } from "./icons";
import "./list-footer.css";

export const ListFooter = ({
  subject,
  count,
  basis,
  continuation,
  loading,
  busy = false,
  load,
  refresh,
}: {
  subject: keyof typeof listCountMessages;
  count: number;
  basis: WorkDateBasis;
  continuation: string | null;
  loading: boolean;
  busy?: boolean;
  load: (cursor: string) => void;
  refresh: () => void;
}) => {
  const p = usePresentation();
  const category = new Intl.PluralRules(p.language).select(count);
  const message = listCountMessages[subject][category];
  return (
    <footer className="list-footer">
      <span>{p.text(message, { count: p.integer(count) })}</span>
      {continuation === null ? null : (
        <Button
          variant="quiet"
          compact
          isDisabled={loading}
          onPress={() => {
            load(continuation);
          }}
        >
          {p.text("ui.loadMore")}
        </Button>
      )}
      <span className="list-footer-update">
        <time dateTime={basis.observedAt}>
          {p.text("ui.updatedAt", { time: p.recordedTime(basis.observedAt, basis.timeZone) })}
        </time>
        {busy ? <span role="status">{p.text("ui.refreshFailed")}</span> : null}
        <Button
          variant="quiet"
          compact
          isDisabled={loading}
          aria-label={p.text(busy ? "ui.retryRefresh" : "ui.refresh")}
          onPress={refresh}
        >
          {busy ? p.text("ui.retryRefresh") : <Icon name="refresh" />}
        </Button>
      </span>
    </footer>
  );
};
