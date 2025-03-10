import '../styles/reboot.css';
import '../styles/main.scss';
import '../styles/index.scss';

import type { AppProps } from 'next/app';

export default function App({ Component, pageProps }: AppProps) {
    return <Component {...pageProps} />;
}
