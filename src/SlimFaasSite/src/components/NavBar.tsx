import { useRef, useState } from 'react';
import Link from 'next/link';
import Image from 'next/image';
import { useRouter } from 'next/router';
import { DOCUMENTATION_CATALOG, DOCUMENTATION_GROUPS } from '@/lib/documentation-catalog';

export default function NavBar() {
    const [open, setOpen] = useState(false);
    const toggle = useRef<HTMLButtonElement>(null);
    const router = useRouter();
    return <header className="site-header">
        <a className="site-header__skip" href="#main-content">Skip to content</a>
        <Link className="site-header__brand" href="/" onClick={() => setOpen(false)}>
            <Image className="site-header__logo" src="/slimfaas.svg" width="36" height="36" alt="" /> SlimFaas
        </Link>
        <span className="site-header__tagline">Functions. Less infrastructure.</span>
        <Link className="site-header__start" href="/get-started">Get Started →</Link>
        <a className="site-header__link" href="https://github.com/SlimPlanet/SlimFaas">GitHub ↗</a>
        <button ref={toggle} className="site-header__toggle" type="button" aria-expanded={open} aria-controls="mobile-navigation" onClick={() => setOpen(!open)}>Menu</button>
        {open && <nav id="mobile-navigation" className="doc-navigation doc-navigation--mobile" aria-label="Documentation" onKeyDown={event => {
            if (event.key === 'Escape') { setOpen(false); toggle.current?.focus(); }
        }}>
            {DOCUMENTATION_GROUPS.map(group => <section className="doc-navigation__group" key={group.label}>
                <strong className="doc-navigation__heading">{group.label}</strong>
                {group.ids.map(id => <Link className={`doc-navigation__link${router.pathname === DOCUMENTATION_CATALOG[id].route ? ' doc-navigation__link--active' : ''}`} key={id} href={DOCUMENTATION_CATALOG[id].route}
                    aria-current={router.pathname === DOCUMENTATION_CATALOG[id].route ? 'page' : undefined}
                    onClick={() => setOpen(false)}>{DOCUMENTATION_CATALOG[id].label}</Link>)}
            </section>)}
        </nav>}
    </header>;
}
