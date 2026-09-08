import Head from 'next/head';
import Link from 'next/link';
import { useEffect, useRef } from 'react';
import Layout from './Layout';
import DocumentationSearch from './DocumentationSearch';
import TableOfContents from './TableOfContents';
import type { DocumentationPageProps } from '@/lib/documentation';
import { DOCUMENTATION_CATALOG, DOCUMENTATION_GROUPS, DOCUMENTATION_ORDER } from '@/lib/documentation-catalog';

export default function DocumentationPage({ id, contentHtml, title, description, headings }: DocumentationPageProps) {
    const article = useRef<HTMLElement>(null);
    const index = DOCUMENTATION_ORDER.indexOf(id);
    const previous = index > 0 ? DOCUMENTATION_CATALOG[DOCUMENTATION_ORDER[index - 1]] : undefined;
    const next = index >= 0 && index < DOCUMENTATION_ORDER.length - 1 ? DOCUMENTATION_CATALOG[DOCUMENTATION_ORDER[index + 1]] : undefined;
    const canonical = `https://slimfaas.dev${DOCUMENTATION_CATALOG[id].route}`;
    useEffect(() => {
        const cleanups: (() => void)[] = [];
        article.current?.querySelectorAll('pre').forEach(pre => {
            const code = pre.querySelector('code');
            if (!code) return;
            const button = document.createElement('button');
            button.type = 'button'; button.className = 'code-block__copy'; button.textContent = 'Copy';
            button.setAttribute('aria-label', 'Copy code to clipboard');
            const feedback = document.createElement('span');
            feedback.className = 'code-block__feedback'; feedback.setAttribute('role', 'status');
            const copy = async () => {
                try { await navigator.clipboard.writeText(code.textContent ?? ''); button.textContent = 'Copied'; feedback.textContent = 'Copied to clipboard'; }
                catch { button.textContent = 'Select to copy'; feedback.textContent = 'Clipboard unavailable. Select and copy the code.'; }
            };
            button.addEventListener('click', copy); pre.append(button, feedback);
            cleanups.push(() => { button.removeEventListener('click', copy); button.remove(); feedback.remove(); });
        });
        return () => cleanups.forEach(cleanup => cleanup());
    }, [contentHtml]);
    return <>
        <Head>
            <title>{`${title} | SlimFaas`}</title>
            <meta name="description" content={description} />
            <link rel="canonical" href={canonical} />
            <meta property="og:url" content={canonical} />
            <meta property="og:title" content={title} />
            <meta property="og:description" content={description} />
            <meta name="twitter:title" content={title} />
            <meta name="twitter:description" content={description} />
        </Head>
        <Layout>
            <div className="documentation">
                <aside className="documentation__sidebar">
                    <DocumentationSearch />
                    <nav className="doc-navigation doc-navigation--desktop" aria-label="Documentation">
                        {DOCUMENTATION_GROUPS.map(group => <section className="doc-navigation__group" key={group.label}>
                            <h2 className="doc-navigation__heading">{group.label}</h2>
                            {group.ids.map(pageId => <Link className={`doc-navigation__link${pageId === id ? ' doc-navigation__link--active' : ''}`} key={pageId} href={DOCUMENTATION_CATALOG[pageId].route} aria-current={pageId === id ? 'page' : undefined}>{DOCUMENTATION_CATALOG[pageId].label}</Link>)}
                        </section>)}
                    </nav>
                </aside>
                <main id="main-content" className="documentation__main" tabIndex={-1}>
                    <TableOfContents headings={headings} mobile />
                    <article className="doc-content" key={id} ref={article} dangerouslySetInnerHTML={{ __html: contentHtml }} />
                    <nav className="doc-pagination" aria-label="Continue reading">
                        {previous && <Link className="doc-pagination__link doc-pagination__link--previous" href={previous.route}><span className="doc-pagination__direction">← Previous</span>{previous.label}</Link>}
                        {next && <Link className="doc-pagination__link doc-pagination__link--next" href={next.route}><span className="doc-pagination__direction">Next →</span>{next.label}</Link>}
                    </nav>
                    <a className="documentation__edit" href={`https://github.com/SlimPlanet/SlimFaas/edit/main/${DOCUMENTATION_CATALOG[id].sourcePath}`}>Improve this page on GitHub ↗</a>
                </main>
                <TableOfContents headings={headings} />
            </div>
        </Layout>
    </>;
}
