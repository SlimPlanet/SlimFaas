import '../styles/reboot.css';
import 'highlight.js/styles/github.css';
import '../styles/main.scss';
import '../styles/index.scss';

import Head from 'next/head';
import type { AppProps } from 'next/app';

export default function App({ Component, pageProps }: AppProps) {
    return<>
        <Head>
            <link rel="icon"  type="image/svg+xml" href="/slimfaas.svg" />
            <title>SlimFaas : The slimmest and simplest Function As A Service</title>
            {/* Open Graph (Facebook, LinkedIn, etc.) */}
            <meta property="og:title" content="SlimFaas : The slimmest and simplest Function As A Service" />
            <meta property="og:description" content="Deploy functions effortlessly with SlimFaas, the ultra-light FaaS platform." />
            <meta property="og:image" content="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" />
            <meta property="og:url" content="https://github.com/SlimPlanet/SlimFaas" />
            <meta property="og:type" content="website" />

            {/* Twitter Card */}
            <meta name="twitter:card" content="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" />
            <meta name="twitter:title" content="SlimFaas : The slimmest and simplest Function As A Service" />
            <meta name="twitter:description" content="Deploy functions effortlessly with SlimFaas, the ultra-light FaaS platform." />
            <meta name="twitter:image" content="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" />
        </Head>
        <Component {...pageProps} />
    </>;
}
