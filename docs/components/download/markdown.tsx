import type { ReactNode } from 'react';
import { Fragment, jsx, jsxs } from 'react/jsx-runtime';
import { toJsxRuntime } from 'hast-util-to-jsx-runtime';
import remarkGfm from 'remark-gfm';
import remarkParse from 'remark-parse';
import remarkRehype from 'remark-rehype';
import { unified } from 'unified';

/**
 * Renders an untrusted markdown string (e.g. release notes from the release
 * feed) into React elements on the server. GFM covers the constructs the
 * changelog uses (tables, task lists, autolinks, strikethrough). Raw HTML is
 * dropped because `remarkRehype` runs without `allowDangerousHtml`, so notes
 * can never inject markup; pair the output with the `prose` class for styling.
 */
const processor = unified().use(remarkParse).use(remarkGfm).use(remarkRehype);

export function Markdown({ text }: { text: string }): ReactNode {
  const hast = processor.runSync(processor.parse(text));
  return toJsxRuntime(hast, { Fragment, development: false, jsx, jsxs });
}
