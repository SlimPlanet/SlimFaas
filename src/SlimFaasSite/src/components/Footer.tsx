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
                        src="https://camo.githubusercontent.com/59d5ee1dc4a1d4559e5395065ce1352a883641a9331ec567566a28fee1b5efff/68747470733a2f2f7777772e636e63662e696f2f77702d636f6e74656e742f75706c6f6164732f323032322f30372f636e63662d737461636b65642d636f6c6f722d62672e737667"
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
