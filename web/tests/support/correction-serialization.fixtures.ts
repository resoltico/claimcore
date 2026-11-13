// Independent literal two-group patch oracles; never derived from product metadata or encoding.
export const correctionSerializations = [
  [
    "KEEP",
    "KEEP",
    '{"operationId":"00000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4,"act":{"kind":"case.correct","groups":{"registration":{"kind":"KEEP"},"decision":{"kind":"KEEP"}}}}',
  ],
  [
    "KEEP",
    "UPDATE",
    '{"operationId":"00000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4,"act":{"kind":"case.correct","groups":{"registration":{"kind":"KEEP"},"decision":{"kind":"UPDATE","values":{"paymentDecisionDate":"2026-09-03","payableAmount":"2.0000","payableCurrency":"EUR"}}}}}',
  ],
  [
    "KEEP",
    "CLEAR",
    '{"operationId":"00000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4,"act":{"kind":"case.correct","groups":{"registration":{"kind":"KEEP"},"decision":{"kind":"CLEAR"}}}}',
  ],
  [
    "UPDATE",
    "KEEP",
    '{"operationId":"00000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4,"act":{"kind":"case.correct","groups":{"registration":{"kind":"UPDATE","values":{"claimantName":"Synthetic replacement","claimedAmount":"1.0000","claimedCurrency":"EUR"}},"decision":{"kind":"KEEP"}}}}',
  ],
  [
    "UPDATE",
    "UPDATE",
    '{"operationId":"00000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4,"act":{"kind":"case.correct","groups":{"registration":{"kind":"UPDATE","values":{"claimantName":"Synthetic replacement","claimedAmount":"1.0000","claimedCurrency":"EUR"}},"decision":{"kind":"UPDATE","values":{"paymentDecisionDate":"2026-09-03","payableAmount":"2.0000","payableCurrency":"EUR"}}}}}',
  ],
  [
    "UPDATE",
    "CLEAR",
    '{"operationId":"00000000-0000-4000-8000-000000000001","target":{"kind":"REFERENCE","caseReference":"CASE-SERIALIZATION"},"expectedRevision":4,"act":{"kind":"case.correct","groups":{"registration":{"kind":"UPDATE","values":{"claimantName":"Synthetic replacement","claimedAmount":"1.0000","claimedCurrency":"EUR"}},"decision":{"kind":"CLEAR"}}}}',
  ],
] as const;
