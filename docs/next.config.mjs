import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

/** @type {import('next').NextConfig} */
const config = {
  reactStrictMode: true,
  // Docker image (docs/Dockerfile) builds the runtime from .next/standalone.
  output: 'standalone',
  async redirects() {
    // All docs live in root folders (sidebar tabs); /docs itself has no page.
    return [{ source: '/docs', destination: '/docs/guide', permanent: false }];
  },
};

export default withMDX(config);
