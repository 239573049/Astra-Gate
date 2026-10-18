const CONTEXT = 3;

type Kind = ' ' | '-' | '+';
interface Op {
  kind: Kind;
  text: string;
}

function splitLines(text: string): string[] {
  if (text.length === 0) return [];
  const lines: string[] = [];
  let start = 0;
  for (let i = 0; i < text.length; i++) {
    if (text[i] !== '\n') continue;
    const end = i > start && text[i - 1] === '\r' ? i - 1 : i;
    lines.push(text.slice(start, end));
    start = i + 1;
  }
  if (start < text.length) lines.push(text.slice(start));
  return lines;
}

function diffOps(a: string[], b: string[]): Op[] {
  // LCS over lines (config files are small; O(n*m) is fine).
  const w = b.length + 1;
  const table = new Int32Array((a.length + 1) * w);
  for (let i = a.length - 1; i >= 0; i--)
    for (let j = b.length - 1; j >= 0; j--)
      table[i * w + j] =
        a[i] === b[j]
          ? table[(i + 1) * w + j + 1]! + 1
          : Math.max(table[(i + 1) * w + j]!, table[i * w + j + 1]!);

  const ops: Op[] = [];
  let i = 0;
  let j = 0;
  while (i < a.length && j < b.length) {
    if (a[i] === b[j]) {
      ops.push({ kind: ' ', text: a[i]! });
      i++;
      j++;
    } else if (table[(i + 1) * w + j]! >= table[i * w + j + 1]!) {
      ops.push({ kind: '-', text: a[i]! });
      i++;
    } else {
      ops.push({ kind: '+', text: b[j]! });
      j++;
    }
  }
  while (i < a.length) ops.push({ kind: '-', text: a[i++]! });
  while (j < b.length) ops.push({ kind: '+', text: b[j++]! });
  return ops;
}

/** Minimal unified diff generator used for change previews (LF line endings in the output). */
export const DiffTool = {
  /** A unified diff (with @@ hunks) between two versions of a file. */
  unified(filePath: string, before: string, after: string): string {
    const ops = diffOps(splitLines(before), splitLines(after));
    let out = `--- a/${filePath}\n+++ b/${filePath}\n`;

    const changeIdx: number[] = [];
    ops.forEach((op, i) => {
      if (op.kind !== ' ') changeIdx.push(i);
    });
    if (changeIdx.length === 0) return out;

    // Group change indices into runs separated by more than 2*CONTEXT equal lines.
    const groups: Array<[number, number]> = [];
    let gStart = changeIdx[0]!;
    let gPrev = changeIdx[0]!;
    for (const ci of changeIdx.slice(1)) {
      if (ci - gPrev - 1 > 2 * CONTEXT) {
        groups.push([gStart, gPrev + 1]);
        gStart = ci;
      }
      gPrev = ci;
    }
    groups.push([gStart, gPrev + 1]);

    let lineA = 0;
    let lineB = 0;
    const aPos: number[] = [];
    const bPos: number[] = [];
    for (const op of ops) {
      aPos.push(lineA);
      bPos.push(lineB);
      if (op.kind !== '+') lineA++;
      if (op.kind !== '-') lineB++;
    }

    for (const [cs, ce] of groups) {
      const start = Math.max(cs - CONTEXT, 0);
      const end = Math.min(ce + CONTEXT, ops.length);
      const aStart = start < ops.length ? aPos[start]! : lineA;
      const bStart = start < ops.length ? bPos[start]! : lineB;
      let aLen = 0;
      let bLen = 0;
      for (let i = start; i < end; i++) {
        if (ops[i]!.kind !== '+') aLen++;
        if (ops[i]!.kind !== '-') bLen++;
      }
      out += `@@ -${aStart + 1},${aLen} +${bStart + 1},${bLen} @@\n`;
      for (let i = start; i < end; i++) out += ops[i]!.kind + ops[i]!.text + '\n';
    }
    return out;
  },
};
