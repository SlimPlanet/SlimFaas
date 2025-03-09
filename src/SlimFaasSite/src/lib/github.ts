import axios from 'axios';
import { remark } from 'remark';
import html from 'remark-html';
import matter from 'gray-matter';

const GITHUB_REPO = 'SlimPlanet/SlimFaas/'; // Remplace avec ton repo
const GITHUB_BRANCH = 'main'; // Remplace si ton branch est différent
const GITHUB_RAW_URL = `https://raw.githubusercontent.com/${GITHUB_REPO}/${GITHUB_BRANCH}`;

export interface MarkdownData {
    contentHtml: string;
    metadata: Record<string, unknown>;
}

/**
 * Récupère et convertit un fichier Markdown en HTML
 * @param filename - Chemin du fichier Markdown (ex: "docs/intro.md")
 * @returns MarkdownData avec HTML converti et métadonnées
 */
export async function fetchMarkdownFile(filename: string): Promise<MarkdownData> {
    const url = `${GITHUB_RAW_URL}/${filename}`;

    try {
        const response = await axios.get<string>(url);
        const fileContent = response.data;

        // Extraction du frontmatter et du contenu Markdown
        const { content, data } = matter(fileContent);

        // Conversion Markdown -> HTML
        const processedContent = await remark().use(html).process(content);
        const contentHtml = processedContent.toString();

        return { contentHtml, metadata: data };
    } catch (error) {
        console.error(`Erreur lors du chargement du fichier ${filename}:`, error);
        return { contentHtml: '', metadata: {} };
    }
}

/**
 * Récupère la liste des fichiers Markdown présents dans le repo GitHub
 * @returns Liste des slugs (nom des fichiers sans extension)
 */
export async function fetchMarkdownFilesList(): Promise<string[]> {
    const apiUrl = `https://api.github.com/repos/${GITHUB_REPO}/git/trees/${GITHUB_BRANCH}?recursive=1`;

    try {
        const response = await axios.get<{ tree: { path: string }[] }>(apiUrl);
        return response.data.tree
            .filter(file => file.path.startsWith('docs/') && file.path.endsWith('.md'))
            .map(file => file.path.replace('docs/', '').replace('.md', ''));
    } catch (error) {
        console.error('Erreur lors de la récupération de la liste des fichiers Markdown:', error);
        return [];
    }
}
