import { ListFooter } from "../ui/ListFooter";
import { apiEndpoints, type ApiResult, type ServiceResponse } from "../api/service";
import { personLinkActorId } from "../routing/coreLinks";
import type { RegionRow, ActorListRequest } from "@contracts/client";
import { isTerm } from "../presentation/terms";
import { ResultPages } from "../ui/ResultPages";
import { renderCatalogKey } from "../presentation/messages";

import { FieldRule } from "../ui/Field";
import { Select } from "../ui/Select";
import { Button } from "../ui/Button";
import { queryInput, taskInput } from "../protocol/taskCatalog";
import { useCallback, useState } from "react";
import type { Notice } from "../protocol/notices";

import { useEndpointPage } from "../hooks/useEndpointPage";
import { NoticeView } from "../presentation/Message";
import { usePresentation } from "../presentation/context";
const peoplePage = (response: ServiceResponse<typeof apiEndpoints.peopleList>) => {
  if (response.outcome.kind !== "SUCCEEDED") {
    return null;
  }
  const doc = response.outcome.document;
  if (doc.basis.kind !== "SNAPSHOT") {
    return null;
  }
  return {
    items: doc.screen.regions.flatMap((region) =>
      region.content.kind === "TABLE" ? region.content.rows : [],
    ),
    nextCursor: doc.nextCursor,
    basis: doc.basis.observation,
    screen: doc.screen,
    disclosure: doc.disclosure,
  };
};
type Actor = RegionRow;
type ListingProps = {
  titleId?: string;
  onSelect: (actorId: string) => void;
  onDisclosureRefused: (notice?: Notice) => void;
  onDisclosureRestored: (source: "listing") => void;
};
type DirectoryFilter = { text: string; role: NonNullable<ActorListRequest["role"]> | "" };
const usePeoplePage = (
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
    (result: ApiResult<ServiceResponse<typeof apiEndpoints.peopleList>>) => {
      if (result.kind === "outcome" && result.value.outcome.kind === "SUCCEEDED") {
        onDisclosureRestored("listing");
      }
    },
    [onDisclosureRestored],
  );
  return useEndpointPage<
    typeof apiEndpoints.peopleList,
    Actor,
    NonNullable<ReturnType<typeof peoplePage>>
  >(apiEndpoints.peopleList, input, peoplePage, onDisclosureRefused, restored);
};
const DirectoryResults = (props: ListingProps & { filter: DirectoryFilter }) => {
  const p = usePresentation();
  const page = usePeoplePage(props, props.filter);
  return (
    <section>
      <ResultPages
        labelledBy={props.titleId ?? "people-actors-title"}
        views={page.views}
        linkNavigation={(link) => {
          const actor = personLinkActorId(link);
          return actor === undefined
            ? undefined
            : {
                id: `actor-grants-${actor}`,
                follow: () => {
                  props.onSelect(actor);
                },
              };
        }}
      />
      {page.message === null ? null : (
        <p role="alert">
          <NoticeView value={page.message} />
        </p>
      )}
      {page.loading ? <p role="status">{p.text("ui.loading")}</p> : null}
      {page.page === null ? null : (
        <ListFooter
          subject="people"
          count={page.items.length}
          basis={page.page.basis}
          continuation={page.cursor}
          loading={page.loading}
          busy={page.refreshBusy}
          load={(cursor) => {
            void page.load(cursor);
          }}
          refresh={page.refresh}
        />
      )}
    </section>
  );
};

const directorySearch = taskInput("input:directorySearch");
const roleFilter = queryInput(apiEndpoints.peopleList, "input:roleFilter");
export const PeopleListing = (props: ListingProps) => {
  const p = usePresentation();
  const [draft, setDraft] = useState<DirectoryFilter>({ text: "", role: "" });
  const [filter, setFilter] = useState(draft);
  return (
    <section>
      {props.titleId === undefined ? (
        <h2 id="people-actors-title" tabIndex={-1}>
          {p.text("view.PEOPLE.label")}
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
            <FieldRule name="directorySearch" ruleId={directorySearch.ruleId} />
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
          help={<FieldRule name="roleFilter" ruleId={roleFilter.ruleId} />}
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
