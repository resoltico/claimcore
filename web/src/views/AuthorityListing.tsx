import { followLink } from "../routing/followLink";
import { IdentifierValue } from "../components/IdentifierValue";
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
export type KnownActorGrantCount = { actorId: string; count: number };
export const ActorSummary = ({ actor }: { actor: Actor }) => {
  const p = usePresentation();
  return (
    <dl className="actor-summary">
      <dt>{p.text("ui.actor")}</dt>
      <dd>
        <IdentifierValue value={actor.actorId} />
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
  knownGrantCount?: KnownActorGrantCount | null;
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
const KnownGrantCount = ({ count }: { count: number | null }) => {
  const p = usePresentation();
  if (count === null) {
    return null;
  }
  return <small>{p.text("ui.knownGrantCount", { count: p.integer(count) })}</small>;
};
const ActorSubject = ({
  actor,
  onSelect,
  mayReadGrants,
}: {
  actor: Actor;
  onSelect: ListingProps["onSelect"];
  mayReadGrants: boolean;
}) => {
  const p = usePresentation();
  if (!mayReadGrants) {
    return (
      <bdi className="actor-subject" title={actor.principal.subject}>
        {actor.principal.subject}
      </bdi>
    );
  }
  const href = `/authority/${encodeURIComponent(actor.actorId)}`;
  return (
    <a
      className="actor-subject row-link"
      href={href}
      id={`actor-grants-${actor.actorId}`}
      title={actor.actorId}
      aria-label={`${p.term("principalKind", actor.principal.kind)} · ${actor.principal.subject} ${p.text(actor.enabled ? "ui.yes" : "ui.no")}`}
      onClick={(event) => {
        followLink(event, () => {
          onSelect(actor.actorId);
        });
      }}
    >
      <bdi title={actor.principal.subject}>{actor.principal.subject}</bdi>
    </a>
  );
};
const ActorRow = (props: {
  actor: Actor;
  knownGrantCount?: KnownActorGrantCount | null;
  onSelect: ListingProps["onSelect"];
  mayReadGrants: boolean;
}) => {
  const p = usePresentation();
  return (
    <tr className="authority-row">
      <td>{p.term("principalKind", props.actor.principal.kind)}</td>
      <td className="authority-subject">
        <ActorSubject {...props} />
      </td>
      <td className="authority-enabled">
        <span className="authority-cell-label" aria-hidden="true">
          {p.text("label.enabled")}:{" "}
        </span>
        {p.text(props.actor.enabled ? "ui.yes" : "ui.no")}
      </td>
      <td className="authority-grant-count">
        {props.mayReadGrants ? (
          <KnownGrantCount
            count={
              props.knownGrantCount?.actorId === props.actor.actorId
                ? props.knownGrantCount.count
                : null
            }
          />
        ) : null}
      </td>
    </tr>
  );
};
const AuthorityTable = ({
  items,
  ...props
}: Pick<ListingProps, "mayReadGrants" | "onSelect" | "knownGrantCount"> & {
  items: Actor[];
}) => {
  const p = usePresentation();
  return (
    <table className="authority-table" aria-labelledby="authority-actors-title">
      <thead className="authority-columns">
        <tr>
          <th scope="col">{p.fieldLabel("principal.kind")}</th>
          <th scope="col">{p.fieldLabel("principal.subject")}</th>
          <th scope="col">{p.text("label.enabled")}</th>
          <th scope="col">{p.text("ui.grantCount")}</th>
        </tr>
      </thead>
      <tbody className="authority-rows">
        {items.map((actor) => (
          <ActorRow key={actor.actorId} actor={actor} {...props} />
        ))}
      </tbody>
    </table>
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
      <AuthorityTable
        items={page.items}
        knownGrantCount={props.knownGrantCount ?? null}
        onSelect={props.onSelect}
        mayReadGrants={props.mayReadGrants}
      />
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
