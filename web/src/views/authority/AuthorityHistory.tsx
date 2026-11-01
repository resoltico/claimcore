import { useCallback } from "react";
import type { AuthorityHistoryEntry } from "@contracts/client";
import type { ServiceResponse } from "../../api/service";
import { useEndpointPage } from "../../hooks/useEndpointPage";
import { usePresentation } from "../../presentation/context";
import { NoticeView } from "../../presentation/Message";
import { ActorIdentity, RecordedTime } from "../../components/ActorIdentity";
import { casePath } from "../../routing/routes";
import { followLink } from "../../routing/followLink";
import { navigate } from "../../routing/navigation";
const select = (reply: ServiceResponse<"authority.history">) =>
  reply.outcome.kind === "SUCCEEDED"
    ? { items: reply.outcome.data.entries, nextCursor: reply.outcome.data.nextCursor }
    : null;
type HistoryPage = NonNullable<ReturnType<typeof select>>;
const HistoryFacts = ({ entry }: { entry: AuthorityHistoryEntry }) => {
  const p = usePresentation();
  const { act } = entry;
  if (act.kind === "REGISTER" || act.kind === "PROVISION_OWNER") {
    return (
      <p>
        <bdi>{act.principal.subject}</bdi> · {p.term("principalKind", act.principal.kind)}
      </p>
    );
  }
  if (act.kind !== "GRANT" && act.kind !== "REVOKE") {
    return null;
  }
  const { scope } = act;
  return (
    <p>
      {p.term("role", act.role)} ·{" "}
      {scope.kind === "CASE" ? (
        <a
          href={casePath(scope.caseId)}
          onClick={(event) => {
            followLink(event, () => {
              navigate(casePath(scope.caseId));
            });
          }}
        >
          <bdi dir="ltr" className="identifier-value">
            {scope.caseReference}
          </bdi>
        </a>
      ) : (
        p.term("scope", scope.kind)
      )}
    </p>
  );
};
const HistoryEntries = ({ items }: { items: ReadonlyArray<AuthorityHistoryEntry> }) => {
  const p = usePresentation();
  return (
    <ol className="history-list">
      {items.map((entry) => (
        <li key={entry.sequence}>
          <header>
            <RecordedTime value={entry.recordedAt} />
            <strong>{p.term("act", entry.act.kind)}</strong>
          </header>
          <dl className="history-attribution">
            <dt>{p.text("ui.targetActor")}</dt>
            <dd>
              <ActorIdentity actorId={entry.targetActorId} />
            </dd>
            <dt>{p.text("ui.recordedBy")}</dt>
            <dd>
              <ActorIdentity actorId={entry.recordedBy} />
            </dd>
          </dl>
          <HistoryFacts entry={entry} />
          <details>
            <summary>{p.text("ui.technicalDetails")}</summary>
            <dl>
              <dt>{p.text("ui.exactUtc")}</dt>
              <dd>
                <time dateTime={entry.recordedAt}>{entry.recordedAt}</time>
              </dd>
              <dt>{p.text("ui.targetActor")}</dt>
              <dd>
                <bdi dir="ltr">{entry.targetActorId}</bdi>
              </dd>
              <dt>{p.text("ui.recordedBy")}</dt>
              <dd>
                <bdi dir="ltr">{entry.recordedBy}</bdi>
              </dd>
            </dl>
          </details>
        </li>
      ))}
    </ol>
  );
};
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
  const page = useEndpointPage<"authority.history", AuthorityHistoryEntry, HistoryPage>(
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
      <HistoryEntries items={page.items} />

      <button type="button" disabled={page.loading} onClick={() => void page.load(null)}>
        {p.text("ui.reloadHistory")}
      </button>
      {page.cursor === null ? null : (
        <button type="button" disabled={page.loading} onClick={() => void page.load(page.cursor)}>
          {p.text("ui.moreHistory")}
        </button>
      )}
    </section>
  );
};
