import { useContext, useState, type ReactNode } from "react";
import { usePresentation } from "../presentation/context";
import { Button } from "./Button";
import { Dialog, InsideDialog } from "./Dialog";

export const TechnicalDetails = ({
  children,
  className,
}: {
  children: ReactNode;
  className?: string | undefined;
}) => {
  const p = usePresentation();
  const inline = useContext(InsideDialog);
  const [open, setOpen] = useState(false);
  const label = p.text("label.technicalDetails");
  return inline ? (
    <details className={className} data-region="DETAILS">
      <summary>{label}</summary>
      {children}
    </details>
  ) : (
    <>
      <Button
        variant="quiet"
        compact
        onPress={() => {
          setOpen(true);
        }}
      >
        {label}
      </Button>
      <Dialog
        title={label}
        closeLabel={p.text("ui.close")}
        isOpen={open}
        isDismissable
        onOpenChange={setOpen}
      >
        <div className={className} data-region="DETAILS">
          {children}
        </div>
      </Dialog>
    </>
  );
};
