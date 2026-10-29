import { PageStatus } from "../components/PageStatus";
import { useCallback } from "react";
import type { Notice } from "../protocol/notices";
import { type ApiResult, type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
const authorityPage = (response: ServiceResponse<"authority.list">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type Actor = NonNullable<ReturnType<typeof authorityPage>>["items"][number];
export const ActorSummary = ({ actor }: { actor: Actor }) => {
  const p = usePresentation();
  return (
    <dl className="actor-summary">
      <dt>{p.text("ui.actor")}</dt>
      <dd>
        <bdi dir="ltr">{actor.actorId}</bdi>
      </dd>
      <dt>{p.text("ui.principal")}</dt>
      <dd>
        {p.term("principalKind", actor.principal.kind)} ·{" "}
        <bdi dir="ltr" className="principal-issuer" title={actor.principal.issuer}>
          {actor.principal.issuer}
        </bdi>{" "}
        · <bdi>{actor.principal.subject}</bdi>
      </dd>
      <dt>{p.text("label.enabled")}</dt>
      <dd>{p.text(actor.enabled ? "ui.yes" : "ui.no")}</dd>
    </dl>
  );
};
type ListingProps = {
  mayReadGrants: boolean;
  onSelect: (actorId: string) => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onDisclosureRestored: (source: "listing") => void;
};
const useAuthorityPage = ({ onDisclosureRefused, onDisclosureRestored }: ListingProps) => {
  const input = useCallback(
    (cursor: string | null) => ({ ...(cursor === null ? {} : { cursor }), limit: 50 }),
    [],
  );
  const restored = useCallback(
    (result: ApiResult<ServiceResponse<"authority.list">>) => {
      if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
        onDisclosureRestored("listing");
      }
    },
    [onDisclosureRestored],
  );
  return useEndpointPage<"authority.list", Actor, NonNullable<ReturnType<typeof authorityPage>>>(
    "authority.list",
    input,
    authorityPage,
    onDisclosureRefused,
    restored,
  );
};
export const AuthorityListing = (props: ListingProps) => {
  const p = usePresentation();
  const page = useAuthorityPage(props);
  return (
    <section>
      <h2 id="authority-actors-title" tabIndex={-1}>
        {p.text("ui.actors")}
      </h2>
      <ul className="authority-rows">
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
      {page.page !== null && !page.loading && page.message === null ? (
        <PageStatus
          empty={page.items.length === 0}
          cursor={page.cursor}
          emptyMessage="ui.noAccessibleActors"
        />
      ) : null}
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
