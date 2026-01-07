import '../styles/reboot.css';
import 'highlight.js/styles/github.css';
import '../styles/main.scss';
import '../styles/index.scss';

import Head from 'next/head';
import Script from 'next/script';
import type { AppProps } from 'next/app';
import { useRouter } from 'next/router';
import { useEffect } from 'react';
import dynamic from 'next/dynamic';

// Bannière client-only (nommée CookieBanner pour éviter toute confusion)
const CookieBanner = dynamic(() => import('@/components/CookieConsent'), { ssr: false });
const MermaidRenderer = dynamic(() => import('@/components/MermaidRenderer'), { ssr: false });


type DataLayerEvent = { event?: string; [key: string]: unknown };

declare global {
    interface Window {
        dataLayer?: DataLayerEvent[];
        gtag?: (...args: unknown[]) => void;
    }
}

export default function App({ Component, pageProps }: AppProps) {
    const router = useRouter();
    const gtmId = process.env.NEXT_PUBLIC_GTM_ID || 'GTM-PKM3T5QT';
    const isProd = process.env.NODE_ENV === 'production';

    // SPA pageviews
    useEffect(() => {
        if (!isProd) return;

        const ensureDL = () => {
            if (!window.dataLayer) window.dataLayer = [];
        };

        const pushPageview = (url: string) => {
            ensureDL();
            window.dataLayer!.push({ event: 'pageview', page: url });
        };

        pushPageview(window.location.pathname + window.location.search);
        router.events.on('routeChangeComplete', pushPageview);
        return () => router.events.off('routeChangeComplete', pushPageview);
    }, [router.events, isProd]);

    return (
        <>
            <Head>
                <link rel="icon" type="image/svg+xml" href="/slimfaas.svg" />
                <title>SlimFaas : The slimmest and simplest Function As A Service</title>

                {/* Open Graph */}
                <meta property="og:title" content="SlimFaas : The slimmest and simplest Function As A Service" />
                <meta property="og:description" content="Deploy functions effortlessly with SlimFaas, the ultra-light FaaS platform." />
                <meta property="og:image" content="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" />
                <meta property="og:url" content="https://github.com/SlimPlanet/SlimFaas" />
                <meta property="og:type" content="website" />

                {/* Twitter Card */}
                <meta name="twitter:card" content="summary_large_image" />
                <meta name="twitter:title" content="SlimFaas : The slimmest and simplest Function As A Service" />
                <meta name="twitter:description" content="Deploy functions effortlessly with SlimFaas, the ultra-light FaaS platform." />
                <meta name="twitter:image" content="https://github.com/SlimPlanet/SlimFaas/blob/main/documentation/SlimFaas.png?raw=true" />
            </Head>

            {/* GTM */}
            {isProd && (
                <Script
                    id="gtm-base"
                    strategy="afterInteractive"
                    dangerouslySetInnerHTML={{
                        __html: `
              (function(w,d,s,l,i){w[l]=w[l]||[];w[l].push({'gtm.start':
              new Date().getTime(),event:'gtm.js'});var f=d.getElementsByTagName(s)[0],
              j=d.createElement(s),dl=l!='dataLayer'?'&l='+l:'';j.async=true;j.src=
              'https://www.googletagmanager.com/gtm.js?id='+i+dl;f.parentNode.insertBefore(j,f);
              })(window,document,'script','dataLayer','${gtmId}');
            `,
                    }}
                />
            )}

            <Component {...pageProps} />
            {isProd && <CookieBanner />}
            <MermaidRenderer />
        </>
    );
}
