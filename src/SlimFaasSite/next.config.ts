import type { NextConfig } from "next";

const nextConfig = {
    output: 'export',
    basePath: '/slimfaas-site',
    images: {
        unoptimized: true,
    },
};

module.exports = nextConfig;
