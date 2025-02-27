import React from 'react';
import Link from 'next/link';

type LayoutProps = {
    children: React.ReactNode;
};

const Layout: React.FC<LayoutProps> = ({ children }) => (
    <div className="layout">
        <nav className="layout__navbar">
            <ul className="navbar__list">
                <li className="navbar__item">
                    <Link href={`/`}>Home</Link>
                </li>
                <li className="navbar__item">
                    <Link href={`/about`}>About</Link>
                </li>
                <li className="navbar__item">
                    <Link href={`/contact`}>Contact</Link>
                </li>
            </ul>
        </nav>
        <main className="layout__content">{children}</main>
    </div>
);

export default Layout;
