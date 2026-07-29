import Head from 'next/head';
import Layout from './Layout';
import type { DocumentationPageProps } from '@/lib/documentation';

const DocumentationPage = ({
    contentHtml,
    title,
    description,
}: DocumentationPageProps) => (
    <>
        <Head>
            <title>{`${title} | SlimFaas`}</title>
            <meta name="description" content={description} />
            <meta property="og:title" content={title} />
            <meta property="og:description" content={description} />
            <meta name="twitter:title" content={title} />
            <meta name="twitter:description" content={description} />
        </Head>
        <Layout>
            <div dangerouslySetInnerHTML={{ __html: contentHtml }} />
        </Layout>
    </>
);

export default DocumentationPage;
