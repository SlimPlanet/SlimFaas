import type { GetStaticProps } from 'next';
import { readFile } from 'fs/promises';
import path from 'path';
import matter from 'gray-matter';
import { remark } from 'remark';
import gfm from 'remark-gfm';
import html from 'remark-html';
import {
    DocumentationId,
    getDocumentationEntry,
} from './documentation-catalog';
import { renderMarkdownWithHighlight } from './markdown';

export interface DocumentationPageProps {
    contentHtml: string;
    title: string;
    description: string;
}

async function loadDocumentationPage(id: DocumentationId): Promise<DocumentationPageProps> {
    const entry = getDocumentationEntry(id);
    const absolutePath = path.join(
        /* turbopackIgnore: true */ process.cwd(),
        '..',
        '..',
        entry.sourcePath,
    );
    const fileContent = await readFile(
        /* turbopackIgnore: true */ absolutePath,
        'utf8',
    );
    const { content, data } = matter(fileContent);
    const processedContent = await remark().use(gfm).use(html).process(content);
    const contentHtml = await renderMarkdownWithHighlight(
        processedContent.toString(),
        entry.sourcePath,
    );

    return {
        contentHtml,
        title: typeof data.title === 'string' ? data.title : entry.title,
        description:
            typeof data.description === 'string' ? data.description : entry.description,
    };
}

export function createDocumentationStaticProps(
    id: DocumentationId,
): GetStaticProps<DocumentationPageProps> {
    return async () => ({
        props: await loadDocumentationPage(id),
    });
}
