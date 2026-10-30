import { mockEndpoint } from "./endpoint-mocks";
import { wireReply } from "./contract-corpus.fixtures";

/** An independent summary response accompanies list-only flow fixtures; SummaryBand tests exercise its transport. */
export const stubWorklistSummary = () =>
  mockEndpoint("case.summary").mockResolvedValue({
    kind: "outcome",
    status: 200,
    value: wireReply("case.summary"),
  });
