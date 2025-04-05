import React from 'react';
import NavBar from './NavBar';
import Footer from './Footer';

type LayoutProps = {
    children: React.ReactNode;
};


const Layout: React.FC<LayoutProps> = ({ children }) => (
    <div className="layout">
       <NavBar />

        <main className="layout__content">{children}</main>
        <Footer/>
    </div>
);



export default Layout;
