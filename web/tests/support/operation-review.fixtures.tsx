import type { ComponentProps } from "react";
import { OperationReview } from "../../src/features/operation/OperationReview";
import { usePresentation } from "../../src/presentation/context";
import { WorkDialog } from "../../src/ui/WorkDialog";

export const ReviewDialog = (props: Omit<ComponentProps<typeof OperationReview>, "children">) => {
  const p = usePresentation();
  const { model } = props;
  return (
    <OperationReview {...props}>
      {(review) =>
        review === null ? null : (
          <WorkDialog
            title={p.text("label.reviewTitle")}
            closeLabel={p.text("ui.returnToEdit")}
            isOpen
            isDismissable={!model.busy}
            onOpenChange={model.returnToEdit}
            footer={review.actions}
          >
            {review.body}
          </WorkDialog>
        )
      }
    </OperationReview>
  );
};
