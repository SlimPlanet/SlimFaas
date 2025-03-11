import { useState } from "react";
import Link from "next/link";
import Image from "next/image";

const Navbar: React.FC = () => {
    const [isOpen, setIsOpen] = useState(false);

    return (
        <nav className="layout__navbar">
            <div className="navbar__left">
                {/* Logo */}
                <Link href="/">
                    <Image className="navbar__logo" src="/SlimFaas/slimfaas-white.svg"  alt="Logo" loading="lazy" width={64} height={64}   decoding="async"
                           data-nimg="1"  />
                </Link>

                {/* Bouton burger */}
                <button
                    className={`navbar__toggle ${isOpen ? "active" : ""}`}
                    onClick={() => setIsOpen(!isOpen)}
                    aria-label="Toggle menu"
                >
                    <span></span>
                    <span></span>
                    <span></span>
                </button>

                {/* Menu */}
                <ul className={`navbar__list ${isOpen ? "active" : ""}`}>
                    <li className="navbar__item">
                        <Link href="/">Home</Link>
                    </li>
                    <li className="navbar__item">
                        <Link href="/about">About</Link>
                    </li>
                    <li className="navbar__item">
                        <Link href="/contact">Contact</Link>
                    </li>
                </ul>
            </div>

            {/* Lien GitHub */}
            <a href="https://github.com/SlimPlanet/SlimFaas" className="navbar__github" target="_blank" rel="noopener noreferrer">
                <svg width="32" height="32" viewBox="0 0 24 24" fill="white" xmlns="http://www.w3.org/2000/svg">
                    <path d="M12 0.296875C5.37256 0.296875 0 5.66943 0 12.2969C0 17.6394 3.43848 22.146 8.20703 23.7744C8.80762 23.8848 9.02344 23.5156 9.02344 23.1982C9.02344 22.9277 9.0127 21.9941 9.00781 20.9102C5.67188 21.6572 4.96875 19.1738 4.96875 19.1738C4.4375 17.8193 3.64062 17.498 3.64062 17.498C2.54785 16.7939 3.72461 16.8105 3.72461 16.8105C4.93359 16.8896 5.55469 18.0332 5.55469 18.0332C6.625 19.9316 8.37207 19.4082 9.0625 19.1113C9.16797 18.3301 9.45898 17.7939 9.78906 17.4863C7.14062 17.1807 4.34766 16.1582 4.34766 11.6084C4.34766 10.3223 4.8125 9.22852 5.58984 8.36133C5.46094 8.05566 5.04883 6.8418 5.71094 5.16406C5.71094 5.16406 6.72559 4.83008 9.00488 6.44336C9.97852 6.18164 11.0078 6.05078 12.0371 6.04688C13.0664 6.05078 14.0957 6.18164 15.0693 6.44336C17.3486 4.83008 18.3623 5.16406 18.3623 5.16406C19.0244 6.8418 18.6123 8.05566 18.4824 8.36133C19.2598 9.22852 19.7246 10.3223 19.7246 11.6084C19.7246 16.168 16.9277 17.1768 14.2734 17.4746C14.6914 17.8525 15.0547 18.5742 15.0547 19.7012C15.0547 21.3076 15.0391 22.6631 15.0391 23.1982C15.0391 23.5156 15.2559 23.8896 15.8613 23.7744C20.625 22.146 24.0664 17.6394 24.0664 12.2969C24 5.66943 18.6274 0.296875 12 0.296875Z"/>
                </svg>
            </a>
        </nav>
    );
};

export default Navbar;
