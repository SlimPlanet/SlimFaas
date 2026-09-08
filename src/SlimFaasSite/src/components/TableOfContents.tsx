import type { DocumentationHeading } from '@/lib/markdown';

export default function TableOfContents({ headings, mobile = false }: { headings: DocumentationHeading[]; mobile?: boolean }) {
    if (!headings.length) return null;
    const links = <ul className="doc-toc__list">{headings.map(heading =>
        <li key={heading.id} className={`doc-toc__item${heading.level === 3 ? ' doc-toc__item--nested' : ''}`}>
            <a className="doc-toc__link" href={`#${heading.id}`}>{heading.text}</a>
        </li>
    )}</ul>;
    return mobile
        ? <details className="doc-toc doc-toc--mobile"><summary>On this page</summary>{links}</details>
        : <nav className="doc-toc doc-toc--desktop" aria-label="On this page"><strong>On this page</strong>{links}</nav>;
}
