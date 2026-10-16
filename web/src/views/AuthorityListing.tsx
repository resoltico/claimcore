import { useCallback } from "react";
import { service, type ServiceResponse } from "../api/service";
import { useRetryablePage } from "../hooks/useRead";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
const authorityPage = (response: ServiceResponse<"authority.list">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;
type Actor = NonNullable<ReturnType<typeof authorityPage>>["items"][number];
const AuthorityActor = ({ actor }: { actor: Actor }) => {
  const p = usePresentation();
  return (
    <li>
      <dl>
        <dt>{p.text("ui.actor")}</dt>
        <dd>
          <bdi>{actor.actorId}</bdi>
        </dd>
        <dt>{p.text("ui.principal")}</dt>
        <dd>
          {p.token(actor.principal.kind)} · <bdi>{actor.principal.issuer}</bdi> ·{" "}
          <bdi>{actor.principal.subject}</bdi>
        </dd>
        <dt>{p.text("ui.enabled")}</dt>
        <dd>{p.text(actor.enabled ? "ui.yes" : "ui.no")}</dd>
      </dl>
      <ul>
        {actor.grants.map((grant) => (
          <li
            key={`${grant.role}:${grant.scope.kind}:${grant.scope.kind === "CASE" ? grant.scope.caseReference : ""}`}
          >
            {p.token(grant.role)} · {p.token(grant.scope.kind)}{" "}
            {grant.scope.kind === "CASE" ? <bdi>{grant.scope.caseReference}</bdi> : null}
          </li>
        ))}
      </ul>
    </li>
  );
};
export const AuthorityListing = ({ token }: { token: string }) => {
  const p = usePresentation();
  const request = useCallback(
    (cursor: string | null, signal: AbortSignal) => service.authorityList(cursor, token, signal),
    [token],
  );
  const page = useRetryablePage<ServiceResponse<"authority.list">, Actor>(request, authorityPage);
  return (
    <section>
      <h3>{p.text("ui.actors")}</h3>
      <ul>
        {page.items.map((actor) => (
          <AuthorityActor key={actor.actorId} actor={actor} />
        ))}
      </ul>
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      <button type="button" disabled={page.loading} onClick={() => void page.load(null)}>
        {p.text("ui.reloadActors")}
      </button>
      {page.cursor === null ? null : (
        <button type="button" disabled={page.loading} onClick={() => void page.load(page.cursor)}>
          {p.text("ui.moreActors")}
        </button>
      )}
    </section>
  );
};
