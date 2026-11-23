import type { PresentedPage } from "../protocol/types";
import { ScreenView } from "./ScreenView";
import type { RegionInteraction } from "./regions/interaction";
import type { Region } from "@contracts/client";

/** A typed page and its supplied rows retain the same order; opaque keys can bind composite identities. */
export const pageItemIndex = (views: ReadonlyArray<PresentedPage>, key: string): number =>
  views
    .flatMap(({ view }) =>
      view.regions.flatMap(({ content }) => {
        if (content.kind === "TABLE") {
          return content.rows.map((row) => row.key);
        }
        if (content.kind === "TIMELINE") {
          return content.entries.map((entry) => entry.key);
        }
        return [];
      }),
    )
    .indexOf(key);
type Table = Extract<Region["content"], { kind: "TABLE" }>;

const combineTable = (previous: Table, current: Table): Table => {
  const rows = new Map([...previous.rows, ...current.rows].map((row) => [row.key, row]));
  const groups = new Map((previous.groups ?? []).map((group) => [group.titleId, group]));
  for (const group of current.groups ?? []) {
    const earlier = groups.get(group.titleId);
    groups.set(
      group.titleId,
      earlier === undefined
        ? group
        : {
            ...group,
            count: group.count + earlier.count,
            rowKeys: [...new Set([...earlier.rowKeys, ...group.rowKeys])],
          },
    );
  }
  return { ...current, rows: [...rows.values()], groups: [...groups.values()] };
};
const combineResults = (views: ReadonlyArray<PresentedPage>) => {
  const regions = new Map<string, Region>();
  for (const { view } of views) {
    for (const [position, region] of view.regions.entries()) {
      const key = `${region.role}:${region.titleId ?? position}`;
      const previous = regions.get(key);
      const current = region.content;
      if (previous?.content.kind === "TABLE" && current.kind === "TABLE") {
        regions.set(key, { ...region, content: combineTable(previous.content, current) });
      } else if (previous?.content.kind === "TIMELINE" && current.kind === "TIMELINE") {
        const entries = new Map(
          [...previous.content.entries, ...current.entries].map((entry) => [entry.key, entry]),
        );
        regions.set(key, { ...region, content: { ...current, entries: [...entries.values()] } });
      } else {
        regions.set(key, region);
      }
    }
  }
  return [...regions.values()].filter((region) =>
    ["RESULTS", "ACTIVITY", "PAYMENTS", "NOTES", "ATTACHMENTS"].includes(region.role),
  );
};

export const ResultPages = ({
  views,
  roles,
  ...interaction
}: RegionInteraction & {
  views: ReadonlyArray<PresentedPage>;
  roles?: ReadonlyArray<Region["role"]>;
}) => {
  const latest = views.at(-1);
  if (latest === undefined) {
    return null;
  }
  return (
    <ScreenView
      view={{ regions: combineResults(views) }}
      timeZone={latest.basis.timeZone}
      {...(roles === undefined ? {} : { roles })}
      {...interaction}
    />
  );
};
