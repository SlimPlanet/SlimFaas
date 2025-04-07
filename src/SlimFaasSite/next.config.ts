import type { NextConfig } from "next";

const isGithubPages = process.env.NEXT_PUBLIC_DEPLOY_ENV === 'GH_PAGES';

const nextConfig = {
    output: 'export',
    basePath: '/SlimFaas',
    assetPrefix: isGithubPages ? '/SlimFaas/' : '',
    images: {
        unoptimized: true,
    },
};

module.exports = nextConfig;
