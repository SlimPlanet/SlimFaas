import { useId, useRef, useState } from 'react';
import Link from 'next/link';
import { searchDocumentation } from '@/lib/search';
import type { SearchEntry } from '@/lib/search';

export default function DocumentationSearch() {
    const id = useId();
    const [query, setQuery] = useState('');
    const [entries, setEntries] = useState<SearchEntry[]>([]);
    const [status, setStatus] = useState<'idle' | 'loading' | 'ready' | 'error'>('idle');
    const loading = useRef(false);
    const input = useRef<HTMLInputElement>(null);
    const fetchIndex = async () => {
        if (loading.current || status === 'ready') return;
        loading.current = true;
        setStatus('loading');
        try {
            const response = await fetch('/search-index.json');
            if (!response.ok) throw new Error('Search unavailable');
            setEntries(await response.json());
            setStatus('ready');
        } catch { setStatus('error'); }
        finally { loading.current = false; }
    };
    const matches = searchDocumentation(entries, query);
    return <div className="doc-search" onKeyDown={event => {
        if (event.key === 'Escape') { setQuery(''); input.current?.focus(); }
    }}>
        <label className="doc-search__label" htmlFor={id}>Search documentation</label>
        <input className="doc-search__input" id={id} ref={input} type="search" placeholder="Try callbacks, TTL, jobs…" value={query}
            onFocus={() => void fetchIndex()} onChange={event => { setQuery(event.target.value); void fetchIndex(); }} />
        {query.trim() && <div className="doc-search__results">
            <p className="doc-search__status" role="status">{status === 'error' ? 'Search unavailable. Use the navigation below.' : status !== 'ready' ? 'Loading search…' : matches.length ? `${matches.length} results` : 'No results. Try a shorter query.'}</p>
            <ul className="doc-search__list">{matches.map(entry => <li key={entry.url}>
                <Link className="doc-search__link" href={entry.url} onClick={() => setQuery('')}><strong>{entry.title}</strong><span className="doc-search__excerpt">{entry.excerpt}</span></Link>
            </li>)}</ul>
        </div>}
    </div>;
}
