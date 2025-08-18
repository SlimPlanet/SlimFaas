// src/pages/_document.tsx
import Document, { Html, Head, Main, NextScript, DocumentContext } from 'next/document';
import Script from 'next/script';

class MyDocument extends Document {
    static async getInitialProps(ctx: DocumentContext) {
        const initialProps = await Document.getInitialProps(ctx);
        return { ...initialProps };
    }

    render() {
        const gtmId = process.env.NEXT_PUBLIC_GTM_ID || 'GTM-PKM3T5QT';
        const isProd = process.env.NODE_ENV === 'production';

        return (
            <Html lang="en">
                <Head>
                    {isProd && (
                        <Script
                            id="consent-mode-default"
                            strategy="beforeInteractive"
                            dangerouslySetInnerHTML={{
                                __html: `
                  window.dataLayer = window.dataLayer || [];
                  function gtag(){dataLayer.push(arguments);}
                  gtag('consent', 'default', {
                    analytics_storage: 'denied',
                    ad_storage: 'denied',
                    ad_user_data: 'denied',
                    ad_personalization: 'denied',
                    wait_for_update: 500
                  });
                `,
                            }}
                        />
                    )}
                </Head>
                <body>
                {/* Google Tag Manager (noscript) */}
                <noscript>
                    <iframe
                        src={`https://www.googletagmanager.com/ns.html?id=${gtmId}`}
                        height="0"
                        width="0"
                        style={{ display: 'none', visibility: 'hidden' }}
                    />
                </noscript>
                {/* End Google Tag Manager (noscript) */}

                <Main />
                <NextScript />
                </body>
            </Html>
        );
    }
}

export default MyDocument;
