import { Fragment, useContext, useId, useState, type ReactNode } from "react";
import { taskDefinitions, type RegionContent, type RegionRow } from "@contracts/client";
import { PresentedValue } from "../components/PresentedValue";
import { usePresentation } from "../presentation/context";
import { renderCatalogKey } from "../presentation/messages";
import { actorName } from "../presentation/actorName";
import { followLink } from "../routing/followLink";
import { personLinkActorId } from "../routing/coreLinks";
import { LinkNavigationContext } from "../routing/LinkNavigationContext";
import { ActionMenu } from "./ActionMenu";
import { Button } from "./Button";
import { Dialog } from "./Dialog";
import { EmptyState } from "./EmptyState";
import { SectionsView } from "./SectionsView";
import { SuppliedMessage } from "./SuppliedMessage";
import { MoneyMeter } from "./regions/MoneyMeter";
import { coreRowLink } from "./regions/rowLink";
import type { RegionInteraction, RowLink } from "./regions/interaction";
import "./data-table.css";

type MenuItem = Parameters<typeof ActionMenu>[0]["items"][number];
type TableContent = Extract<RegionContent, { kind: "TABLE" }>;
type Column = TableContent["columns"][number] & { index: number };
type Value = RegionRow["values"][number];
type TableProps = RegionInteraction & {
  view: TableContent;
  timeZone?: string;
  emptyId?: string | undefined;
};

const Cell = ({ value, timeZone }: { value: Value; timeZone: string }) => {
  const p = usePresentation();
  if (value.kind !== "ABSENT") {
    return <PresentedValue value={value} timeZone={timeZone} avatar={value.kind === "ACTOR"} />;
  }
  return (
    <>
      <span aria-hidden="true">—</span>
      <span className="sr-only">{renderCatalogKey(p, value.reasonId)}</span>
    </>
  );
};

const rowCaption = (p: ReturnType<typeof usePresentation>, main: Value | undefined): string => {
  switch (main?.kind) {
    case "ACTOR":
      return actorName(p, main.value);
    case "DATE":
      return p.date(main.value);
    case "REFERENCE":
    case "PARTY":
    case "TEXT":
      return main.value;
    default:
      return p.text("label.results");
  }
};

const SecondaryCells = ({
  row,
  columns,
  timeZone,
}: {
  row: RegionRow;
  columns: ReadonlyArray<Column>;
  timeZone: string;
}) => {
  const p = usePresentation();
  return (
    <dl className="data-table-stack">
      {columns
        .filter(({ prominence }) => prominence === "SECONDARY")
        .map((column) => {
          const value = row.values[column.index];
          return value === undefined ? null : (
            <div key={column.key}>
              <dt>{renderCatalogKey(p, column.labelId)}</dt>
              <dd>
                <Cell value={value} timeZone={timeZone} />
              </dd>
            </div>
          );
        })}
    </dl>
  );
};

const TableRowControls = ({
  row,
  caption,
  actionPresentation,
  actionsDisabled,
  onAction,
  showDetails,
}: RegionInteraction & {
  row: RegionRow;
  caption: string;
  showDetails: (key: string) => void;
}) => {
  const p = usePresentation();
  const trigger = useId();
  const primary = useId();
  const offered = row.actions ?? [];
  const recommended = offered.find((action) => action.task === row.recommended);
  const items: MenuItem[] =
    onAction === undefined
      ? []
      : offered.map((action) => {
          const task = taskDefinitions.find(({ id }) => id === action.task)!;
          return {
            id: action.task,
            label:
              actionPresentation?.(action).label ??
              renderCatalogKey(p, task.reviewedLabelId ?? action.labelId),
            disabled: actionsDisabled === true || action.blocked !== null,
            consequence: task.consequence,
            select: () => {
              onAction(action, row, trigger);
            },
          };
        });
  if (row.detail !== undefined) {
    items.push({
      id: "details",
      label: p.text("ui.viewDetails"),
      disabled: false,
      consequence: null,
      select: () => {
        showDetails(row.key);
      },
    });
  }
  return (
    <td>
      <div className="data-table-actions">
        {recommended === undefined || onAction === undefined ? null : (
          <Button
            id={primary}
            compact
            isDisabled={actionsDisabled === true || recommended.blocked !== null}
            onPress={() => {
              onAction(recommended, row, primary, true);
            }}
          >
            {renderCatalogKey(p, recommended.labelId)}
          </Button>
        )}
        {items.length === 0 ? null : (
          <ActionMenu
            label={p.text("ui.actionsFor", { row: caption })}
            id={trigger}
            items={items}
            icon
            stewardshipLabel={p.text("label.dataStewardship")}
          />
        )}
      </div>
    </td>
  );
};

const RowControls = (props: Parameters<typeof TableRowControls>[0]) =>
  props.renderControls === undefined ? (
    <TableRowControls {...props} />
  ) : (
    <td>{props.renderControls(props.row)}</td>
  );

const linkedValue = (link: RowLink | undefined, value: Value, timeZone: string) => {
  const cell = <Cell value={value} timeZone={timeZone} />;
  return link === undefined ? (
    cell
  ) : (
    <a
      href={link.href}
      id={link.id}
      aria-label={link.label}
      title={value.kind === "REFERENCE" ? value.value : undefined}
      className="row-link"
      onClick={(event) => {
        followLink(event, link.follow);
      }}
    >
      {cell}
    </a>
  );
};

const DataRow = ({
  row,
  columns,
  timeZone,
  controls,
  showDetails,
  ...interaction
}: RegionInteraction & {
  row: RegionRow;
  columns: ReadonlyArray<Column>;
  timeZone: string;
  controls: boolean;
  showDetails: (key: string) => void;
}) => {
  const p = usePresentation();
  const inherited = useContext(LinkNavigationContext);
  const link = coreRowLink(row.link, interaction.linkNavigation ?? inherited);
  const firstPrimary = columns.findIndex(({ prominence }) => prominence === "PRIMARY");
  const linkKind =
    row.link !== undefined && personLinkActorId(row.link) !== undefined ? "ACTOR" : "REFERENCE";
  const linked = columns.findIndex((column) => row.values[column.index]?.kind === linkKind);
  const linkAt = linked >= 0 ? linked : Math.max(0, firstPrimary);
  const caption = rowCaption(p, row.values[columns[linkAt]?.index ?? 0]);
  return (
    <tr data-linked={link === undefined ? undefined : ""}>
      {columns.flatMap((column, position) => {
        const value = row.values[column.index];
        if (value === undefined) {
          return [];
        }
        return (
          <td
            key={column.key}
            data-column={column.key}
            data-prominence={column.prominence}
            data-money={column.isMoney || undefined}
            data-reference={value.kind === "REFERENCE" || undefined}
          >
            <div className="data-table-cell">
              {linkedValue(position === linkAt ? link : undefined, value, timeZone)}
            </div>
            {position === firstPrimary ? (
              <SecondaryCells row={row} columns={columns} timeZone={timeZone} />
            ) : null}
          </td>
        );
      })}
      {controls ? (
        <RowControls row={row} caption={caption} showDetails={showDetails} {...interaction} />
      ) : null}
    </tr>
  );
};

const RowSupplement = ({ columns, content }: { columns: number; content: ReactNode }) =>
  content === null || content === undefined ? null : (
    <tr>
      <td colSpan={columns}>{content}</td>
    </tr>
  );

const DataGridBody = ({
  rows,
  renderRow,
  isolateRows,
}: {
  rows: TableContent["rows"];
  renderRow: (row: RegionRow) => ReactNode;
  isolateRows: boolean;
}) =>
  isolateRows ? (
    rows.map((row) => <tbody key={row.key}>{renderRow(row)}</tbody>)
  ) : (
    <tbody>{rows.map(renderRow)}</tbody>
  );

const DataGrid = ({
  view,
  timeZone,
  showDetails,
  ...interaction
}: TableProps & {
  timeZone: string;
  showDetails: (key: string) => void;
}) => {
  const p = usePresentation();
  const columns = view.columns.flatMap((column, index) =>
    column.prominence === "DETAIL" ? [] : [{ ...column, index }],
  );
  const controls =
    interaction.renderControls !== undefined ||
    view.rows.some(
      (row) =>
        row.detail !== undefined ||
        (interaction.onAction !== undefined && (row.actions?.length ?? 0) > 0),
    );
  const columnsCount = columns.length + Number(controls);
  const rows = new Map(view.rows.map((row) => [row.key, row]));
  const renderRow = (row: RegionRow) => (
    <Fragment key={row.key}>
      <DataRow
        row={row}
        columns={columns}
        timeZone={timeZone}
        controls={controls}
        showDetails={showDetails}
        {...interaction}
      />
      <RowSupplement columns={columnsCount} content={interaction.renderSupplement?.(row)} />
    </Fragment>
  );
  const body =
    (view.groups ?? []).length === 0 ? (
      <DataGridBody
        rows={view.rows}
        renderRow={renderRow}
        isolateRows={interaction.renderSupplement !== undefined}
      />
    ) : (
      view.groups?.map((group) => (
        <tbody key={group.titleId}>
          <tr className="table-group">
            <th scope="rowgroup" colSpan={columnsCount}>
              {renderCatalogKey(p, group.titleId)} · {p.integer(group.count)}
            </th>
          </tr>
          {group.rowKeys.flatMap((key) => {
            const row = rows.get(key);
            return row === undefined ? [] : [renderRow(row)];
          })}
        </tbody>
      ))
    );
  return (
    <div className="table-view data-table">
      <table
        {...(interaction.labelledBy === undefined
          ? { "aria-label": p.text("label.results") }
          : { "aria-labelledby": interaction.labelledBy })}
      >
        <thead>
          <tr>
            {columns.map((column) => (
              <th
                key={column.key}
                scope="col"
                data-prominence={column.prominence}
                data-money={column.isMoney || undefined}
              >
                {renderCatalogKey(p, column.labelId)}
              </th>
            ))}
            {controls ? (
              <th scope="col">
                <span className="sr-only">{p.text("label.availableActs")}</span>
              </th>
            ) : null}
          </tr>
        </thead>
        {body}
      </table>
    </div>
  );
};

const DetailContents = ({
  detail,
  timeZone,
}: {
  detail: NonNullable<RegionRow["detail"]>;
  timeZone: string;
}) => (
  <>
    {detail.summary === null ? null : (
      <p>
        <SuppliedMessage message={detail.summary} timeZone={timeZone} />
      </p>
    )}
    <SectionsView sections={detail.highlights} timeZone={timeZone} />
    <SectionsView sections={detail.sections} timeZone={timeZone} />
  </>
);

export const DataTable = ({
  view,
  timeZone = "UTC",
  sectionLink,
  showTableSections = true,
  emptyId,
  ...interaction
}: TableProps) => {
  const p = usePresentation();
  const [detailKey, setDetailKey] = useState<string | null>(null);
  const detail = view.rows.find((row) => row.key === detailKey)?.detail;
  return (
    <>
      {view.meter === undefined ? null : (
        <MoneyMeter meter={view.meter} timeZone={timeZone} label={p.text("label.paid")} />
      )}
      {view.rows.length === 0 && emptyId !== undefined ? (
        <EmptyState messageId={emptyId} />
      ) : (
        <DataGrid view={view} timeZone={timeZone} showDetails={setDetailKey} {...interaction} />
      )}
      {!showTableSections || view.sections === undefined ? null : (
        <SectionsView
          sections={view.sections}
          headingLevel={2}
          timeZone={timeZone}
          {...(sectionLink === undefined ? {} : { link: sectionLink })}
        />
      )}
      <Dialog
        title={p.text("ui.viewDetails")}
        closeLabel={p.text("ui.close")}
        isOpen={detail !== undefined}
        isDismissable
        onOpenChange={(open) => {
          if (!open) {
            setDetailKey(null);
          }
        }}
      >
        {detail === undefined ? null : <DetailContents detail={detail} timeZone={timeZone} />}
      </Dialog>
    </>
  );
};
