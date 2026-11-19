export const redPage = "1 0 0 rg 0 0 200 200 re f";
export const bluePage = "0 0 1 rg 0 0 200 200 re f";

type PageStream = string | { flate: Buffer };
const streamObject = (stream: PageStream) => {
  const content = typeof stream === "string" ? stream : stream.flate.toString("latin1");
  const filter = typeof stream === "string" ? "" : " /Filter /FlateDecode";
  return `<< /Length ${Buffer.byteLength(content, "latin1")}${filter} >>\nstream\n${content}\nendstream`;
};

export const syntheticPdf = (
  streams: ReadonlyArray<PageStream>,
  resources: { dictionary: string; objects: ReadonlyArray<string> } = {
    dictionary: "<< >>",
    objects: [],
  },
): Buffer => {
  const pages = streams.map((_stream, index) => 3 + index * 2);
  const objects = [
    "<< /Type /Catalog /Pages 2 0 R >>",
    `<< /Type /Pages /Count ${pages.length} /Kids [${pages.map((page) => `${page} 0 R`).join(" ")}] >>`,
    ...streams.flatMap((stream, index) => [
      `<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Resources ${resources.dictionary} /Contents ${4 + index * 2} 0 R >>`,
      streamObject(stream),
    ]),
    ...resources.objects,
  ];
  let length = 0;
  const chunks: Buffer[] = [];
  const append = (value: string) => {
    const bytes = Buffer.from(value, "latin1");
    chunks.push(bytes);
    length += bytes.length;
  };
  append("%PDF-1.4\n");
  const offsets = objects.map((object, index) => {
    const offset = length;
    append(`${index + 1} 0 obj\n${object}\nendobj\n`);
    return offset;
  });
  const xref = length;
  append(`xref\n0 ${offsets.length + 1}\n0000000000 65535 f \n`);
  for (const offset of offsets) {
    append(`${String(offset).padStart(10, "0")} 00000 n \n`);
  }
  append(`trailer\n<< /Root 1 0 R /Size ${offsets.length + 1} >>\nstartxref\n${xref}\n%%EOF\n`);
  return Buffer.concat(chunks);
};
