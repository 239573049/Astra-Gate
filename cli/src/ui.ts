import process from 'node:process';

const useColor =
  process.stdout.isTTY === true && process.env.NO_COLOR !== '1' && process.env.TERM !== 'dumb';

function wrap(open: string): (text: string) => string {
  return (text: string): string => (useColor ? `\u001b[${open}m${text}\u001b[0m` : text);
}

export const style = {
  bold: wrap('1'),
  dim: wrap('2'),
  green: wrap('32'),
  yellow: wrap('33'),
  red: wrap('31'),
  cyan: wrap('36'),
};

export function info(message: string): void {
  console.log(message);
}

export function success(message: string): void {
  console.log(style.green(message));
}

export function warn(message: string): void {
  console.log(style.yellow(message));
}

export function fail(message: string): void {
  console.error(style.red(message));
}

/** Print aligned columns. All cells are treated as plain strings. */
export function printTable(rows: readonly (readonly string[])[], headers?: readonly string[]): void {
  const all: readonly (readonly string[])[] = headers ? [headers, ...rows] : rows;
  if (all.length === 0) return;
  const widths: number[] = [];
  for (const row of all) {
    row.forEach((cell, i) => {
      widths[i] = Math.max(widths[i] ?? 0, cell.length);
    });
  }
  const line = (row: readonly string[]): string =>
    row.map((cell, i) => cell + ' '.repeat((widths[i] ?? 0) - cell.length)).join('  ');
  if (headers) console.log(style.bold(line(headers)));
  for (const row of rows) console.log(line(row));
}
