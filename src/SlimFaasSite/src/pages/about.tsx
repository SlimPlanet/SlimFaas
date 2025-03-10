import React from 'react';
import Layout from '../components/Layout';
import { GetStaticProps } from 'next';
import { fetchMarkdownFile, MarkdownData } from '../lib/github';

interface DocPageProps {
    contentHtml: string;
    metadata: Record<string, unknown>;
}


export const getStaticProps: GetStaticProps<DocPageProps> = async () => {
    const markdownFile: MarkdownData = await fetchMarkdownFile(`README.md`);

    return {
        props: {
            contentHtml: markdownFile.contentHtml,
            metadata: markdownFile.metadata || {},
        },
    };
};

const formatMetadata = (metadata: Record<string, unknown>):string  => {
    // @ts-ignore
    return metadata.title || "Documentation"
}
const About =  ({ contentHtml, metadata }: DocPageProps) => (
    <Layout>
        <h1>About Us</h1>
        <p>
            Welcome to SlimFaas! SlimFaas is a lightweight serverless framework designed for
            simplicity, scalability, and high performance. We are committed to providing developers
            with a streamlined experience for deploying and managing serverless functions.
        </p>
        <div style={{ maxWidth: "800px", margin: "auto", padding: "2rem" }}>
            <h1>{formatMetadata(metadata)}</h1>
            <div dangerouslySetInnerHTML={{ __html: contentHtml }} />
        </div>
    </Layout>
);

export default About;
