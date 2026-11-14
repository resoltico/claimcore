export const hostCases = () => [
  {
    id: "input-invalid",
    valid: true,
    endpoint: null,
    status: 400,
    value: { hostFailure: { code: "INPUT_INVALID", phase: "NOT_STARTED" } },
  },
];
