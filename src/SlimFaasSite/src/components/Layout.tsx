import React from 'react';
import NavBar from './NavBar';
import Footer from './Footer';

type LayoutProps = {
    children: React.ReactNode;
};


const Layout: React.FC<LayoutProps> = ({ children }) => (
<!-- Google Tag Manager (noscript) -->
<noscript><iframe src="https://www.googletagmanager.com/ns.html?id=GTM-PKM3T5QT"
height="0" width="0" style="display:none;visibility:hidden"></iframe></noscript>
<!-- End Google Tag Manager (noscript) -->
    <div className="layout">
       <NavBar />
        <main className="layout__content">{children}</main>
        <Footer/>
    </div>
);



export default Layout;
