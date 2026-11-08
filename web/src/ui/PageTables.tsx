import { Fragment } from "react";
import { usePresentation } from "../presentation/context";
/* oxlint-disable react/no-array-index-key -- Page ordinals are stable during append-only loading; replacement reads clear the collection. */
import type { ReactNode } from "react";
import type { PresentedPage } from "../protocol/types";
import { TableView, type RowLink } from "./TableView";

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
}) => {
  const p = usePresentation();
  return (
    <>
      {views.map(({ view, basis }, page) => {
        const offset = views
          .slice(0, page)
          .reduce(
            (count, previous) =>
              count + (previous.view.kind === "TABLE" ? previous.view.rows.length : 0),
            0,
          );
        return view.kind === "TABLE" ? (
          <Fragment key={page}>
            <TableView
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
            <p className="observation-basis">
              {p.text("ui.workDateBasis", {
                date: p.date(basis.businessDate),
                zone: basis.timeZone,
              })}
            </p>
          </Fragment>
        ) : null;
      })}
    </>
  );
};

/* oxlint-enable react/no-array-index-key */
