import React from 'react';
import Layout from '../components/Layout';
import { GetStaticProps } from 'next';
import { fetchMarkdownFile, MarkdownData, MarkdownMetadata } from '../lib/github';
import { renderMarkdownWithHighlight } from '@/lib/markdown';

export interface DocPageProps {
    contentHtml: string;
    metadata: MarkdownMetadata;
}


export const getStaticProps: GetStaticProps<DocPageProps> = async () => {
    const markdownFile: MarkdownData = await fetchMarkdownFile(`documentation/mcp.md`);
    const contentHtml = await renderMarkdownWithHighlight(markdownFile.contentHtml);

    return {
        props: {
            contentHtml: contentHtml,
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
