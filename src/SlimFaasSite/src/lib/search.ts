export interface SearchEntry { title: string; url: string; text: string }

export function searchDocumentation(entries: SearchEntry[], query: string) {
    const terms = query.toLowerCase().trim().split(/\s+/).filter(Boolean);
    if (!terms.length) return [];
    return entries.map(entry => {
        const title = entry.title.toLowerCase();
        const text = entry.text.toLowerCase();
        if (!terms.every(term => title.includes(term) || text.includes(term))) return null;
        const score = terms.reduce((sum, term) => sum + (title.includes(term) ? 10 : 1), 0);
        const start = Math.max(0, text.indexOf(terms[0]) - 40);
        return { ...entry, score, excerpt: `${start ? '…' : ''}${entry.text.slice(start, start + 160)}${entry.text.length > start + 160 ? '…' : ''}` };
    }).filter(entry => entry !== null).sort((a, b) => b.score - a.score || a.url.localeCompare(b.url)).slice(0, 12);
}
