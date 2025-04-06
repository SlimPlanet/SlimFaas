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
                    <img
                        src="https://www.cncf.io/wp-content/uploads/2022/07/cncf-stacked-color-bg.svg"
                        alt="CNCF Logo"
                        className="footer__cncf-logo"
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
                    © SlimFaas a Series of LF Projects, LLC. All rights reserved.
                </p>

            </div>
        </footer>
    );
};

export default Footer;
