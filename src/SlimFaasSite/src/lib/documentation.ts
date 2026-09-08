import type { GetStaticProps } from 'next';
import { readFile } from 'fs/promises';
import path from 'path';
import matter from 'gray-matter';
import { remark } from 'remark';
import gfm from 'remark-gfm';
import html from 'remark-html';
import {
    getDocumentationEntry,
} from './documentation-catalog.ts';
import type { DocumentationId } from './documentation-catalog.ts';
import { renderMarkdownWithHighlight } from './markdown.ts';
import type { DocumentationHeading } from './markdown.ts';

export interface DocumentationPageProps {
    id: DocumentationId;
    headings: DocumentationHeading[];
    contentHtml: string;
    title: string;
    description: string;
}

export async function loadDocumentationPage(id: DocumentationId): Promise<DocumentationPageProps> {
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
    const { contentHtml, headings } = await renderMarkdownWithHighlight(
        processedContent.toString(),
        entry.sourcePath,
    );

    return {
        id,
        headings,
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
