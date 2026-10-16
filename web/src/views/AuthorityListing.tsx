import { useCallback } from "react";
import type { Notice } from "../api/notices";
import { service, type ServiceResponse } from "../api/service";
import { useRetryablePage } from "../hooks/useRead";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
const authorityPage = (response: ServiceResponse<"authority.list">) =>
  response.outcome.tag === "SUCCEEDED" ? response.outcome.data : null;
type Actor = NonNullable<ReturnType<typeof authorityPage>>["items"][number];
export const ActorSummary = ({ actor }: { actor: Actor }) => {
  const p = usePresentation();
  return (
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
  );
};
type ListingProps = {
  token: string;
  mayReadGrants: boolean;
  onSelect: (actorId: string) => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onDisclosureRestored: (source: "listing") => void;
};
const useAuthorityPage = ({ token, onDisclosureRefused, onDisclosureRestored }: ListingProps) => {
  const request = useCallback(
    async (cursor: string | null, signal: AbortSignal) => {
      const result = await service.authorityList(cursor, token, signal);
      if (
        !signal.aborted &&
        result.kind === "outcome" &&
        result.value.outcome.tag === "SUCCEEDED"
      ) {
        onDisclosureRestored("listing");
      }
      return result;
    },
    [token, onDisclosureRestored],
  );
  return useRetryablePage<ServiceResponse<"authority.list">, Actor>(
    request,
    authorityPage,
    onDisclosureRefused,
  );
};
export const AuthorityListing = (props: ListingProps) => {
  const p = usePresentation();
  const page = useAuthorityPage(props);
  return (
    <section>
      <h3 id="authority-actors-title" tabIndex={-1}>
        {p.text("ui.actors")}
      </h3>
      <ul>
        {page.items.map((actor) => (
          <li key={actor.actorId}>
            <ActorSummary actor={actor} />
            {props.mayReadGrants ? (
              <button
                type="button"
                id={`actor-grants-${actor.actorId}`}
                onClick={() => {
                  props.onSelect(actor.actorId);
                }}
              >
                {p.text("ui.viewGrants")}
              </button>
            ) : null}
          </li>
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
