import '../styles/reboot.css';
import 'highlight.js/styles/github.css';
import '../styles/main.scss';
import '../styles/index.scss';

import Head from 'next/head';
import type { AppProps } from 'next/app';

export default function App({ Component, pageProps }: AppProps) {
    return<>
        <Head>
<!-- Google Tag Manager -->
<script>(function(w,d,s,l,i){w[l]=w[l]||[];w[l].push({'gtm.start':
new Date().getTime(),event:'gtm.js'});var f=d.getElementsByTagName(s)[0],
j=d.createElement(s),dl=l!='dataLayer'?'&l='+l:'';j.async=true;j.src=
'https://www.googletagmanager.com/gtm.js?id='+i+dl;f.parentNode.insertBefore(j,f);
})(window,document,'script','dataLayer','GTM-PKM3T5QT');</script>
<!-- End Google Tag Manager -->
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
            <meta name="google-site-verification" content="xNhzS-RDYiAc7stQdDH4cnjtqssYzMf5bj5XsCwcgmM" />
        </Head>
        <Component {...pageProps} />
    </>;
}
