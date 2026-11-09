import { isTerm } from "../presentation/terms";
import { PageTables } from "../ui/PageTables";
import { renderCatalogKey } from "../presentation/messages";
import type { ActorListRequest } from "@contracts/client";
import { FieldHelp } from "../components/FieldHelp";
import { Select } from "../ui/Select";
import { Button } from "../ui/Button";
import { queryInput, taskInput } from "../protocol/taskCatalog";
import { PageStatus } from "../components/PageStatus";
import { useCallback, useState } from "react";
import type { Notice } from "../protocol/notices";
import { type ApiResult, type ServiceResponse } from "../api/service";
import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
const authorityPage = (response: ServiceResponse<"authority.list">) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = response.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.context.actorIds,
    nextCursor: doc.context.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
  };
};
type Actor = string;
type ListingProps = {
  titleId?: string;
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
const DirectoryResults = (props: ListingProps & { filter: DirectoryFilter }) => {
  const p = usePresentation();
  const page = useAuthorityPage(props, props.filter);
  return (
    <section>
      <PageTables
        labelledBy={props.titleId ?? "authority-actors-title"}
        views={page.views}
        rowLink={(key) => {
          const id = page.items.find((id) => id === key);
          if (id === undefined || !props.mayReadGrants) {
            return undefined;
          }
          return {
            column: "actor",
            href: "/people/" + encodeURIComponent(id),
            id: "actor-grants-" + id,
            follow: () => {
              props.onSelect(id);
            },
          };
        }}
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
      <button
        type="button"
        disabled={page.loading}
        onClick={() => {
          void page.load(null);
        }}
      >
        {p.text("ui.reloadActors")}
      </button>
      {page.cursor === null ? null : (
        <button
          type="button"
          disabled={page.loading}
          onClick={() => {
            void page.load(page.cursor);
          }}
        >
          {p.text("ui.moreActors")}
        </button>
      )}
    </section>
  );
};

const directorySearch = taskInput("input:directorySearch");
const roleFilter = queryInput("query:authorityList", "input:roleFilter");
export const AuthorityListing = (props: ListingProps) => {
  const p = usePresentation();
  const [draft, setDraft] = useState<DirectoryFilter>({ text: "", role: "" });
  const [filter, setFilter] = useState(draft);
  return (
    <section>
      {props.titleId === undefined ? (
        <h2 id="authority-actors-title" tabIndex={-1}>
          {p.text("ui.shell.people")}
        </h2>
      ) : null}
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
            <FieldHelp
              name="directorySearch"
              label={p.fieldLabel("directorySearch")}
              ruleId={directorySearch.ruleId}
            />
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
          label={renderCatalogKey(p, roleFilter.labelId)}
          help={
            <FieldHelp
              name="roleFilter"
              label={renderCatalogKey(p, roleFilter.labelId)}
              ruleId={roleFilter.ruleId}
            />
          }
          {...(roleFilter.hintId === null
            ? {}
            : { description: renderCatalogKey(p, roleFilter.hintId) })}
          value={draft.role}
          options={[
            { value: "", label: p.text("ui.anyFilter") },
            ...roleFilter.choices
              .filter((role) => isTerm("role", role))
              .map((role) => ({
                value: role,
                label: p.term("role", role),
              })),
          ]}
          onChange={(value) => {
            const selected = roleFilter.choices.find(
              (role) => role === value && isTerm("role", role),
            );
            setDraft((prior) => ({
              ...prior,
              role: selected !== undefined && isTerm("role", selected) ? selected : "",
            }));
          }}
        />
        <Button type="submit">{p.text("ui.search")}</Button>
      </form>
      <DirectoryResults key={JSON.stringify(filter)} {...props} filter={filter} />
    </section>
  );
};
