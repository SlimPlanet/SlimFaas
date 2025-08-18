
const nextConfig = {
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

module.exports = nextConfig;
