import type { ActorListRequest } from "@contracts/client";
import { FieldHelp } from "../components/FieldHelp";
import { Select } from "../ui/Select";
import { Button } from "../ui/Button";
import { productMetadata } from "../protocol/productMetadata";
import { ActorRoleScopes } from "../components/ActorRoleScopes";
import { actorName } from "../presentation/actorName";
import { followLink } from "../routing/followLink";
import { PageStatus } from "../components/PageStatus";
import { useCallback, useState } from "react";
import type { Notice } from "../protocol/notices";
import { type ApiResult, type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
const authorityPage = (response: ServiceResponse<"authority.list">) =>
  response.outcome.kind === "SUCCEEDED" ? response.outcome.data : null;
type Actor = NonNullable<ReturnType<typeof authorityPage>>["items"][number];
type ListingProps = {
  mayReadGrants: boolean;
  onSelect: (actorId: string) => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onDisclosureRestored: (source: "listing") => void;
};
type DirectoryFilter = { text: string; role: NonNullable<ActorListRequest["role"]> | "" };
const useAuthorityPage = (
  { onDisclosureRefused, onDisclosureRestored }: ListingProps,
  filter: DirectoryFilter,
) => {
  const input = useCallback(
    (cursor: string | null) => ({
      ...(cursor === null ? {} : { cursor }),
      limit: 50,
      ...(filter.text === "" ? {} : { text: filter.text }),
      ...(filter.role === "" ? {} : { role: filter.role }),
    }),
    [filter.text, filter.role],
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
  const label = actorName(p, actor.actor);
  if (!mayReadGrants) {
    return (
      <bdi className="actor-subject" title={label}>
        {label}
      </bdi>
    );
  }
  const href = `/people/${encodeURIComponent(actor.actor.actorId)}`;
  return (
    <a
      className="actor-subject row-link"
      href={href}
      id={`actor-grants-${actor.actor.actorId}`}
      title={label}
      aria-label={`${p.term("principalKind", actor.principal.kind)} · ${label} ${p.term("accountState", actor.state)}`}
      onClick={(event) => {
        followLink(event, () => {
          onSelect(actor.actor.actorId);
        });
      }}
    >
      <bdi title={label}>{label}</bdi>
    </a>
  );
};
const ActorRow = (props: {
  actor: Actor;
  onSelect: ListingProps["onSelect"];
  mayReadGrants: boolean;
}) => {
  const p = usePresentation();
  return (
    <tr className="authority-row">
      <td className="authority-subject">
        <ActorSubject {...props} />
        {props.actor.contact === null ? null : (
          <small className="actor-contact">
            <bdi>{props.actor.contact}</bdi>
          </small>
        )}
      </td>
      <td>
        <ul className="actor-role-chips">
          {props.actor.roles.map((summary) => (
            <li key={summary.role}>{p.term("role", summary.role)}</li>
          ))}
        </ul>
      </td>
      <td>
        <ActorRoleScopes roles={props.actor.roles} />
      </td>
      <td>
        <span className={`account-state account-state-${props.actor.state.toLowerCase()}`}>
          {p.term("accountState", props.actor.state)}
        </span>
      </td>
    </tr>
  );
};

const AuthorityTable = ({
  items,
  ...props
}: Pick<ListingProps, "mayReadGrants" | "onSelect"> & {
  items: Actor[];
}) => {
  const p = usePresentation();
  return (
    <table className="authority-table" aria-labelledby="authority-actors-title">
      <thead className="authority-columns">
        <tr>
          <th scope="col">{p.text("ui.people.person")}</th>
          <th scope="col">{p.text("ui.people.roles")}</th>
          <th scope="col">{p.text("ui.people.scope")}</th>
          <th scope="col">{p.text("ui.people.status")}</th>
        </tr>
      </thead>
      <tbody className="authority-rows">
        {items.map((actor) => (
          <ActorRow key={actor.actor.actorId} actor={actor} {...props} />
        ))}
      </tbody>
    </table>
  );
};
const DirectoryResults = (props: ListingProps & { filter: DirectoryFilter }) => {
  const p = usePresentation();
  const page = useAuthorityPage(props, props.filter);
  return (
    <section>
      <AuthorityTable
        items={page.items}
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

const directorySearch = productMetadata.catalog.inputFields.find(
  (field) => field.name === "directorySearch",
)!;
export const AuthorityListing = (props: ListingProps) => {
  const p = usePresentation();
  const [draft, setDraft] = useState<DirectoryFilter>({ text: "", role: "" });
  const [filter, setFilter] = useState(draft);
  return (
    <section>
      <h2 id="authority-actors-title" tabIndex={-1}>
        {p.text("ui.shell.people")}
      </h2>
      <form
        className="directory-filters"
        onSubmit={(event) => {
          event.preventDefault();
          setFilter(draft);
        }}
      >
        <div className="text-field">
          <div className="field-label-row">
            <label htmlFor="people-search">{p.fieldLabel("directorySearch")}</label>
            <FieldHelp label={p.fieldLabel("directorySearch")} ruleId={directorySearch.ruleId} />
          </div>
          <input
            id="people-search"
            name="text"
            type="search"
            value={draft.text}
            aria-describedby="people-search-hint"
            onChange={(event) => {
              setDraft((prior) => ({ ...prior, text: event.target.value }));
            }}
          />
          <small id="people-search-hint">{p.fieldHint("directorySearch")}</small>
        </div>
        <Select
          id="people-role-filter"
          label={p.fieldLabel("role")}
          value={draft.role}
          options={[
            { value: "", label: p.text("ui.anyFilter") },
            ...productMetadata.catalog.roles.map(({ role }) => ({
              value: role,
              label: p.term("role", role),
            })),
          ]}
          onChange={(value) => {
            const selected = productMetadata.catalog.roles.find(({ role }) => role === value);
            setDraft((prior) => ({ ...prior, role: selected?.role ?? "" }));
          }}
        />
        <Button type="submit">{p.text("ui.search")}</Button>
      </form>
      <DirectoryResults key={JSON.stringify(filter)} {...props} filter={filter} />
    </section>
  );
};
