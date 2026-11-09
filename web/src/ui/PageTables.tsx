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
export const PageTables = ({
  views,
  roles,
  ...interaction
}: RegionInteraction & {
  views: ReadonlyArray<PresentedPage>;
  roles?: ReadonlyArray<Region["role"]>;
}) => (
  <>
    {views.map(({ view, basis }, page) => (
      <ScreenView
        // oxlint-disable-next-line react/no-array-index-key -- Issued pages are append-only and replaced together on a fresh read.
        key={page}
        view={view}
        timeZone={basis.timeZone}
        {...(roles === undefined ? {} : { roles })}
        {...interaction}
      />
    ))}
  </>
);
