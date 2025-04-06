import React from 'react';
import Layout from '../components/Layout';
import { GetStaticProps } from 'next';
import { fetchMarkdownFile, MarkdownData, MarkdownMetadata } from '../lib/github';

export interface DocPageProps {
    contentHtml: string;
    metadata: MarkdownMetadata;
}


export const getStaticProps: GetStaticProps<DocPageProps> = async () => {
    const markdownFile: MarkdownData = await fetchMarkdownFile(`documentation/planet-saver.md`);

    return {
        props: {
            contentHtml: markdownFile.contentHtml,
            metadata: markdownFile.metadata || {},
        },
    };
};

const PlanetSaver =  ({ contentHtml }: DocPageProps) => (
    <Layout>
        <div>
            <div dangerouslySetInnerHTML={{ __html: contentHtml }} />
        </div>
    </Layout>
);

export default PlanetSaver;
