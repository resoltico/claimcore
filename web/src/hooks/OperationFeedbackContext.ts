import { createContext } from "react";
import type { OperationReceipt } from "../protocol/types";

export const OperationFeedbackContext = createContext<
  ((receipt: OperationReceipt, operationId: string) => void) | null
>(null);
