import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
    output: 'export',
    images: {
        unoptimized: true,
        remotePatterns: [
            { protocol: 'https', hostname: 'www.cncf.io' },
            { protocol: 'https', hostname: 'github.com' },
            { protocol: 'https', hostname: 'raw.githubusercontent.com' },
        ],
    },
};

export default nextConfig;
