// Portable data primitives used by JSON byte validation and request freezing.
export {};
declare global {
  // oxlint-disable-next-line no-redeclare -- TypeScript merges the instance type with its ambient constructor value.
  interface TextEncoder {
    // oxlint-disable-next-line typescript/method-signature-style -- Method syntax must match the platform declaration for legal interface merging.
    encode(input?: string): Uint8Array<ArrayBuffer>;
  }
  var TextEncoder: {
    prototype: TextEncoder;
    new (): TextEncoder;
  };
  interface TextDecoderOptions {
    fatal?: boolean;
    ignoreBOM?: boolean;
  }
  interface TextDecodeOptions {
    stream?: boolean;
  }
  // oxlint-disable-next-line no-redeclare -- TypeScript merges the instance type with its ambient constructor value.
  interface TextDecoder {
    // oxlint-disable-next-line typescript/method-signature-style -- Method syntax must match the platform declaration for legal interface merging.
    decode(input?: ArrayBufferView | ArrayBufferLike, options?: TextDecodeOptions): string;
  }
  var TextDecoder: {
    prototype: TextDecoder;
    new (label?: string, options?: TextDecoderOptions): TextDecoder;
  };
  function structuredClone<T>(value: T): T;
}
