import type { NextConfig } from "next";

const nextConfig = {
    output: 'export',
    basePath: '/SlimFaas',
    images: {
        unoptimized: true,
    },
};

module.exports = nextConfig;
