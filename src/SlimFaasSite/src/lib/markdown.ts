// lib/markdown.ts
import hljs from 'highlight.js';
import { load } from 'cheerio';

export async function renderMarkdownWithHighlight(rawHtml: string): Promise<string> {
    // 2. Charger le HTML avec cheerio pour manipuler le DOM côté serveur.
    const $ = load(rawHtml);

    // 3. Sélectionner chaque bloc <pre><code> et appliquer highlight.js
    $('pre code').each((_ , element ) => {
        const code = $(element).text();       // Récupère le code brut
        const result = hljs.highlightAuto(code);  // Détection auto du langage ou highlight(code, { language: 'js' }) si vous savez le langage
        $(element).html(result.value);        // Remplace le contenu par la version colorisée
        // Optionnel : ajouter la classe hljs pour que le CSS s’applique
        $(element).addClass('hljs');
    });


    // ⬇️ Wrap tables for horizontal scrolling only when needed
    $('table').each((_, el) => {
        const $table = $(el);
        if (!$table.parent().hasClass('table-scroll')) {
            $table.wrap(
                '<div class="table-scroll" role="region" aria-label="Scrollable table" tabindex="0"></div>'
            );
        }
    });

    // 4. Renvoyer le HTML final
    return $.html();
}
