import Head from 'next/head';
import Link from 'next/link';
import { useEffect } from 'react';
import { legacyDeadLetterTarget } from '@/lib/documentation-catalog';

export default function DeadLetterRedirect() {
    const target = legacyDeadLetterTarget('');
    useEffect(() => {
        window.location.replace(legacyDeadLetterTarget(window.location.hash));
    }, []);

    return <>
        <Head>
            <title>Dead Letter Queues — Async Functions | SlimFaas</title>
            <meta name="robots" content="noindex" />
            <link rel="canonical" href="https://slimfaas.dev/async-functions" />
        </Head>
        <main>
            <h1>Dead Letter Queues have moved</h1>
            <p><Link href={target}>Read the Async Functions documentation</Link>.</p>
        </main>
    </>;
}
