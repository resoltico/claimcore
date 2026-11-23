import { useState, type ReactNode } from "react";
import { useCompact } from "../hooks/useCompact";
import { usePresentation } from "../presentation/context";
import { Button } from "./Button";
import { Dialog } from "./Dialog";
import "./toolbar.css";

export const Toolbar = ({
  children,
  filter,
  activeFilters = 0,
  className = "",
}: {
  children: ReactNode;
  filter?: ReactNode;
  activeFilters?: number;
  className?: string;
}) => {
  const p = usePresentation();
  const compact = useCompact();
  const [filtersOpen, setFiltersOpen] = useState(false);
  const label = p.text("ui.filtersSheet");
  return (
    <div className={["toolbar", className].filter(Boolean).join(" ")}>
      {filter === undefined ? null : <div className="toolbar-filter">{filter}</div>}
      {compact && filter !== undefined ? (
        <>
          <Button
            variant="secondary"
            onPress={() => {
              setFiltersOpen(true);
            }}
          >
            {activeFilters === 0
              ? label
              : p.text("ui.filtersSheetCount", { count: p.integer(activeFilters) })}
          </Button>
          <Dialog
            title={label}
            closeLabel={p.text("ui.close")}
            isOpen={filtersOpen}
            isDismissable
            onOpenChange={setFiltersOpen}
            placement="bottom"
          >
            <div className="toolbar-sheet">{children}</div>
          </Dialog>
        </>
      ) : (
        children
      )}
    </div>
  );
};
