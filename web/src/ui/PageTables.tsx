import { Fragment } from "react";
/* oxlint-disable react/no-array-index-key -- Page ordinals are stable during append-only loading; replacement reads clear the collection. */
import type { ReactNode } from "react";
import type { PresentedPage } from "../protocol/types";
import type { RowLink } from "./TableView";
import { ScreenView } from "./ScreenView";

export const PageTables = ({
  views,
  renderActions,
  rowLink,
  labelledBy,
}: {
  views: ReadonlyArray<PresentedPage>;
  renderActions?: (index: number) => ReactNode;
  rowLink?: (index: number) => RowLink | undefined;
  labelledBy?: string;
}) => (
  <>
    {views.map(({ view, basis }, page) => {
      const offset = views
        .slice(0, page)
        .reduce(
          (count, previous) =>
            count +
            previous.view.regions.reduce(
              (sum, region) =>
                sum + (region.content.kind === "TABLE" ? region.content.rows.length : 0),
              0,
            ),
          0,
        );
      return (
        <Fragment key={page}>
          <ScreenView
            view={view}
            {...(labelledBy === undefined ? {} : { labelledBy })}
            timeZone={basis.timeZone}
            {...(rowLink === undefined
              ? {}
              : { rowLink: (index: number) => rowLink(offset + index) })}
            {...(renderActions === undefined
              ? {}
              : { renderActions: (index: number) => renderActions(offset + index) })}
          />
        </Fragment>
      );
    })}
  </>
);

/* oxlint-enable react/no-array-index-key */
