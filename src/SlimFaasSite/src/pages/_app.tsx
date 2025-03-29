import '../styles/reboot.css';
import '../styles/main.scss';
import '../styles/index.scss';

import Head from 'next/head';
import type { AppProps } from 'next/app';

export default function App({ Component, pageProps }: AppProps) {
    return<>
        <Head>
            <link rel="icon"  type="image/svg+xml" href="/SlimFaas/slimfaas.svg" />
            <title>SlimFaas : The slimmest and simplest Function As A Service</title>
        </Head>
        <Component {...pageProps} />
    </>;
}
