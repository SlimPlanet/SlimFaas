import type { NextConfig } from "next";

const isGithubPages = process.env.NEXT_PUBLIC_DEPLOY_ENV === 'GH_PAGES';

const nextConfig = {
    output: 'export',
    basePath: isGithubPages ? '/SlimFaas' : '',
    assetPrefix: '' ,
    images: {
        unoptimized: true,
    },
};

module.exports = nextConfig;
