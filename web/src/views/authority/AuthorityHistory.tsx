import { casePath } from "../../routing/routes";
import { navigate } from "../../routing/navigation";
import { PageTables } from "../../ui/PageTables";
import { useCallback } from "react";
import type { HumanReadDocument_authorityHistory } from "@contracts/client";
type HistoryControl = HumanReadDocument_authorityHistory["context"]["entries"][number];
import type { ServiceResponse } from "../../api/service";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
const select = (reply: ServiceResponse<"authority.history">) => {
  if (reply.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = reply.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.context.entries,
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type HistoryPage = NonNullable<ReturnType<typeof select>>;
export const AuthorityHistory = ({ actorId }: { actorId: string }) => {
  const p = usePresentation();
  const input = useCallback(
    (cursor: string | null) => ({
      limit: 50,
      ...(actorId === "" ? {} : { actorId }),
      ...(cursor === null ? {} : { cursor }),
    }),
    [actorId],
  );
  const page = useEndpointPage<"authority.history", HistoryControl, HistoryPage>(
    "authority.history",
    input,
    select,
  );
  return (
    <section className="authority-history" aria-labelledby="authority-history-title">
      <h1 id="authority-history-title">{p.text("endpoint.authority.history.label")}</h1>
      {page.loading ? <p role="status">{p.text("ui.loadingHistory")}</p> : null}
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      <h2 className="sr-only">{p.text("ui.timeline.title")}</h2>
      <PageTables
        views={page.views}
        rowLink={(key) => {
          const item = page.items.find((entry) => entry.key === key);
          if (item === undefined || item.caseId === null) {
            return undefined;
          }
          const href = casePath(item.caseId);
          return {
            column: "scopeReference",
            href,
            follow: () => {
              navigate(href);
            },
          };
        }}
      />

      <button
        type="button"
        disabled={page.loading}
        onClick={() => {
          void page.load(null);
        }}
      >
        {p.text("ui.reloadHistory")}
      </button>
      {page.cursor === null ? null : (
        <button
          type="button"
          disabled={page.loading}
          onClick={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.moreHistory")}
        </button>
      )}
    </section>
  );
};
