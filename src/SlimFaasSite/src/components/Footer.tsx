// src/components/Footer.tsx
import Image from 'next/image';
import React from 'react';

const Footer: React.FC = () => {

    return (
        <footer className="footer">
            <div className="footer__container">

                <p className="footer__origin">
                    SlimFaas was originally created by AXA France.
                </p>

                <div className="footer__cncf">
                    <p className="footer__cncf-text">
                        We are a Cloud Native Computing Foundation sandbox project.
                    </p>

                    <Image
                        src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg"
                        alt="CNCF Logo"
                        className="footer__cncf-logo"
                        width={150}
                        height={150}
                        priority={false}
                        unoptimized
                    />
                </div>

                <p className="footer__kubecon">
                    Learn more about us at <a
                    href="https://events.linuxfoundation.org/kubecon-cloudnativecon/"
                    className="footer__kubecon-link"
                >
                    KubeCon + CloudNativeCon
                </a>.
                </p>

                <p className="footer__trademark">
                    The Linux Foundation® (TLF) has registered trademarks and uses trademarks.
                    For a list of TLF trademarks, see <a
                    href="https://www.linuxfoundation.org/trademark-usage"
                    className="footer__trademark-link"
                >
                    Trademark Usage
                </a>.
                </p>

                <p className="footer__copyright">
                    © SlimFaas a Series of LF Projects, LLC. All rights reserved. &nbsp;|&nbsp;
                    <a
                        href="#"
                        className="footer__trademark-link"
                        onClick={(e: React.MouseEvent<HTMLAnchorElement>) => {
                            e.preventDefault();
                            (window as unknown as { __sf_openConsent?: () => void }).__sf_openConsent?.();
                        }}
                    >
                        Cookie settings
                    </a>
                </p>

            </div>
        </footer>
    );
};

export default Footer;
